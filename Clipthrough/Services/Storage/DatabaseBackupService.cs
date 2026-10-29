using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
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
/// is a consistent copy of the (encrypted) database read through SQLite by
/// <see cref="SqliteDatabaseCopier"/>, so commits still in the WAL are included
/// while the app keeps running, and it can be restored by replacing the live
/// database.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    public const int DefaultRetention = 7;

    private readonly IStorageOptionsService _storageOptionsService;
    private readonly IClipboardMonitorService? _clipboardMonitor;
    private readonly IBackgroundOcrQueue? _ocrQueue;
    private readonly IEmbeddingWorker? _embeddingWorker;
    private readonly ISettingsService? _settingsService;
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
        int retention = DefaultRetention,
        ISettingsService? settingsService = null)
    {
        _storageOptionsService = storageOptionsService;
        _clipboardMonitor = clipboardMonitor;
        _ocrQueue = ocrQueue;
        _embeddingWorker = embeddingWorker;
        _settingsService = settingsService;
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
        // Invariant: the stamp is part of the filename and backup retention orders
        // by it. A non-Gregorian calendar culture (th-TH, ar-SA) would emit a
        // Buddhist or Hijri year here, so stamps written under different locales
        // would not sort against each other.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var todayPath = Path.Combine(backupDir, $"{BackupNamePrefix}{stamp}{BackupNameSuffix}");

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
            var tempPath = todayPath + TempSuffix;
            DeleteStrandedTempCopies(backupDir, tempPath);
            try
            {
                await Task.Run(
                    () =>
                    {
                        var password = _storageOptionsService.Current.DatabasePassword;
                        SqliteDatabaseCopier.CopyDatabase(dbPath, password, tempPath);
                        // Owner decision on OD-4a: the purge serves the lifetime
                        // promise. With the lifetime off sensitive clips are kept
                        // like any other, so a restored backup brings them back.
                        if (_settingsService?.Current.EnableSensitiveClipLifetime == true)
                        {
                            PurgeSensitiveClips(tempPath, password);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, todayPath, overwrite: true);
            }
            finally
            {
                // On any failure the temp copy may hold clips the purge never
                // removed, and retention only prunes finished backups.
                DeleteTempCopy(tempPath);
            }

            Trace.TraceInformation($"Database backup written: {todayPath}");

            PruneOldBackups(backupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Trace.TraceWarning($"Database backup failed: {ex.Message}");
        }
    }

    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Removes a temp copy and any journal beside it. Best effort: a copy that
    /// cannot be removed now is swept by a later backup.
    /// </summary>
    private static void DeleteTempCopy(string tempPath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(tempPath + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not remove the backup temp copy '{Path.GetFileName(tempPath + suffix)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sweeps temp copies a crash left behind on earlier days. Each day writes
    /// its own temp name, so nothing else would ever overwrite or prune them,
    /// and one that died mid-purge still holds sensitive clips. Today's name is
    /// skipped: this run overwrites it.
    /// </summary>
    private static void DeleteStrandedTempCopies(string backupDir, string currentTempPath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(backupDir, BackupNamePattern + TempSuffix + "*"))
            {
                if (path.StartsWith(currentTempPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    Trace.TraceInformation($"Removed a stranded backup temp copy: {Path.GetFileName(path)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"Could not remove the stranded backup temp copy '{Path.GetFileName(path)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not list stranded backup temp copies: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes sensitive clips - with their full-text entries, vectors and
    /// rule matches - from a backup copy before it is kept (F-19, OD-4a).
    /// Called only while the sensitive-clip lifetime is on.
    ///
    /// A backup is retained for a week, so a sensitive clip copied into one
    /// outlived its lifetime there no matter when the live library expired it.
    /// With the lifetime on, restoring a backup therefore brings back no
    /// sensitive clips; that is the owner's choice recorded as OD-4a.
    ///
    /// Deleting the rows is not enough on its own: their bytes stay in free
    /// pages and the full-text index keeps the original segments until a merge.
    /// So the index is optimized and the file vacuumed on every purge, including
    /// one that removes nothing: live expiry may have deleted a sensitive clip
    /// before the copy, leaving the same residue. A copy whose schema has no
    /// <c>is_sensitive</c> column is left alone.
    /// </summary>
    internal static int PurgeSensitiveClips(string path, string? password)
    {
        // Through the factory (U-G1/U-C9): the cipher recipe and busy_timeout come with it.
        // The schema's ON DELETE CASCADE removes the vectors and rule matches; SQLite
        // only honours it with foreign keys on.
        using var connection = Clipthrough.Database.SqliteConnectionFactory.Create(path, password, SqliteOpenMode.ReadWrite, pooling: false, foreignKeys: true);
        connection.Open();

        if (!HasColumn(connection, "clips", "is_sensitive"))
        {
            return 0;
        }

        var removed = Execute(connection, "DELETE FROM clips WHERE is_sensitive = 1;");

        // Not only when this purge removed rows: live expiry may already have
        // deleted a sensitive clip, and the copy carries its index segments
        // all the same (rubber-duck F-06-3).
        if (HasTable(connection, "clips_fts"))
        {
            Execute(connection, "INSERT INTO clips_fts(clips_fts) VALUES('optimize');");
        }

        Execute(connection, "VACUUM;");
        if (removed > 0)
        {
            Trace.TraceInformation($"Removed {removed} sensitive clip(s) from the backup copy.");
        }

        return removed;
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name = $name AND type = 'table';";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }


    private const string BackupNamePrefix = "clipthrough-";
    private const string BackupNameSuffix = ".db";
    private const string BackupNamePattern = BackupNamePrefix + "*" + BackupNameSuffix;

    /// <summary>
    /// Re-keys every backup beside <paramref name="dbPath"/> after the live
    /// database moved from <paramref name="previousKey"/> to
    /// <paramref name="newKey"/>, so each restore point Settings lists still
    /// restores. Restore requires a backup to open with the live key; before
    /// this, every backup taken before a rekey - or before encryption was
    /// turned on - was listed and could not be restored (F-15). Turning
    /// encryption on also no longer leaves the history in plaintext beside it.
    /// </summary>
    /// <remarks>
    /// Each backup is rewritten to a temp file, verified, then renamed over the
    /// original, so a failure leaves that backup as it was. Best effort per
    /// file: the live rekey has already succeeded and must not be undone by a
    /// backup that cannot be read. A backup that opens with neither key - an
    /// older key from before an earlier rekey - is left alone and traced.
    /// Synchronous and slow (a key derivation per open, a full copy per file):
    /// call it off the UI thread.
    /// </remarks>
    internal static void RekeyBackups(string dbPath, string previousKey, string newKey)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        if (!Directory.Exists(backupDir))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(backupDir, BackupNamePattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not list backups to re-key: {ex.Message}");
            return;
        }

        foreach (var backup in files)
        {
            var tempPath = backup + ".rekeying-" + Guid.NewGuid().ToString("N");
            try
            {
                if (StorageOptionsService.CanOpenWithPassword(backup, newKey))
                {
                    continue;
                }

                // A backup the live database could restore before the rekey
                // opens with the previous key, or - per ValidateBackupReadable -
                // with none at all.
                string? sourceKey = StorageOptionsService.CanOpenWithPassword(backup, previousKey) ? previousKey
                    : previousKey.Length > 0 && StorageOptionsService.CanOpenWithPassword(backup, string.Empty) ? string.Empty
                    : null;
                if (sourceKey is null)
                {
                    Trace.TraceWarning($"Backup '{Path.GetFileName(backup)}' opens with neither the previous nor the new key; left as it is.");
                    continue;
                }

                StorageOptionsService.WriteRekeyedCopy(backup, sourceKey, newKey, tempPath);
                if (!StorageOptionsService.CanOpenWithPassword(tempPath, newKey))
                {
                    throw new InvalidOperationException("the re-keyed copy does not open with the new key");
                }

                // CanOpenWithPassword pools its connection; a pooled handle on
                // the backup would make the rename below fail.
                SqliteConnection.ClearAllPools();
                File.Move(tempPath, backup, overwrite: true);
                Trace.TraceInformation($"Backup '{Path.GetFileName(backup)}' re-keyed to match the database.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                Trace.TraceWarning($"Could not re-key backup '{Path.GetFileName(backup)}': {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>
    /// The day a backup covers, taken from its name.
    ///
    /// <see cref="EnsureDailyBackupAsync"/> already treats the file name as the
    /// identity of a backup — it decides whether today is already covered purely
    /// by whether <c>clipthrough-{today}.db</c> exists, never by reading a
    /// timestamp. Readers have to use the same identity, or writer and reader
    /// disagree about what a given file is.
    ///
    /// The last-write time is not that identity. It is ordinary filesystem
    /// metadata: any tool that moves the backups folder without preserving it
    /// (robocopy without <c>/COPY:DAT</c>, rsync without <c>-t</c>, several
    /// cloud sync clients) rewrites every stamp to the moment of the sync, and
    /// with it the order in which these files get deleted.
    /// </summary>
    private static DateTime? ParseBackupDay(string fileName)
    {
        if (!fileName.StartsWith(BackupNamePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(BackupNameSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var stamp = fileName[BackupNamePrefix.Length..^BackupNameSuffix.Length];
        return DateTime.TryParseExact(
            stamp,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var day)
            ? day
            : null;
    }

    /// <summary>
    /// Newest first, by the day in the name; the last-write time only breaks
    /// ties within a day and stands in for names this version did not write.
    /// Shared so pruning and listing cannot drift into different orders.
    /// </summary>
    private static (DateTime Day, DateTime Written) BackupOrderKey(FileInfo file)
    {
        var written = file.LastWriteTimeUtc;
        return (ParseBackupDay(file.Name) ?? written.Date, written);
    }

    private void PruneOldBackups(string backupDir)
    {
        try
        {
            var files = Directory.EnumerateFiles(backupDir, BackupNamePattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(BackupOrderKey)
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
            return Directory.EnumerateFiles(backupDir, BackupNamePattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(BackupOrderKey)
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

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
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
