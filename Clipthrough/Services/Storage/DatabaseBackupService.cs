using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services.Search;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

/// <summary>
/// Daily snapshot of the (potentially encrypted) clip database. After today's
/// near-miss with a torn migration, having an automatic point-in-time copy on
/// disk makes the difference between "we lost a day of clips" and "we lost
/// nothing — restore yesterday's backup".
///
/// Snapshots are written next to the live database in a sibling
/// <c>backups/</c> folder as <c>clipthrough-YYYYMMDD.db</c>. The file format
/// is just a copy of the encrypted SQLite file (after WAL checkpoint) so it
/// can be restored by simply replacing the live database.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    public const int DefaultRetention = 7;

    private readonly IStorageOptionsService _storageOptionsService;
    private readonly IClipboardMonitorService? _clipboardMonitor;
    private readonly IBackgroundOcrQueue? _ocrQueue;
    private readonly IEmbeddingWorker? _embeddingWorker;
    private readonly int _retention;

    /// <summary>
    /// Primary constructor used by the DI container. Worker services are
    /// injected so restore operations can quiesce them via
    /// <see cref="DatabaseMaintenanceScope"/>.
    /// </summary>
    public DatabaseBackupService(
        IStorageOptionsService storageOptionsService,
        IClipboardMonitorService? clipboardMonitor,
        IBackgroundOcrQueue? ocrQueue,
        IEmbeddingWorker? embeddingWorker,
        int retention = DefaultRetention)
    {
        _storageOptionsService = storageOptionsService;
        _clipboardMonitor = clipboardMonitor;
        _ocrQueue = ocrQueue;
        _embeddingWorker = embeddingWorker;
        _retention = retention < 1 ? 1 : retention;
    }

    public async Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default)
    {
        var dbPath = _storageOptionsService.Current.DatabasePath;
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            return;
        }

        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
        var todayPath = Path.Combine(backupDir, $"clipthrough-{stamp}.db");

        if (File.Exists(todayPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(backupDir);

            // Read the snapshot through SQLite rather than checkpointing and
            // copying the file, so commits still living in the WAL are included
            // even while other connections are reading or writing.
            var tempPath = todayPath + ".tmp";
            await Task.Run(
                () => SqliteDatabaseCopier.CopyDatabase(dbPath, _storageOptionsService.Current.DatabasePassword, tempPath),
                cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, todayPath, overwrite: true);
            Trace.TraceInformation($"Database backup written: {todayPath}");

            PruneOldBackups(backupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Trace.TraceWarning($"Database backup failed: {ex.Message}");
        }
    }

    private static void ApplyBusyTimeoutOnOpen(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState == ConnectionState.Open && sender is SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
        }
    }

    private void PruneOldBackups(string backupDir)
    {
        try
        {
            var files = Directory.EnumerateFiles(backupDir, "clipthrough-*.db")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_retention)
                .ToArray();

            foreach (var f in files)
            {
                try
                {
                    f.Delete();
                    Trace.TraceInformation($"Pruned old backup: {f.Name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"Could not prune backup '{f.Name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Backup pruning failed: {ex.Message}");
        }
    }

    public System.Collections.Generic.IReadOnlyList<DatabaseBackupInfo> ListBackups()
    {
        var dbPath = _storageOptionsService.Current.DatabasePath;
        if (string.IsNullOrEmpty(dbPath))
        {
            return System.Array.Empty<DatabaseBackupInfo>();
        }

        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        if (!Directory.Exists(backupDir))
        {
            return System.Array.Empty<DatabaseBackupInfo>();
        }

        try
        {
            return Directory.EnumerateFiles(backupDir, "clipthrough-*.db")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new DatabaseBackupInfo(f.FullName, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Length))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Listing backups failed: {ex.Message}");
            return System.Array.Empty<DatabaseBackupInfo>();
        }
    }

    /// <summary>
    /// Restores the given backup over the live database.
    ///
    /// Pool-safe sequence (U7):
    ///   1. Enter <see cref="DatabaseMaintenanceScope"/> — stops workers,
    ///      clears the connection pool so no handle is open on the live DB.
    ///   2. Validate the backup opens (is a readable SQLite file) before
    ///      touching the live DB.
    ///   3. Rename live .db/.db-wal/.db-shm to .before-restore-{stamp}.
    ///   4. Copy backup → temp + atomic rename to live path.
    ///   5. Validate the restored DB opens before returning.
    ///
    /// If anything from step 3 onwards fails the stashed files are moved back,
    /// so a failed restore leaves the user exactly where they started rather
    /// than with no database at all.
    /// </summary>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file not found.", backupPath);
        }

        var dbPath = _storageOptionsService.Current.DatabasePath;
        if (string.IsNullOrEmpty(dbPath))
        {
            throw new InvalidOperationException("No database path is configured.");
        }

        // Step 1: Enter maintenance scope — stops workers and clears the pool
        // so no pooled connection holds the live file open during the swap.
        await using var scope = await DatabaseMaintenanceScope.EnterAsync(
            _clipboardMonitor, _ocrQueue, _embeddingWorker);

        await Task.Run(() => RestoreCore(backupPath, dbPath), cancellationToken).ConfigureAwait(false);
    }

    private void RestoreCore(string backupPath, string dbPath)
    {
        // Step 2: Validate backup is readable before touching the live DB.
        ValidateBackupReadable(backupPath, _storageOptionsService.Current.DatabasePassword);

        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var stashed = new System.Collections.Generic.List<(string Live, string Stashed)>();

        try
        {
            // Step 3: Stash whatever's currently live so the operation is reversible.
            // The -wal and -shm files have to move with the main file: leaving them
            // beside a *different* database would let SQLite replay stale frames
            // into it.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var live = dbPath + suffix;
                if (File.Exists(live))
                {
                    var stash = $"{live}.before-restore-{stamp}";
                    File.Move(live, stash);
                    stashed.Add((live, stash));
                }
            }

            // Step 4: Copy via a temp file + atomic rename so the restore is also
            // crash-safe: if we die between Copy and Move, the live path is still
            // empty and the user re-runs the restore.
            var tempPath = dbPath + ".restoring";
            // overwrite: a prior attempt that died between the copy and the move would
            // otherwise leave a stale .restoring file, and File.Copy throws when the
            // destination exists — silently blocking every future restore.
            File.Copy(backupPath, tempPath, overwrite: true);
            try
            {
                File.Move(tempPath, dbPath);
            }
            catch
            {
                // The swap into the live path failed; remove the temp so the next
                // restore attempt starts clean instead of tripping over a stray file.
                try { File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }

            // Step 5: Validate the restored DB opens with the stored password.
            // ValidateBackupReadable accepts a backup that opens with *either* the
            // live password or none, so an unencrypted backup reaches this point
            // and fails here when the live database is encrypted.
            var password = _storageOptionsService.Current.DatabasePassword;
            if (!StorageOptionsService.CanOpenWithPassword(dbPath, password ?? string.Empty))
            {
                throw new InvalidOperationException(
                    "The restored database does not open with the current password. " +
                    "The previous database has been put back unchanged.");
            }
        }
        catch
        {
            RollBackRestore(stashed);
            throw;
        }

        Trace.TraceInformation($"Restored database from backup '{backupPath}'. Previous live files renamed with suffix '.before-restore-{stamp}'.");
    }

    /// <summary>
    /// Puts the pre-restore files back after a failed restore.
    ///
    /// Without this, every failure from the stash onwards left the live path
    /// empty: the previous database was still on disk, but under a
    /// <c>.before-restore-*</c> name that the application does not look for, so
    /// the user's history simply disappeared and only a manual rename brought it
    /// back. A restore that fails must be a no-op.
    /// </summary>
    private static void RollBackRestore(
        System.Collections.Generic.IReadOnlyList<(string Live, string Stashed)> stashed)
    {
        foreach (var (live, stash) in stashed)
        {
            try
            {
                // Whatever the failed attempt left at the live path has to go first:
                // File.Move refuses an existing destination, and a half-restored
                // file is worthless next to the original we are putting back.
                if (File.Exists(live))
                {
                    File.Delete(live);
                }

                File.Move(stash, live);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never mask the failure that started the rollback. Say plainly
                // where the data still is.
                Trace.TraceError(
                    $"Restore rollback could not put '{stash}' back as '{live}': {ex.Message}. " +
                    "The pre-restore database is still on disk under that name.");
            }
        }
    }

    /// <summary>
    /// Validates that <paramref name="backupPath"/> is a readable SQLite file.
    /// Throws <see cref="InvalidOperationException"/> if the file is unreadable
    /// or corrupt, so <see cref="RestoreCore"/> can abort before touching the
    /// live database.
    /// </summary>
    private static void ValidateBackupReadable(string backupPath, string? password)
    {
        // A backup that requires the same password as the live DB is fine.
        // An unencrypted backup is also fine (password="" opens it).
        bool opens = StorageOptionsService.CanOpenWithPassword(backupPath, password ?? string.Empty);
        if (!opens)
        {
            // Try without password in case the backup is unencrypted.
            opens = StorageOptionsService.CanOpenWithPassword(backupPath, string.Empty);
        }

        if (!opens)
        {
            throw new InvalidOperationException(
                $"The backup file '{Path.GetFileName(backupPath)}' is unreadable or requires a different password.");
        }
    }
}
