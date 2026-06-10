using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Services;
using Clipthrough.Services.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DatabaseBackupService"/> covering Phase 2 (U7):
/// WAL-complete backup and pool-safe restore with pre-restore validation.
/// </summary>
public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly string _backupDir;

    public DatabaseBackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ct-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Combine(_tempRoot, "clipthrough.db");
        _backupDir = Path.Combine(_tempRoot, "backups");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private DatabaseBackupService NewService(int retention = DatabaseBackupService.DefaultRetention)
    {
        var storage = new TestStorageOptionsService(_dbPath);
        return new DatabaseBackupService(storage, null, null, null, retention);
    }

    /// <summary>
    /// Creates a minimal unencrypted SQLite database with an optional row value.
    /// Closes all connections and clears the pool before returning.
    /// </summary>
    private async Task CreateDbWithRow(string dbPath, string value = "seed")
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using var conn = new SqliteConnection(builder.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS clips (v TEXT); INSERT INTO clips VALUES (@v);";
        cmd.Parameters.AddWithValue("@v", value);
        await cmd.ExecuteNonQueryAsync();
        // Close the connection explicitly so the WAL is flushed on the next checkpoint.
        await conn.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static string? ReadSingleRow(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM clips LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    // ── EnsureDailyBackupAsync ─────────────────────────────────────────────────

    /// <summary>
    /// A backup created for today must be a readable SQLite file.
    /// </summary>
    [Fact]
    public async Task EnsureDailyBackup_WritesReadableBackupFile()
    {
        await CreateDbWithRow(_dbPath, "row1");
        var service = NewService();

        await service.EnsureDailyBackupAsync();

        var backups = Directory.GetFiles(_backupDir, "clipthrough-*.db");
        Assert.Single(backups);
        // Must be openable.
        Assert.True(StorageOptionsService.CanOpenWithPassword(backups[0], string.Empty));
    }

    /// <summary>
    /// A second call on the same day must be a no-op (today's backup already exists).
    /// </summary>
    [Fact]
    public async Task EnsureDailyBackup_IdempotentWithinSameDay()
    {
        await CreateDbWithRow(_dbPath);
        var service = NewService();

        await service.EnsureDailyBackupAsync();
        var before = new FileInfo(Directory.GetFiles(_backupDir, "clipthrough-*.db").Single()).LastWriteTimeUtc;

        // Wait a moment so the mtime would differ.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await service.EnsureDailyBackupAsync();

        var after = new FileInfo(Directory.GetFiles(_backupDir, "clipthrough-*.db").Single()).LastWriteTimeUtc;
        Assert.Equal(before, after); // File not re-written.
    }

    /// <summary>
    /// The backup must include rows that were committed to a WAL-mode database
    /// but not yet checkpointed into the main DB file when the backup runs.
    /// The service's TRUNCATE checkpoint flushes the WAL data before the copy.
    /// </summary>
    [Fact]
    public async Task EnsureDailyBackup_IncludesWalResidentRows()
    {
        // Enable WAL mode, create table, and write a row (will go to WAL).
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        // Keep this connection OPEN through the backup: a WAL database
        // checkpoints and removes its -wal sidecar when the LAST connection
        // closes, which would defeat the "row still in WAL" premise.
        await using var setupConn = new SqliteConnection(builder.ToString());
        await setupConn.OpenAsync();
        await using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL; " +
                              "PRAGMA wal_autocheckpoint = 0; " +
                              "CREATE TABLE clips (v TEXT); " +
                              "INSERT INTO clips VALUES ('wal-row');";
            await cmd.ExecuteNonQueryAsync();
        }

        // The WAL sidecar file should exist with uncommitted (uncheckpointed) frames.
        Assert.True(File.Exists(_dbPath + "-wal"), "Expected a WAL sidecar file with pending frames.");

        // Backup runs: TRUNCATE checkpoint flushes WAL → main file, then copies.
        var service = NewService();
        await service.EnsureDailyBackupAsync();
        SqliteConnection.ClearAllPools();

        var backupPath = Directory.GetFiles(_backupDir, "clipthrough-*.db").Single();
        var row = ReadSingleRow(backupPath);
        Assert.Equal("wal-row", row);
    }

    /// <summary>
    /// PruneOldBackups must keep exactly <paramref name="retention"/> newest
    /// files and delete older ones.
    /// </summary>
    [Fact]
    public async Task PruneOldBackups_KeepsNewestN()
    {
        await CreateDbWithRow(_dbPath);
        Directory.CreateDirectory(_backupDir);

        // Seed 5 fake backup files with different timestamps.
        for (int i = 0; i < 5; i++)
        {
            var path = Path.Combine(_backupDir, $"clipthrough-2024010{i}.db");
            File.WriteAllText(path, $"backup{i}");
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, i + 1, 0, 0, 0, DateTimeKind.Utc));
        }

        const int retention = 3;
        var service = NewService(retention);
        await service.EnsureDailyBackupAsync(); // adds today + prunes

        var remaining = Directory.GetFiles(_backupDir, "clipthrough-*.db");
        // Prune runs after adding today's backup, so total should be retention.
        Assert.Equal(retention, remaining.Length);
    }

    // ── ListBackups ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListBackups_ReturnsMostRecentFirst()
    {
        await CreateDbWithRow(_dbPath);
        Directory.CreateDirectory(_backupDir);

        var paths = new[]
        {
            Path.Combine(_backupDir, "clipthrough-20240101.db"),
            Path.Combine(_backupDir, "clipthrough-20240103.db"),
            Path.Combine(_backupDir, "clipthrough-20240102.db"),
        };
        File.WriteAllText(paths[0], "b0");
        File.SetLastWriteTimeUtc(paths[0], new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // 20240101
        File.WriteAllText(paths[1], "b1");
        File.SetLastWriteTimeUtc(paths[1], new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc)); // 20240103
        File.WriteAllText(paths[2], "b2");
        File.SetLastWriteTimeUtc(paths[2], new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)); // 20240102

        var service = NewService();
        var list = service.ListBackups();

        Assert.Equal(3, list.Count);
        // Newest (Jan 3) must be first.
        Assert.Contains("20240103", list[0].Path);
        Assert.Contains("20240101", list[list.Count - 1].Path);
    }

    // ── RestoreAsync ────────────────────────────────────────────────────────────

    /// <summary>
    /// Restoring a valid backup must make the restored data queryable.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_ValidBackup_RestoredDataQueryable()
    {
        // Create the live DB and a backup.
        await CreateDbWithRow(_dbPath, "live");
        var service = NewService();
        await service.EnsureDailyBackupAsync();
        var backupPath = Directory.GetFiles(_backupDir, "clipthrough-*.db").Single();

        // Update the live DB so it differs from the backup.
        SqliteConnection.ClearAllPools();
        var builder = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite };
        await using (var conn = new SqliteConnection(builder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO clips VALUES ('live-update');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // Restore the backup.
        await service.RestoreAsync(backupPath);

        // DB must be openable and contain the backed-up row.
        SqliteConnection.ClearAllPools();
        var row = ReadSingleRow(_dbPath);
        Assert.Equal("live", row);
    }

    /// <summary>
    /// Restoring a backup must rename the live files to .before-restore-{stamp}
    /// so the pre-restore state is recoverable.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_RenamesLiveFilesToBeforeRestore()
    {
        await CreateDbWithRow(_dbPath, "original");
        var service = NewService();
        await service.EnsureDailyBackupAsync();
        var backupPath = Directory.GetFiles(_backupDir, "clipthrough-*.db").Single();
        SqliteConnection.ClearAllPools();

        await service.RestoreAsync(backupPath);

        var beforeRestoreFiles = Directory.GetFiles(_tempRoot, "*.before-restore-*");
        Assert.NotEmpty(beforeRestoreFiles);
    }

    /// <summary>
    /// Restoring a non-existent backup must throw <see cref="FileNotFoundException"/>
    /// before touching the live database.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_MissingBackup_Throws()
    {
        await CreateDbWithRow(_dbPath);

        var service = NewService();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.RestoreAsync(Path.Combine(_backupDir, "nonexistent.db")));

        // Live DB must be untouched.
        Assert.True(File.Exists(_dbPath));
    }

    /// <summary>
    /// An empty (corrupt / zero-byte) backup must fail at the validation step,
    /// leaving the live database intact.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_CorruptBackup_ThrowsAndPreservesLiveDb()
    {
        await CreateDbWithRow(_dbPath, "live-safe");
        Directory.CreateDirectory(_backupDir);
        var corruptPath = Path.Combine(_backupDir, "clipthrough-corrupt.db");
        File.WriteAllBytes(corruptPath, new byte[] { 0xFF, 0xFF, 0xFF }); // not SQLite

        var service = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(corruptPath));

        // Live DB must still be readable.
        Assert.True(StorageOptionsService.CanOpenWithPassword(_dbPath, string.Empty));
        var row = ReadSingleRow(_dbPath);
        Assert.Equal("live-safe", row);
    }

    /// <summary>
    /// No .restoring temp files must remain on disk after a successful restore.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_NoTempFilesLeftAfterSuccess()
    {
        await CreateDbWithRow(_dbPath, "clean");
        var service = NewService();
        await service.EnsureDailyBackupAsync();
        var backupPath = Directory.GetFiles(_backupDir, "clipthrough-*.db").Single();
        SqliteConnection.ClearAllPools();

        await service.RestoreAsync(backupPath);

        Assert.Empty(Directory.GetFiles(_tempRoot, "*.restoring*"));
    }
}
