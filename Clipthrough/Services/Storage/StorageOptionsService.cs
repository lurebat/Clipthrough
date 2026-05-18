using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class StorageOptionsService : IStorageOptionsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _configPath;
    private readonly IDataProtectionService _dataProtection;

    public StorageOptionsService(IDataProtectionService dataProtection)
        : this(dataProtection, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough",
            "storage.json"))
    {
    }

    // Test-only seam: allow the config path to be overridden so tests don't
    // pollute the user's real storage.json.
    public StorageOptionsService(IDataProtectionService dataProtection, string configPath)
    {
        _dataProtection = dataProtection;
        _configPath = configPath;

        Current = LoadFromDisk();
    }

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
            // Path move still copies the DB. Same-path password edits are now
            // metadata-only — they no longer invoke the rekey pragma. Use
            // RekeyAsync to actually re-encrypt.
            await ApplyStorageChangesAsync(previous, normalized, cancellationToken);
            await SaveToDiskAsync(normalized, cancellationToken);
            Current = normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyStorageChangesAsync(StorageOptions previous, StorageOptions next, CancellationToken cancellationToken)
    {
        var oldPath = previous.DatabasePath;
        var newPath = next.DatabasePath;

        if (!File.Exists(oldPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            return;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same path. Password edits no longer trigger rekey here — that's
            // explicit through RekeyAsync.
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        if (File.Exists(newPath))
        {
            File.Delete(newPath);
        }

        await using var sourceConnection = OpenConnection(previous);
        await using var targetConnection = OpenConnection(next);
        await sourceConnection.OpenAsync(cancellationToken);
        await targetConnection.OpenAsync(cancellationToken);
        sourceConnection.BackupDatabase(targetConnection);
    }

    public async Task RekeyAsync(string currentPassword, string newPassword, bool rememberNewPassword, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            var openWith = current with { DatabasePassword = currentPassword ?? string.Empty };

            await using var connection = OpenConnection(openWith);
            try
            {
                await connection.OpenAsync(cancellationToken);
                // Ensure the supplied password actually decrypts the DB before rekeying.
                await using (var verify = connection.CreateCommand())
                {
                    verify.CommandText = "SELECT count(*) FROM sqlite_master;";
                    await verify.ExecuteScalarAsync(cancellationToken);
                }
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException("Current password is incorrect.", ex);
            }

            await using (var rekey = connection.CreateCommand())
            {
                rekey.CommandText = $"PRAGMA rekey = '{EscapeSqlLiteral(newPassword ?? string.Empty)}';";
                await rekey.ExecuteNonQueryAsync(cancellationToken);
            }

            var updated = (current with
            {
                DatabasePassword = newPassword ?? string.Empty,
                RememberPassword = rememberNewPassword,
            }).Normalize();

            await SaveToDiskAsync(updated, cancellationToken);
            Current = updated;
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
            Cache = SqliteCacheMode.Shared,
        };

        if (!string.IsNullOrWhiteSpace(options.DatabasePassword))
        {
            builder.Password = options.DatabasePassword;
        }

        return new SqliteConnection(builder.ToString());
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

            // Back-compat: v0.8.0 wrote DatabasePassword without a RememberPassword
            // field. Treat any persisted password as "remember on" so existing
            // installs keep auto-unlocking.
            var rememberPassword = stored.RememberPassword
                ?? !string.IsNullOrEmpty(stored.DatabasePassword);
            var loadedPassword = rememberPassword ? (stored.DatabasePassword ?? string.Empty) : string.Empty;

            var options = new StorageOptions
            {
                DatabasePath = stored.DatabasePath ?? StorageOptions.GetDefaultDatabasePath(),
                DatabasePassword = loadedPassword,
                RememberPassword = rememberPassword,
            }.Normalize();
            return EnsureLegacyDefaultDatabaseCopied(options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Storage settings load failed: {ex.Message}");
            return EnsureLegacyDefaultDatabaseCopied(StorageOptions.Default.Normalize());
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
    private static void TryCopyLegacyDatabase(string legacyPath, string migratedPath, string? password)
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

    private static void TryCheckpointLegacyDatabase(string legacyPath, string? password)
    {
        // No password means we can't read encrypted pages, so the safest thing
        // is to skip the checkpoint and accept that any uncheckpointed WAL
        // frames will not make it into the migrated copy. That's strictly
        // better than producing a structurally corrupt destination, which is
        // what the previous raw .db + -wal + -shm copy did.
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Mode = SqliteOpenMode.ReadWrite,
                Password = password,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Trace.TraceWarning($"Legacy database checkpoint failed; copying raw .db without merging WAL: {ex.Message}");
        }
    }

    private async Task SaveToDiskAsync(StorageOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        var document = new StorageOptionsDocument
        {
            DatabasePath = options.DatabasePath,
            RememberPassword = options.RememberPassword,
            DatabasePassword = options.RememberPassword && !string.IsNullOrEmpty(options.DatabasePassword)
                ? options.DatabasePassword
                : null,
        };

        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class StorageOptionsDocument
    {
        public string? DatabasePath { get; init; }

        /// <summary>
        /// True when the user opted in to persist the database password as
        /// plaintext so the DB auto-unlocks on next launch.
        /// </summary>
        public bool? RememberPassword { get; init; }

        /// <summary>
        /// Database password stored as plaintext (only when
        /// <see cref="RememberPassword"/> is true). The settings UI surfaces a
        /// warning about this trade-off and gates writing it on an explicit
        /// confirmation dialog.
        /// </summary>
        public string? DatabasePassword { get; init; }
    }
}
