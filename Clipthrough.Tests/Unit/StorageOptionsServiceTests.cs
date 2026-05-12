using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class StorageOptionsServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configPath;
    private readonly string _dbPath;

    public StorageOptionsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "clipthrough-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _configPath = Path.Combine(_tempRoot, "storage.json");
        _dbPath = Path.Combine(_tempRoot, "clipthrough.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    private StorageOptionsService NewService() =>
        new(new NoOpDataProtectionService(), _configPath);

    /// <summary>
    /// Seed the storage.json so a freshly-constructed service starts with
    /// <c>Current.DatabasePath</c> already pointing at the temp DB. Without
    /// this, the first SaveAsync triggers a path-change backup from the
    /// real user LocalAppData default into the temp dir.
    /// </summary>
    private StorageOptionsService NewServicePointingAtTempDb(bool rememberPassword = false, string? password = null)
    {
        var safePath = _dbPath.Replace("\\", "\\\\");
        var pwdJson = password is null ? "null" : "\"" + password + "\"";
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + safePath + "\", " +
            "\"rememberPassword\": " + (rememberPassword ? "true" : "false") + ", " +
            "\"databasePassword\": " + pwdJson + " }");
        return NewService();
    }

    [Fact]
    public async Task SaveAsync_DoesNotWritePassword_WhenRememberPasswordFalse()
    {
        var service = NewServicePointingAtTempDb();
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "hunter2",
            RememberPassword = false,
        });

        var json = await File.ReadAllTextAsync(_configPath);
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("\"rememberPassword\": false", json);

        // Reload via a fresh instance — password should not come back.
        var reloaded = NewService();
        Assert.Equal(string.Empty, reloaded.Current.DatabasePassword);
        Assert.False(reloaded.Current.RememberPassword);
    }

    [Fact]
    public async Task SaveAsync_WritesPassword_WhenRememberPasswordTrue()
    {
        var service = NewServicePointingAtTempDb();
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "hunter2",
            RememberPassword = true,
        });

        var reloaded = NewService();
        Assert.Equal("hunter2", reloaded.Current.DatabasePassword);
        Assert.True(reloaded.Current.RememberPassword);
    }

    [Fact]
    public void LoadFromDisk_TreatsLegacyPasswordAsRememberOn()
    {
        // Simulate a v0.8.0 storage.json: has a password but no rememberPassword field.
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + _dbPath.Replace("\\", "\\\\") + "\", " +
            "\"databasePassword\": \"legacy\" }");

        var service = NewService();

        Assert.True(service.Current.RememberPassword);
        Assert.Equal("legacy", service.Current.DatabasePassword);
    }

    [Fact]
    public async Task RekeyAsync_RejectsWrongCurrentPassword()
    {
        await CreateEncryptedDb("right-password");

        var service = NewServicePointingAtTempDb(rememberPassword: false, password: "right-password");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RekeyAsync("wrong-password", "new-pass", rememberNewPassword: false));
    }

    [Fact]
    public async Task RekeyAsync_HappyPath_ChangesEncryptionKey()
    {
        await CreateEncryptedDb("first-pass");

        var service = NewServicePointingAtTempDb(rememberPassword: true, password: "first-pass");

        await service.RekeyAsync("first-pass", "second-pass", rememberNewPassword: true);

        Assert.Equal("second-pass", service.Current.DatabasePassword);

        // Old password no longer works:
        Assert.Throws<SqliteException>(() => OpenWithPassword("first-pass"));

        // New password works:
        using var ok = OpenWithPassword("second-pass");
        using var cmd = ok.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        cmd.ExecuteScalar();
    }

    private async Task CreateEncryptedDb(string password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = password,
        };

        await using var conn = new SqliteConnection(builder.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS marker (id INTEGER PRIMARY KEY); INSERT INTO marker DEFAULT VALUES;";
        await cmd.ExecuteNonQueryAsync();
    }

    private SqliteConnection OpenWithPassword(string password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Password = password,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        // Force decrypt by issuing a real read.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        cmd.ExecuteScalar();
        return conn;
    }
}
