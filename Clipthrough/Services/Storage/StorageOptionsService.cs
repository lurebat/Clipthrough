using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Clipthrough.Services;

public sealed class StorageOptionsService : IStorageOptionsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _configPath;
    private readonly IDataProtectionService _dataProtection;
    private readonly IServiceProvider? _services;

    /// <summary>
    /// Primary constructor used by the DI container. The service provider is
    /// injected (instead of the worker services directly) so lifecycle
    /// operations can resolve the workers lazily at call time. This avoids a
    /// constructor cycle: StorageOptionsService -> workers -> ClipStoreService
    /// -> SqliteConnectionFactory -> StorageOptionsService.
    /// </summary>
    public StorageOptionsService(IDataProtectionService dataProtection, IServiceProvider services)
        : this(
            dataProtection,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipthrough",
                "storage.json"),
            services)
    {
    }

    // Test-only seam: allow the config path to be overridden so tests don't
    // pollute the user's real storage.json. No service provider — the quiesce
    // scope degrades to pool-clear only (safe in tests).
    public StorageOptionsService(IDataProtectionService dataProtection, string configPath)
        : this(dataProtection, configPath, null)
    {
    }

    internal StorageOptionsService(
        IDataProtectionService dataProtection,
        string configPath,
        IServiceProvider? services)
    {
        _dataProtection = dataProtection;
        _configPath = configPath;
        _services = services;

        Current = LoadFromDisk();
    }

    // Resolves the live worker services (when running under DI) so the
    // maintenance scope can quiesce them; null in test contexts.
    private Task<DatabaseMaintenanceScope> EnterMaintenanceScopeAsync()
        => DatabaseMaintenanceScope.EnterAsync(
            _services?.GetService<IClipboardMonitorService>(),
            _services?.GetService<IBackgroundOcrQueue>(),
            _services?.GetService<IEmbeddingWorker>());

    public StorageOptions Current { get; private set; }

    public bool HasSavedConfig => File.Exists(_configPath);

    /// <inheritdoc />
    public string? LoadFault { get; private set; }

    public bool DatabaseExists => File.Exists(Current.DatabasePath);

    /// <summary>
    /// Returns true when the database file at <paramref name="dbPath"/> is encrypted
    /// and requires a password to open.
    /// </summary>
    public static bool RequiresPassword(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.StateChange += ApplyBusyTimeoutOnOpen;
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns true when the database at <paramref name="dbPath"/> can be
    /// opened and read using <paramref name="password"/>. Used at startup to
    /// validate a persisted "Remember password" entry before skipping the
    /// unlock prompt.
    /// </summary>
    private const int SqliteCantOpen = 14;

    public static bool CanOpenWithPassword(string dbPath, string password)
        => TryOpenWithPassword(dbPath, password) is null;

    /// <inheritdoc />
    public Task<SqliteException?> TryOpenWithPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        var dbPath = Current.DatabasePath;
        return Task.Run(() => TryOpenWithPassword(dbPath, password), cancellationToken);
    }

    /// <summary>
    /// Opens the database with <paramref name="password"/> and returns the failure,
    /// or <c>null</c> when it opened. Callers that need to tell a wrong password
    /// apart from a missing file or a disk error need the exception, not a bool.
    ///
    /// Synchronous and slow by nature: SQLCipher derives the key on open, which
    /// measures ~530ms on this machine and does not get cheaper with a smaller
    /// database. Anything on the UI thread must go through
    /// <see cref="TryOpenWithPasswordAsync"/> instead - note that awaiting
    /// SqliteConnection.OpenAsync is not an alternative, because it does the whole
    /// derivation on the calling thread and hands back an already-completed task.
    /// </summary>
    private static SqliteException? TryOpenWithPassword(string dbPath, string password)
    {
        if (!File.Exists(dbPath))
        {
            // The same code SQLite itself reports for a file it cannot open, so
            // callers classifying the failure do not need a special case.
            return new SqliteException($"Database file not found: {dbPath}", SqliteCantOpen);
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Password = password,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.StateChange += ApplyBusyTimeoutOnOpen;
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return null;
        }
        catch (SqliteException ex)
        {
            return ex;
        }
    }

    public async Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = options.Normalize();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Current;
            // Path move copies the DB (inside a maintenance scope).
            // Same-path password edits are validated against the actual DB key
            // but do NOT trigger rekey — use RekeyAsync to re-encrypt.
            // The path-move maintenance scope must outlive the Current flip: it
            // restarts the workers on dispose, and they reopen Current — which must
            // already be the new path, or they recreate an empty DB at the deleted
            // old path and every later clip is lost. Null for non-move saves.
            await using var scope = await ApplyStorageChangesAsync(previous, normalized, cancellationToken);

            // A non-null scope means the database file has moved and the source
            // is already deleted, so the file system is the source of truth and
            // Current has to agree with it before anything else can fail. If
            // persisting threw while Current still named the old path, the scope
            // would restart the workers there, they would find nothing and
            // create an empty database, and every later clip would go into it
            // while the real history sat orphaned at the new path. Persisting
            // still throws, so the caller learns the config on disk is stale.
            //
            // Saves that did not move anything keep the original ordering: there
            // is nothing on disk to agree with, so a failed write should leave
            // the in-memory state untouched rather than diverging from it.
            if (scope is not null)
            {
                Current = normalized;
            }

            await SaveToDiskAsync(normalized, cancellationToken);
            Current = normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DatabaseMaintenanceScope?> ApplyStorageChangesAsync(StorageOptions previous, StorageOptions next, CancellationToken cancellationToken)
    {
        var oldPath = previous.DatabasePath;
        var newPath = next.DatabasePath;

        if (!File.Exists(oldPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            return null;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same path. Validate that the new password actually opens the DB
            // before persisting to storage.json — a metadata-only write with a
            // divergent key would lock the user out on next launch.
            bool passwordChanged = !string.Equals(
                previous.DatabasePassword,
                next.DatabasePassword,
                StringComparison.Ordinal);

            if (passwordChanged && File.Exists(newPath))
            {
                if (!CanOpenWithPassword(newPath, next.DatabasePassword ?? string.Empty))
                {
                    throw new InvalidOperationException(
                        "The database does not open with the new password. " +
                        "Use 'Re-encrypt database…' to change the encryption key.");
                }
            }

            // Password edits no longer trigger rekey here — that's
            // explicit through RekeyAsync.
            return null;
        }

        // --- Atomic path move (U6) ---
        // Quiesce workers so no clip is written to the old path between the
        // snapshot and the Current flip; clear the pool so no pooled connection
        // holds the source file open. The scope is returned to the caller, which
        // disposes it only AFTER flipping Current to the new path — otherwise the
        // restarted workers reopen Current (still the old, now-deleted path) and
        // recreate an empty DB there, losing every subsequent clip.
        var scope = await EnterMaintenanceScopeAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

            // Release pooled handles on the source before reading it.
            SqliteConnection.ClearAllPools();

            // Copy source → temp (in the destination directory for same-volume rename).
            // Read through SQLite, not the file system: this copy becomes the
            // database and the source is deleted immediately afterwards, so a
            // copy missing WAL-resident commits would lose them permanently.
            var tempPath = Path.Combine(Path.GetDirectoryName(newPath)!, Path.GetFileName(newPath) + ".moving-" + Guid.NewGuid().ToString("N"));
            SqliteDatabaseCopier.CopyDatabase(oldPath, previous.DatabasePassword, tempPath);

            var stashed = new System.Collections.Generic.List<(string Live, string Stashed)>();
            try
            {
                // Whatever already sits at the target - a database that was not
                // shut down cleanly, a cloud-synced copy - is set aside as a set:
                // main file, -wal and -shm under one stamp. Renaming only the
                // main file over it would leave its -wal beside the moved
                // library, and SQLite replays that foreign WAL on the next open,
                // replacing the user's history with the old target's. The same
                // hazard RestoreCore guards against.
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var existing = newPath + suffix;
                    if (File.Exists(existing))
                    {
                        var safeguard = existing + ".before-move-" + stamp;
                        File.Move(existing, safeguard);
                        stashed.Add((existing, safeguard));
                        Trace.TraceInformation($"Existing target file '{existing}' set aside as '{safeguard}'.");
                    }
                }

                // Atomic rename: temp → newPath. Nothing is left at newPath now,
                // but overwrite keeps a racing stray file from failing the move.
                File.Move(tempPath, newPath, overwrite: true);
                tempPath = null; // newPath owns it now — don't delete in finally.
                stashed.Clear(); // The move landed; the set-aside files stay as the safeguard.

                // Remove the source and its WAL/SHM sidecar files.
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var src = oldPath + suffix;
                    if (File.Exists(src))
                    {
                        try { File.Delete(src); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Trace.TraceWarning($"Could not remove source file '{src}': {ex.Message}");
                        }
                    }
                }

                Trace.TraceInformation($"Database moved from '{oldPath}' to '{newPath}'.");
            }
            finally
            {
                // Clean up the temp copy if the rename failed.
                if (tempPath is not null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }

                // ...and put the displaced target back where it was, so a failed
                // move changes nothing at either end.
                foreach (var (live, safeguard) in stashed)
                {
                    try
                    {
                        if (!File.Exists(live))
                        {
                            File.Move(safeguard, live);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Trace.TraceError($"Could not put '{safeguard}' back as '{live}' after a failed move: {ex.Message}");
                    }
                }
            }
        }
        catch
        {
            // The move failed before Current was flipped; restart the workers on
            // the original path (still intact) so the app stays functional.
            await scope.DisposeAsync();
            throw;
        }

        return scope;
    }

    /// <summary>
    /// Re-encrypts the database in place using a copy-then-swap strategy so a
    /// failure at any step leaves the original database intact and openable.
    ///
    /// Sequence:
    ///   1. Verify current password opens the DB.
    ///   2. Enter maintenance scope (stop workers, clear pool).
    ///   3-4. Write a copy keyed with the new password (<see cref="WriteRekeyedCopy"/>),
    ///      read through SQLite so commits still in the WAL travel with it.
    ///   5. Verify the copy opens with the new password.
    ///   6. Atomic rename: temp → live DB path (DB file is the source of truth).
    ///   7. Update Current (key for the session + workers restarted on scope dispose).
    ///   8. Persist storage.json with the new password.
    ///
    /// If any step fails the original DB is untouched; the temp copy is cleaned up.
    /// </summary>
    public async Task RekeyAsync(string currentPassword, string newPassword, bool rememberNewPassword, CancellationToken cancellationToken = default)
    {
        // Both passwords go through the rule every unlock applies. Keying the
        // file with the raw value - say, a pasted passphrase ending in a space -
        // and then running the session on the trimmed one locked the user out
        // of their whole history for good (F-02). The dialog refuses edge
        // whitespace up front (S42); this keeps the service consistent with it
        // for every other caller.
        var currentKey = StorageOptions.NormalizePassword(currentPassword);
        var newKey = StorageOptions.NormalizePassword(newPassword);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            var dbPath = current.DatabasePath;

            if (!File.Exists(dbPath))
            {
                throw new InvalidOperationException("No database file found to re-encrypt.");
            }

            // Step 1: Verify the current password opens the DB before touching anything.
            var openWith = current with { DatabasePassword = currentKey };
            try
            {
                await using var probe = OpenConnection(openWith);
                await probe.OpenAsync(cancellationToken);
                await using var verify = probe.CreateCommand();
                verify.CommandText = "SELECT count(*) FROM sqlite_master;";
                await verify.ExecuteScalarAsync(cancellationToken);
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException("Current password is incorrect.", ex);
            }

            // Step 2: Enter maintenance scope (stop workers, clear pool).
            await using var scope = await EnterMaintenanceScopeAsync();

            // Steps 3 and 4: write a copy of the DB keyed with the new password.
            // The copy is renamed over the live database, so it has to be
            // complete - see WriteRekeyedCopy.
            SqliteConnection.ClearAllPools();
            string? tempPath = dbPath + ".rekeying-" + Guid.NewGuid().ToString("N");

            try
            {
                WriteRekeyedCopy(dbPath, currentKey, newKey, tempPath);

                // Step 5: Verify the rekeyed copy opens with the key the session
                // will use from here on - not merely with what was passed in.
                if (!CanOpenWithPassword(tempPath, newKey))
                {
                    throw new InvalidOperationException(
                        "Rekey verification failed: the rekeyed copy does not open with the new password.");
                }

                // Step 6: Atomic swap — the rekeyed copy (verified in step 5 to
                // open with the new password) becomes the live database. Swap the
                // file BEFORE persisting the password: the DB file is the source of
                // truth for which key is required. A crash here leaves a freshly
                // rekeyed DB with storage.json still on the old password, so the next
                // launch falls back to the unlock prompt — which accepts the new
                // password the user just chose. Persisting first would instead point
                // storage.json at a key the still-old DB rejects, locking them out.
                // Removing encryption has no prompt to fall back on; LoadFromDisk
                // drops a remembered key the plaintext file does not need.
                SqliteConnection.ClearAllPools();
                File.Move(tempPath, dbPath, overwrite: true);
                tempPath = null; // dbPath owns it now.

                var updated = (current with
                {
                    DatabasePassword = newKey,
                    RememberPassword = rememberNewPassword,
                }).Normalize();

                // Step 7: Update in-memory state so the running session (and the
                // workers restarted on scope dispose) use the new key.
                Current = updated;

                // Step 7b: Bring the backups along, or every restore point taken
                // before today stops restoring (F-15). Per-file best effort, and
                // not cancellable: the live file is already rekeyed.
                await Task.Run(() => DatabaseBackupService.RekeyBackups(dbPath, currentKey, newKey), CancellationToken.None);

                // Step 8: Persist the remembered password last. If this throws the
                // DB is already rekeyed and Current is correct for this session.
                await SaveToDiskAsync(updated, cancellationToken);

                Trace.TraceInformation($"Database rekeyed successfully.");
            }
            finally
            {
                // Clean up the temp copy on any failure path.
                if (tempPath is not null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes a copy of <paramref name="sourcePath"/>, which opens with
    /// <paramref name="currentKey"/>, to <paramref name="destinationPath"/>
    /// keyed with <paramref name="newKey"/>. An empty key means unencrypted.
    /// The source is only read. A partial destination is removed on failure.
    /// </summary>
    /// <remarks>
    /// Between two keys this copies through the online backup API and applies
    /// <c>PRAGMA rekey</c> to the copy. That pragma cannot add encryption to a
    /// plaintext file or strip it from an encrypted one - on SQLCipher 4.5 it
    /// is a silent no-op there, so "Re-encrypt database" failed verification
    /// for every unencrypted library (F-16). When either side is empty the
    /// copy is written with <c>sqlcipher_export</c> into a database attached
    /// with the target key instead. Like VACUUM it carries the schema, every
    /// row, the FTS5 shadow tables and triggers; <c>user_version</c> and the
    /// journal mode are not carried, so both are set to match the source.
    /// </remarks>
    internal static void WriteRekeyedCopy(string sourcePath, string currentKey, string newKey, string destinationPath)
    {
        if (currentKey.Length == 0 || newKey.Length == 0)
        {
            File.Delete(destinationPath);
            try
            {
                // ReadWriteCreate: ATTACH inherits the open flags, and it has to
                // create the destination. The source exists (checked by callers).
                using var source = OpenUnpooled(sourcePath, currentKey, SqliteOpenMode.ReadWriteCreate);
                string journalMode;
                using (var mode = source.CreateCommand())
                {
                    mode.CommandText = "PRAGMA main.journal_mode;";
                    journalMode = Convert.ToString(mode.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
                }

                // sqlcipher_export does not carry user_version on this build, so
                // the copy would read 0 unless it is set explicitly.
                long userVersion;
                using (var version = source.CreateCommand())
                {
                    version.CommandText = "PRAGMA main.user_version;";
                    userVersion = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using var export = source.CreateCommand();
                export.CommandText =
                    "ATTACH DATABASE $path AS rekeyed KEY $key; " +
                    "SELECT sqlcipher_export('rekeyed'); " +
                    "PRAGMA rekeyed.user_version = " + userVersion.ToString(CultureInfo.InvariantCulture) + "; " +
                    (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase) ? "PRAGMA rekeyed.journal_mode = WAL; " : string.Empty) +
                    "DETACH DATABASE rekeyed;";
                export.Parameters.AddWithValue("$path", destinationPath);
                export.Parameters.AddWithValue("$key", newKey);
                export.ExecuteNonQuery();
            }
            catch
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
                {
                    try { File.Delete(destinationPath + suffix); } catch { /* best effort */ }
                }

                throw;
            }

            return;
        }

        SqliteDatabaseCopier.CopyDatabase(sourcePath, currentKey, destinationPath);
        using var copy = OpenUnpooled(destinationPath, currentKey, SqliteOpenMode.ReadWrite);
        using var rekey = copy.CreateCommand();
        rekey.CommandText = $"PRAGMA rekey = '{EscapeSqlLiteral(newKey)}';";
        rekey.ExecuteNonQuery();
    }

    private static SqliteConnection OpenUnpooled(string path, string key, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // Unpooled so the file is released on Dispose and can be renamed.
            Pooling = false,
        };
        if (key.Length > 0)
        {
            builder.Password = key;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += ApplyBusyTimeoutOnOpen;
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }
    private SqliteConnection OpenConnection(StorageOptions options)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            // Private cache (the default) is required: shared cache surfaces
            // in-process write contention as SQLITE_LOCKED, which busy_timeout
            // does NOT retry. Private cache surfaces it as SQLITE_BUSY, which
            // the 5-second busy_timeout below DOES retry.
        };

        if (!string.IsNullOrWhiteSpace(options.DatabasePassword))
        {
            builder.Password = options.DatabasePassword;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += ApplyBusyTimeoutOnOpen;
        return connection;
    }

    public void SetInMemoryPassword(string password)
    {
        Current = new StorageOptions
        {
            DatabasePath = Current.DatabasePath,
            DatabasePassword = password,
            RememberPassword = Current.RememberPassword,
        }.Normalize();
    }

    private StorageOptions LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
            }

            var json = File.ReadAllText(_configPath);
            var stored = JsonSerializer.Deserialize<StorageOptionsDocument>(json, JsonOptions);
            if (stored is null)
            {
                return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
            }

            bool rememberPassword;
            string loadedPassword;
            bool needsMigration = false;

            if (!string.IsNullOrEmpty(stored.DatabasePasswordProtected))
            {
                // Protected blob is present — try to unprotect it.
                rememberPassword = stored.RememberPassword ?? true;
                loadedPassword = string.Empty;
                try
                {
                    var protectedBytes = Convert.FromBase64String(stored.DatabasePasswordProtected);
                    var raw = _dataProtection.Unprotect(protectedBytes);
                    loadedPassword = Encoding.UTF8.GetString(raw);
                }
                catch (Exception ex)
                {
                    // Corrupt blob or user-profile change. Drop the key so the app
                    // prompts for a password — mirror CopilotAuthService behaviour.
                    Trace.TraceWarning($"DB password unprotect failed; dropping key: {ex.Message}");
                    loadedPassword = string.Empty;
                    needsMigration = true; // Rewrite file without the corrupt blob.
                }
            }
            else if (!string.IsNullOrEmpty(stored.DatabasePassword))
            {
                // Legacy plaintext (written by older versions). Use it in-memory
                // and immediately re-save as a protected blob (auto-migration, KTD2).
                rememberPassword = stored.RememberPassword ?? true;
                loadedPassword = rememberPassword ? stored.DatabasePassword! : string.Empty;
                needsMigration = true;
            }
            else
            {
                rememberPassword = stored.RememberPassword ?? false;
                loadedPassword = string.Empty;
            }

            var options = new StorageOptions
            {
                DatabasePath = stored.DatabasePath ?? StorageOptions.GetDefaultDatabasePath(),
                DatabasePassword = loadedPassword,
                RememberPassword = rememberPassword,
            }.Normalize();

            options = EnsureLegacyDefaultDatabaseCopied(options);

            // A remembered key on a file that opens without one is stale: removing
            // encryption swaps the plaintext file in before storage.json is
            // rewritten, so a crash or failed write in between leaves exactly this.
            // Keying every connection to a plaintext file fails with "file is not
            // a database", and there is no prompt to fall back on because the file
            // needs no password. Only a successful keyless read drops it, and only
            // of a non-empty file: an empty one opens keyless too, and dropping
            // the key there would build the library unencrypted.
            if (options.DatabasePassword.Length > 0
                && File.Exists(options.DatabasePath)
                && new FileInfo(options.DatabasePath).Length > 0
                && !RequiresPassword(options.DatabasePath))
            {
                Trace.TraceWarning("Dropping the remembered database password: the database is not encrypted.");
                options = options with { DatabasePassword = string.Empty };
                needsMigration = true;
            }

            if (needsMigration)
            {
                // Synchronous best-effort rewrite so the plaintext / corrupt blob
                // is removed even if the app exits abnormally before the next
                // explicit SaveAsync call.
                TrySaveMigrationSync(options);
            }

            return options;
        }
        catch (JsonException ex)
        {
            // Unreadable content - typically a save torn by a crash, a power
            // loss or a full disk. The only recovery available is the default
            // library, so the user must hear about it, and the file must be set
            // aside before the next save overwrites the evidence (F-22).
            LoadFault = QuarantineUnreadableConfig(ex);
            return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not quarantined: a locked or briefly unreadable file
            // is usually transient, and moving it aside would lose a config
            // that was never corrupt.
            Trace.TraceWarning($"Storage settings load failed: {ex.Message}");
            return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
        }
    }

    private string QuarantineUnreadableConfig(JsonException ex)
    {
        var fileName = Path.GetFileName(_configPath);
        try
        {
            var quarantinePath = AtomicFile.Quarantine(_configPath);
            Trace.TraceError($"Storage settings could not be parsed and were moved to '{quarantinePath}': {ex.Message}");
            return AppText.FormatStorageLoadFault(fileName, Path.GetFileName(quarantinePath));
        }
        catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Storage settings could not be parsed, and moving them aside failed: {moveFailure.Message}");
            return AppText.FormatStorageLoadFaultNotMoved(fileName);
        }
    }

    /// <summary>
    /// Synchronous variant of <see cref="SaveToDiskAsync"/> used only from the
    /// constructor's auto-migration path. Failures are swallowed so that a
    /// read-only or locked config directory does not prevent startup.
    /// </summary>
    private void TrySaveMigrationSync(StorageOptions options)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);

            var document = BuildDocument(options);
            AtomicFile.WriteAllText(_configPath, JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Storage settings migration save failed: {ex.Message}");
        }
    }

    private static StorageOptions EnsureLegacyDefaultDatabaseCopied(StorageOptions options)
    {
        if (string.Equals(options.DatabasePath, StorageOptions.GetDefaultDatabasePath(), StringComparison.OrdinalIgnoreCase))
        {
            TryCopyLegacyDatabase(StorageOptions.GetLegacyDefaultDatabasePath(), options.DatabasePath, options.DatabasePassword);
            return options;
        }

        if (!StorageOptions.IsLegacyDefaultDatabasePath(options.DatabasePath))
        {
            return options;
        }

        var migrated = options with { DatabasePath = StorageOptions.GetDefaultDatabasePath() };
        TryCopyLegacyDatabase(options.DatabasePath, migrated.DatabasePath, migrated.DatabasePassword);
        return migrated.Normalize();
    }

    /// <summary>
    /// Migrates the legacy default database to its new location without leaving
    /// a torn copy behind. Previously this method bit-copied <c>.db</c>,
    /// <c>.db-wal</c>, and <c>.db-shm</c> in three separate <see cref="File.Copy"/>
    /// calls, which produced a corrupt destination whenever the source had
    /// pending WAL frames (the <c>-shm</c> file in particular is a per-process
    /// shared-memory snapshot and is never safe to copy verbatim).
    ///
    /// Checkpoint-then-copy replaced that, and was still not safe:
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c> reports failure in its result row
    /// instead of throwing, so a reader holding an older snapshot leaves frames
    /// in the WAL and the copied <c>.db</c> opens as
    /// "database disk image is malformed".
    ///
    /// The migration now reads the legacy database through SQLite's online
    /// backup API, which includes WAL-resident commits by construction. If it
    /// cannot be opened — encrypted with a password we do not have yet, or
    /// locked — we skip the migration entirely rather than risk a torn copy.
    /// The legacy file stays in place and the user can re-run the migration
    /// later (once the password prompt has completed) via
    /// <see cref="ImportLegacyDatabaseAsync"/>.
    /// </summary>
    internal static void TryCopyLegacyDatabase(string legacyPath, string migratedPath, string? password)
    {
        if (!File.Exists(legacyPath) || File.Exists(migratedPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(migratedPath)!);

            SqliteDatabaseCopier.CopyDatabase(legacyPath, password, migratedPath);
            Trace.TraceInformation($"Copied legacy database from '{legacyPath}' to uninstall-safe path '{migratedPath}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Leaving the legacy database where it is keeps it readable. Writing
            // a destination we could not read in full would not.
            Trace.TraceWarning($"Legacy database migration failed; leaving '{legacyPath}' in place: {ex.Message}");
        }
    }

    private async Task SaveToDiskAsync(StorageOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        var document = BuildDocument(options);
        await AtomicFile.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
    }

    /// <summary>
    /// Builds a <see cref="StorageOptionsDocument"/> from <paramref name="options"/>.
    /// When <see cref="IDataProtectionService.CanPersistSecrets"/> is true the
    /// password is protected and stored as a base-64 blob; when false (no-op
    /// protector on non-Windows) the password is kept in-memory only and the
    /// document carries neither field.
    /// </summary>
    private StorageOptionsDocument BuildDocument(StorageOptions options)
    {
        string? protectedPassword = null;

        if (options.RememberPassword
            && !string.IsNullOrEmpty(options.DatabasePassword)
            && _dataProtection.CanPersistSecrets)
        {
            try
            {
                var raw = Encoding.UTF8.GetBytes(options.DatabasePassword);
                var protectedBytes = _dataProtection.Protect(raw);
                protectedPassword = Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"DB password protect failed; not persisting: {ex.Message}");
            }
        }

        return new StorageOptionsDocument
        {
            DatabasePath = options.DatabasePath,
            RememberPassword = options.RememberPassword,
            // DatabasePassword (plaintext) is intentionally omitted — never written.
            DatabasePasswordProtected = protectedPassword,
        };
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// StateChange handler that applies <c>PRAGMA busy_timeout = 5000</c>
    /// every time a connection transitions to the Open state. Mirrored from
    /// <see cref="Clipthrough.Database.SqliteConnectionFactory"/> so that
    /// every connection opened by this service (rekey probe, checkpoint,
    /// path-move backup) also respects the contention retry budget.
    /// </summary>
    private static void ApplyBusyTimeoutOnOpen(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState == ConnectionState.Open && sender is SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class StorageOptionsDocument
    {
        public string? DatabasePath { get; init; }

        /// <summary>
        /// True when the user opted in to persist the database password so the
        /// DB auto-unlocks on next launch.
        /// </summary>
        public bool? RememberPassword { get; init; }

        /// <summary>
        /// Database password stored as a DPAPI-protected, base-64-encoded blob.
        /// Written only when <see cref="IDataProtectionService.CanPersistSecrets"/>
        /// is true. Replaces the legacy plaintext <see cref="DatabasePassword"/> field.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? DatabasePasswordProtected { get; init; }

        /// <summary>
        /// Legacy plaintext password field (v0.x). Read-only: never written by
        /// this version. Kept for JSON deserialization backward-compatibility so
        /// that existing installs auto-migrate rather than lose their password.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? DatabasePassword { get; init; }
    }
}
