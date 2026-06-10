using System;
using System.IO;
using System.Text.Json;
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

    // NoOp protector (CanPersistSecrets = false): used for tests that verify
    // passwords are NOT written to disk.
    private StorageOptionsService NewService() =>
        new(new NoOpDataProtectionService(), _configPath);

    // Fake protector (CanPersistSecrets = true): used for tests that verify
    // the full protect/unprotect round-trip.
    private StorageOptionsService NewFakeService() =>
        new(new FakeDataProtectionService(), _configPath);

    private StorageOptionsService NewFailingService() =>
        new(new FailingUnprotectDataProtectionService(), _configPath);

    /// <summary>
    /// Seed the storage.json so a freshly-constructed service starts with
    /// <c>Current.DatabasePath</c> already pointing at the temp DB. Without
    /// this, the first SaveAsync triggers a path-change backup from the
    /// real user LocalAppData default into the temp dir.
    /// </summary>
    private StorageOptionsService NewServicePointingAtTempDb(bool rememberPassword = false, string? password = null) =>
        NewServicePointingAtTempDb(new NoOpDataProtectionService(), rememberPassword, password);

    private StorageOptionsService NewServicePointingAtTempDb(
        IDataProtectionService protection,
        bool rememberPassword = false,
        string? password = null)
    {
        var safePath = _dbPath.Replace("\\", "\\\\");
        var pwdJson = password is null ? "null" : "\"" + password + "\"";
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + safePath + "\", " +
            "\"rememberPassword\": " + (rememberPassword ? "true" : "false") + ", " +
            "\"databasePassword\": " + pwdJson + " }");
        return new StorageOptionsService(protection, _configPath);
    }

    // ─── NoOp (non-Windows) behaviour ─────────────────────────────────────────

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
    public async Task SaveAsync_NoOpProtector_DoesNotWritePassword_EvenWhenRememberTrue()
    {
        // NoOp protector cannot persist secrets — password stays in-memory only.
        var service = NewServicePointingAtTempDb();
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "hunter2",
            RememberPassword = true,
        });

        var json = await File.ReadAllTextAsync(_configPath);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("databasePasswordProtected", json);

        // A fresh load yields no password since nothing was persisted.
        var reloaded = NewService();
        Assert.Equal(string.Empty, reloaded.Current.DatabasePassword);
    }

    // ─── FakeDataProtectionService (real-protector round-trip) ───────────────

    [Fact]
    public async Task SaveAsync_RealProtector_WritesProtectedBlob_NotPlaintext()
    {
        var service = NewServicePointingAtTempDb(new FakeDataProtectionService(), rememberPassword: true, password: null);
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "hunter2",
            RememberPassword = true,
        });

        var json = await File.ReadAllTextAsync(_configPath);
        // Plaintext must NOT appear.
        Assert.DoesNotContain("hunter2", json);
        // Protected field must be present.
        Assert.Contains("databasePasswordProtected", json);
        // Legacy plaintext field must NOT be present.
        Assert.DoesNotContain("\"databasePassword\"", json);
    }

    [Fact]
    public async Task SaveAsync_RealProtector_RoundTrips_Password()
    {
        var service = NewServicePointingAtTempDb(new FakeDataProtectionService(), rememberPassword: false, password: null);
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "hunter2",
            RememberPassword = true,
        });

        // Reload with the same FakeDataProtectionService — should round-trip.
        var reloaded = new StorageOptionsService(new FakeDataProtectionService(), _configPath);
        Assert.Equal("hunter2", reloaded.Current.DatabasePassword);
        Assert.True(reloaded.Current.RememberPassword);
    }

    // ─── Legacy plaintext auto-migration ──────────────────────────────────────

    [Fact]
    public void LoadFromDisk_TreatsLegacyPasswordAsRememberOn()
    {
        // Simulate a v0.8.0 storage.json: has a password but no rememberPassword field.
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + _dbPath.Replace("\\", "\\\\") + "\", " +
            "\"databasePassword\": \"legacy\" }");

        // NoOp protector: password is read into memory but NOT re-written.
        var service = NewService();

        Assert.True(service.Current.RememberPassword);
        Assert.Equal("legacy", service.Current.DatabasePassword);
    }

    [Fact]
    public void LoadFromDisk_LegacyPlaintext_WithRealProtector_MigratesOnLoad()
    {
        // Legacy storage.json with plaintext password.
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + _dbPath.Replace("\\", "\\\\") + "\", " +
            "\"rememberPassword\": true, " +
            "\"databasePassword\": \"migrate-me\" }");

        // Load with a real protector — migration should fire synchronously.
        var service = new StorageOptionsService(new FakeDataProtectionService(), _configPath);

        Assert.Equal("migrate-me", service.Current.DatabasePassword);

        // The config file should now have the protected blob, not the plaintext.
        var json = File.ReadAllText(_configPath);
        Assert.DoesNotContain("migrate-me", json);
        Assert.Contains("databasePasswordProtected", json);

        // A fresh load with the same protector must still return the password.
        var reloaded = new StorageOptionsService(new FakeDataProtectionService(), _configPath);
        Assert.Equal("migrate-me", reloaded.Current.DatabasePassword);
    }

    // ─── Unprotect failure: drop key ──────────────────────────────────────────

    [Fact]
    public void LoadFromDisk_UnprotectFailure_DropsKey()
    {
        // Seed the config file directly with a protected entry whose blob will fail to Unprotect.
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + _dbPath.Replace("\\", "\\\\") + "\", " +
            "\"rememberPassword\": true, " +
            "\"databasePasswordProtected\": \"aW52YWxpZA==\" }");

        // Load with a service whose Unprotect always throws.
        var service = new StorageOptionsService(new FailingUnprotectDataProtectionService(), _configPath);

        // Key must be dropped — no crash, empty password.
        Assert.Equal(string.Empty, service.Current.DatabasePassword);
    }

    // ─── remember=false: key never written ────────────────────────────────────

    [Fact]
    public async Task SaveAsync_RememberFalse_KeyUsableInSession_NeverWritten()
    {
        var service = NewServicePointingAtTempDb(new FakeDataProtectionService());
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "session-only",
            RememberPassword = false,
        });

        // In-memory state should still have the password.
        Assert.Equal("session-only", service.Current.DatabasePassword);

        // But the file should not contain it.
        var json = await File.ReadAllTextAsync(_configPath);
        Assert.DoesNotContain("session-only", json);
        Assert.DoesNotContain("databasePasswordProtected", json);

        // Fresh load: password gone.
        var reloaded = new StorageOptionsService(new FakeDataProtectionService(), _configPath);
        Assert.Equal(string.Empty, reloaded.Current.DatabasePassword);
    }

    // ─── Rekey tests ──────────────────────────────────────────────────────────

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

        var service = NewServicePointingAtTempDb(new FakeDataProtectionService(), rememberPassword: true, password: "first-pass");

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
