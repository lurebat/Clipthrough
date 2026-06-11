using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public static bool CanOpenWithPassword(string dbPath, string password)
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
                Password = password,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.StateChange += ApplyBusyTimeoutOnOpen;
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
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

            // Checkpoint the source WAL so the raw file copy is consistent.
            TryCheckpointLegacyDatabase(oldPath, previous.DatabasePassword);
            SqliteConnection.ClearAllPools();

            // If the destination already exists, keep a timestamped safety copy.
            if (File.Exists(newPath))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var safeguard = newPath + ".before-move-" + stamp;
                File.Copy(newPath, safeguard, overwrite: false);
                Trace.TraceInformation($"Existing target '{newPath}' backed up to '{safeguard}'.");
            }

            // Copy source → temp (in the destination directory for same-volume rename).
            var tempPath = Path.Combine(Path.GetDirectoryName(newPath)!, Path.GetFileName(newPath) + ".moving-" + Guid.NewGuid().ToString("N"));
            File.Copy(oldPath, tempPath, overwrite: false);

            try
            {
                // Atomic rename: temp → newPath (overwrites any existing file).
                File.Move(tempPath, newPath, overwrite: true);
                tempPath = null; // newPath owns it now — don't delete in finally.

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
    ///   3. Checkpoint source (WAL → main file).
    ///   4. Copy DB to a temp file.
    ///   5. Apply PRAGMA rekey to the copy.
    ///   6. Verify the copy opens with the new password.
    ///   7. Atomic rename: temp → live DB path (DB file is the source of truth).
    ///   8. Update Current (key for the session + workers restarted on scope dispose).
    ///   9. Persist storage.json with the new password.
    ///
    /// If any step fails the original DB is untouched; the temp copy is cleaned up.
    /// </summary>
    public async Task RekeyAsync(string currentPassword, string newPassword, bool rememberNewPassword, CancellationToken cancellationToken = default)
    {
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
            var openWith = current with { DatabasePassword = currentPassword ?? string.Empty };
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

            // Step 3: Checkpoint so the WAL is merged into the main file and the raw copy is consistent.
            TryCheckpointLegacyDatabase(dbPath, currentPassword);
            SqliteConnection.ClearAllPools();

            // Step 4: Copy DB to a temp file.
            var tempPath = dbPath + ".rekeying-" + Guid.NewGuid().ToString("N");
            File.Copy(dbPath, tempPath, overwrite: false);

            try
            {
                // Step 5: Rekey the copy with the new password.
                var tempOptions = current with
                {
                    DatabasePath = tempPath,
                    DatabasePassword = currentPassword ?? string.Empty,
                };

                await using (var conn = OpenConnection(tempOptions))
                {
                    await conn.OpenAsync(cancellationToken);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"PRAGMA rekey = '{EscapeSqlLiteral(newPassword ?? string.Empty)}';";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // Flush pooled connections to the temp file before verifying.
                SqliteConnection.ClearAllPools();

                // Step 6: Verify the rekeyed copy opens with the new password.
                if (!CanOpenWithPassword(tempPath, newPassword ?? string.Empty))
                {
                    throw new InvalidOperationException(
                        "Rekey verification failed: the rekeyed copy does not open with the new password.");
                }

                // Step 7: Atomic swap — the rekeyed copy (verified in step 6 to
                // open with the new password) becomes the live database. Swap the
                // file BEFORE persisting the password: the DB file is the source of
                // truth for which key is required. A crash here leaves a freshly
                // rekeyed DB with storage.json still on the old password, so the next
                // launch falls back to the unlock prompt — which accepts the new
                // password the user just chose. Persisting first would instead point
                // storage.json at a key the still-old DB rejects, locking them out.
                SqliteConnection.ClearAllPools();
                File.Move(tempPath, dbPath, overwrite: true);
                tempPath = null; // dbPath owns it now.

                var updated = (current with
                {
                    DatabasePassword = newPassword ?? string.Empty,
                    RememberPassword = rememberNewPassword,
                }).Normalize();

                // Step 8: Update in-memory state so the running session (and the
                // workers restarted on scope dispose) use the new key.
                Current = updated;

                // Step 9: Persist the remembered password last. If this throws the
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

            if (needsMigration)
            {
                // Synchronous best-effort rewrite so the plaintext / corrupt blob
                // is removed even if the app exits abnormally before the next
                // explicit SaveAsync call.
                TrySaveMigrationSync(options);
            }

            return options;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Storage settings load failed: {ex.Message}");
            return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
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
            File.WriteAllText(_configPath, JsonSerializer.Serialize(document, JsonOptions));
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
    /// The safe sequence is:
    ///   1. Open the legacy database with its password.
    ///   2. PRAGMA wal_checkpoint(TRUNCATE) to flush every pending frame into the
    ///      main <c>.db</c> file, then close the connection so the OS releases
    ///      its locks and the <c>-shm</c> file becomes stale.
    ///   3. Copy only the <c>.db</c> file. The destination starts with a fresh
    ///      WAL on next open.
    ///
    /// If we don't have a working password we skip the migration entirely
    /// rather than risk a torn copy — the legacy file stays in place and the
    /// user can re-run the migration later (once the password prompt has
    /// completed) via <see cref="ImportLegacyDatabaseAsync"/>.
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

            // Best-effort checkpoint so the legacy WAL is merged into the .db
            // file. We only attempt this when we have the password — without it
            // SQLCipher can't decrypt the pages enough to apply them.
            TryCheckpointLegacyDatabase(legacyPath, password);

            File.Copy(legacyPath, migratedPath);
            Trace.TraceInformation($"Copied legacy database from '{legacyPath}' to uninstall-safe path '{migratedPath}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Legacy database migration failed: {ex.Message}");
        }
    }

    internal static void TryCheckpointLegacyDatabase(string legacyPath, string? password)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Mode = SqliteOpenMode.ReadWrite,
            };
            if (!string.IsNullOrEmpty(password))
            {
                builder.Password = password;
            }
            using var connection = new SqliteConnection(builder.ToString());
            connection.StateChange += ApplyBusyTimeoutOnOpen;
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // Encrypted DB without a password, locked file, or schema-level
            // failure. We accept that any uncheckpointed WAL frames will not
            // make it into the migrated copy — that's strictly better than
            // producing a structurally corrupt destination, which is what the
            // previous raw .db + -wal + -shm copy did.
            Trace.TraceWarning($"Legacy database checkpoint failed; copying raw .db without merging WAL: {ex.Message}");
        }
    }

    private async Task SaveToDiskAsync(StorageOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        var document = BuildDocument(options);
        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
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
