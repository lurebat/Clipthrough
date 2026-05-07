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
    {
        _dataProtection = dataProtection;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough",
            "storage.json");

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

    public async Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = options.Normalize();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Current;
            if (string.Equals(previous.DatabasePath, normalized.DatabasePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.DatabasePassword, normalized.DatabasePassword, StringComparison.Ordinal))
            {
                await SaveToDiskAsync(normalized, cancellationToken);
                Current = normalized;
                return;
            }

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

        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
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
            return;
        }

        if (string.Equals(previous.DatabasePassword, next.DatabasePassword, StringComparison.Ordinal))
        {
            return;
        }

        await using var connection = OpenConnection(previous);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA rekey = '{EscapeSqlLiteral(next.DatabasePassword)}';";
        await command.ExecuteNonQueryAsync(cancellationToken);
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

            var options = new StorageOptions
            {
                DatabasePath = stored.DatabasePath ?? StorageOptions.GetDefaultDatabasePath(),
                DatabasePassword = string.Empty,
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
            TryCopyLegacyDatabase(StorageOptions.GetLegacyDefaultDatabasePath(), options.DatabasePath);
            return options;
        }

        if (!StorageOptions.IsLegacyDefaultDatabasePath(options.DatabasePath))
        {
            return options;
        }

        var migrated = options with { DatabasePath = StorageOptions.GetDefaultDatabasePath() };
        TryCopyLegacyDatabase(options.DatabasePath, migrated.DatabasePath);
        return migrated.Normalize();
    }

    private static void TryCopyLegacyDatabase(string legacyPath, string migratedPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(migratedPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(migratedPath)!);
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var source = legacyPath + suffix;
                if (File.Exists(source))
                {
                    File.Copy(source, migratedPath + suffix);
                }
            }

            Trace.TraceInformation($"Copied legacy database from '{legacyPath}' to uninstall-safe path '{migratedPath}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Legacy database migration failed: {ex.Message}");
        }
    }

    private async Task SaveToDiskAsync(StorageOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        var document = new StorageOptionsDocument
        {
            DatabasePath = options.DatabasePath,
        };

        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class StorageOptionsDocument
    {
        public string? DatabasePath { get; init; }
    }
}
