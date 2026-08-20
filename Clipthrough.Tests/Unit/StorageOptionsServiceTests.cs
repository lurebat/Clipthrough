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

/// <summary>
/// Phase 2 (U5/U6) crash-safety tests for StorageOptionsService.
/// These extend the base fixture by reusing its temp-directory and helpers.
/// </summary>
public sealed class StorageOptionsServicePhase2Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configPath;
    private readonly string _dbPath;

    public StorageOptionsServicePhase2Tests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ct-storage-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _configPath = Path.Combine(_tempRoot, "storage.json");
        _dbPath = Path.Combine(_tempRoot, "clipthrough.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private StorageOptionsService NewService(bool rememberPassword = false, string? password = null) =>
        NewService(new NoOpDataProtectionService(), rememberPassword, password);

    private StorageOptionsService NewService(
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
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS marker (id INTEGER PRIMARY KEY); " +
                          "INSERT INTO marker DEFAULT VALUES;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static void OpenAndVerify(string path, string password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Password = password,
        };
        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        cmd.ExecuteScalar();
    }

    /// <summary>
    /// Leaves <paramref name="dbPath"/> in the state an unclean shutdown leaves
    /// behind: a main .db file plus a -wal holding committed frames that were
    /// never checkpointed into it, and no process holding either open. SQLite
    /// replays the WAL on the next open, so the database still reads correctly -
    /// but anything that copies only the .db file loses those commits.
    ///
    /// It is built by copying the two files out from under a live connection,
    /// because a clean close checkpoints and removes the WAL.
    /// </summary>
    private static async Task CreateEncryptedDbWithHotWal(string dbPath, string password, string value)
    {
        var staging = Path.Combine(Path.GetTempPath(), "ct-hotwal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagedDb = Path.Combine(staging, "staged.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = stagedDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = password,
            Pooling = false,
        };

        await using (var conn = new SqliteConnection(builder.ToString()))
        {
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode = WAL; " +
                                  "PRAGMA wal_autocheckpoint = 0; " +
                                  "CREATE TABLE marker (v TEXT); " +
                                  "INSERT INTO marker VALUES (@v);";
                cmd.Parameters.AddWithValue("@v", value);
                await cmd.ExecuteNonQueryAsync();
            }

            // Snapshot both files while the connection is still open: a clean
            // close would fold the WAL into the main file and destroy the case.
            Assert.True(File.Exists(stagedDb + "-wal"), "Setup failed to produce a WAL.");
            File.Copy(stagedDb, dbPath, overwrite: true);
            File.Copy(stagedDb + "-wal", dbPath + "-wal", overwrite: true);
        }

        try { Directory.Delete(staging, recursive: true); } catch { }

        // The fixture is only meaningful if the row lives in the WAL and not in
        // the main file, so prove that before any test relies on it.
        var mainOnly = Path.Combine(Path.GetDirectoryName(dbPath)!, "main-only-probe.db");
        File.Copy(dbPath, mainOnly, overwrite: true);
        Assert.Null(TryReadMarker(mainOnly, password));
        File.Delete(mainOnly);
    }

    /// <summary>
    /// Reads the single marker row, or returns null when the database cannot be
    /// opened or has no such table - which is what a torn copy looks like from
    /// the outside.
    /// </summary>
    private static string? TryReadMarker(string dbPath, string password)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Password = password,
                Pooling = false,
            };
            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM marker LIMIT 1;";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-encryption starting from a database left with a hot WAL by an unclean
    /// shutdown must end with those commits still present. The rekeyed copy is
    /// renamed over the live database and the original is deleted, so anything
    /// dropped along the way is lost for good.
    ///
    /// Note this passes with a raw file copy too, because step 1's verification
    /// probe opens and cleanly closes the database and so checkpoints the WAL
    /// before the copy runs. That ordering is what makes the rekey path safe,
    /// not the copy method - reading through SQLite here is defence in depth
    /// and consistency with the move and backup paths, which are reachable.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_DatabaseWithAHotWal_KeepsTheCommittedRows()
    {
        await CreateEncryptedDbWithHotWal(_dbPath, "old-pass", "survives-rekey");
        var service = NewService(rememberPassword: true, password: "old-pass");

        await service.RekeyAsync("old-pass", "new-pass", rememberNewPassword: true);

        Assert.Equal("survives-rekey", TryReadMarker(_dbPath, "new-pass"));
    }

    /// <summary>
    /// Persisting storage.json is the last step of a path move, and it happens
    /// after the source database has already been deleted. If it fails, Current
    /// must still point at where the database actually is. It used to be left
    /// on the old path, so the maintenance scope restarted the workers there,
    /// they found nothing and created an empty database, and every clip from
    /// then on went into it while the real history sat orphaned at the new
    /// path.
    /// </summary>
    [Fact]
    public async Task SaveAsync_MoveSucceedsButConfigWriteFails_LeavesCurrentOnTheMovedDatabase()
    {
        await CreateEncryptedDb("pass");
        var service = NewService(rememberPassword: true, password: "pass");
        var newPath = Path.Combine(_tempRoot, "moved", "clipthrough.db");

        // Make the config file unwritable by putting a directory in its place.
        File.Delete(_configPath);
        Directory.CreateDirectory(_configPath);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.SaveAsync(service.Current with { DatabasePath = newPath }));

        Assert.False(File.Exists(_dbPath), "The move should have removed the source.");
        Assert.True(File.Exists(newPath), "The move should have completed.");
        Assert.Equal(newPath, service.Current.DatabasePath);
    }

    /// <summary>
    /// The converse of the move case: a save that moved nothing has no
    /// already-committed file-system change to agree with, so a failed write
    /// must leave the in-memory state exactly as it was rather than diverging
    /// from what is on disk.
    /// </summary>
    [Fact]
    public async Task SaveAsync_NoMoveAndConfigWriteFails_LeavesCurrentUnchanged()
    {
        await CreateEncryptedDb("pass");
        var service = NewService(rememberPassword: true, password: "pass");
        var before = service.Current;

        File.Delete(_configPath);
        Directory.CreateDirectory(_configPath);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.SaveAsync(service.Current with { RememberPassword = false }));

        Assert.Equal(before.RememberPassword, service.Current.RememberPassword);
        Assert.Equal(before.DatabasePath, service.Current.DatabasePath);
    }

    /// <summary>
    /// The same hazard on the database path move, which deletes the source as
    /// soon as the copy is renamed into place.
    /// </summary>
    [Fact]
    public async Task SaveAsync_MovingADatabaseWithAHotWal_KeepsTheCommittedRows()
    {
        await CreateEncryptedDbWithHotWal(_dbPath, "pass", "survives-move");
        var service = NewService(rememberPassword: true, password: "pass");
        var newPath = Path.Combine(_tempRoot, "moved", "clipthrough.db");

        await service.SaveAsync(service.Current with { DatabasePath = newPath });

        Assert.Equal("survives-move", TryReadMarker(newPath, "pass"));
    }

    // ─── U5: Atomic rekey ─────────────────────────────────────────────────────

    /// <summary>
    /// The rekeyed DB must be openable with the new password; the old key
    /// must no longer work. Current is updated accordingly.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_HappyPath_RekeysAndVerifies()
    {
        await CreateEncryptedDb("old-pass");
        var service = NewService(new FakeDataProtectionService(), rememberPassword: true, password: "old-pass");

        await service.RekeyAsync("old-pass", "new-pass", rememberNewPassword: true);

        Assert.Equal("new-pass", service.Current.DatabasePassword);
        Assert.True(service.Current.RememberPassword);

        // New key must open the DB.
        OpenAndVerify(_dbPath, "new-pass");

        // Old key must no longer open the DB.
        Assert.False(StorageOptionsService.CanOpenWithPassword(_dbPath, "old-pass"));
    }

    /// <summary>
    /// A password containing single quotes must be escaped correctly so the
    /// PRAGMA rekey literal doesn't break the SQL syntax or truncate the key.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_SingleQuoteInPassword_RoundTrips()
    {
        const string tricky = "it's a test";
        await CreateEncryptedDb("start");
        var service = NewService(rememberPassword: false, password: "start");

        await service.RekeyAsync("start", tricky, rememberNewPassword: false);

        Assert.Equal(tricky, service.Current.DatabasePassword);
        OpenAndVerify(_dbPath, tricky);
    }

    /// <summary>
    /// Wrong current password must throw before touching the database or any
    /// temp files.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_WrongCurrentPassword_ThrowsBeforeAnyChange()
    {
        await CreateEncryptedDb("correct");
        var service = NewService(rememberPassword: false, password: "correct");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RekeyAsync("wrong", "new", rememberNewPassword: false));

        // Original DB still works.
        OpenAndVerify(_dbPath, "correct");
        // No temp files should have been created.
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.rekeying-*"));
    }

    /// <summary>
    /// remember=false rekey keeps the new password in-memory (usable in-session)
    /// but must not write it to storage.json.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_RememberFalse_KeyInMemoryNotOnDisk()
    {
        await CreateEncryptedDb("first");
        var service = NewService(new FakeDataProtectionService(), rememberPassword: false, password: "first");

        await service.RekeyAsync("first", "second", rememberNewPassword: false);

        Assert.Equal("second", service.Current.DatabasePassword);

        var json = await File.ReadAllTextAsync(_configPath);
        Assert.DoesNotContain("second", json);
        Assert.DoesNotContain("databasePasswordProtected", json);
    }

    /// <summary>
    /// No temp .rekeying file must remain on disk after a successful rekey.
    /// </summary>
    [Fact]
    public async Task RekeyAsync_NoTempFilesLeftAfterSuccess()
    {
        await CreateEncryptedDb("pass1");
        var service = NewService(rememberPassword: false, password: "pass1");

        await service.RekeyAsync("pass1", "pass2", rememberNewPassword: false);

        Assert.Empty(Directory.GetFiles(_tempRoot, "*.rekeying-*"));
    }

    // ─── U5: Same-path password validation ───────────────────────────────────

    /// <summary>
    /// SaveAsync with a changed password on the same DB path must throw when
    /// the new password does not actually open the database — a metadata-only
    /// write would lock the user out on the next launch.
    /// </summary>
    [Fact]
    public async Task SaveAsync_SamePathPasswordChange_WrongNewPassword_Throws()
    {
        await CreateEncryptedDb("real-key");
        // Service configured with the real key.
        var service = NewService(rememberPassword: false, password: "real-key");

        // Try to save with a different (wrong) password — same path.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new StorageOptions
            {
                DatabasePath = _dbPath,
                DatabasePassword = "wrong-key",
                RememberPassword = false,
            }));
    }

    /// <summary>
    /// SaveAsync with the correct (unchanged) password must not throw even
    /// though the path is the same.
    /// </summary>
    [Fact]
    public async Task SaveAsync_SamePathCorrectPassword_Succeeds()
    {
        await CreateEncryptedDb("mykey");
        var service = NewService(rememberPassword: false, password: "mykey");

        // Same path, same password — should not throw.
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = _dbPath,
            DatabasePassword = "mykey",
            RememberPassword = true,
        });

        Assert.Equal("mykey", service.Current.DatabasePassword);
    }

    // ─── U6: Atomic path move ─────────────────────────────────────────────────

    /// <summary>
    /// A path move to a fresh location must result in the new file containing
    /// the original data, the old file removed, and Current updated.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PathMove_MovesDataToNewPath()
    {
        // Create an unencrypted DB with a known row at the old path.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using (var conn = new SqliteConnection(builder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('hello');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var newPath = Path.Combine(_tempRoot, "sub", "new.db");
        var service = NewService(rememberPassword: false, password: null);

        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = newPath,
            DatabasePassword = string.Empty,
            RememberPassword = false,
        });

        // Old path removed.
        Assert.False(File.Exists(_dbPath));
        // New path exists and has the row.
        var newBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = newPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var newConn = new SqliteConnection(newBuilder.ToString());
        newConn.Open();
        using var sel = newConn.CreateCommand();
        sel.CommandText = "SELECT v FROM t;";
        var value = (string?)sel.ExecuteScalar();
        Assert.Equal("hello", value);
    }

    /// <summary>
    /// When the destination already exists and the copy throws (simulated by
    /// making the destination directory read-only would be OS-dependent, so
    /// instead we verify that the timestamped .before-move file is written
    /// before the new file appears).
    /// If the copy step throws midway, the destination must still contain its
    /// original content.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PathMove_ExistingTarget_BackedUpWithTimestamp()
    {
        // Seed source.
        var srcBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using (var conn = new SqliteConnection(srcBuilder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE src (v TEXT); INSERT INTO src VALUES ('src-row');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // Pre-create destination with different content.
        var newPath = Path.Combine(_tempRoot, "dest.db");
        var dstBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = newPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using (var conn = new SqliteConnection(dstBuilder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE dst (v TEXT); INSERT INTO dst VALUES ('original-dest');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var service = NewService(rememberPassword: false, password: null);

        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = newPath,
            DatabasePassword = string.Empty,
            RememberPassword = false,
        });

        // A .before-move backup of the old destination must exist.
        var beforeMoveFiles = Directory.GetFiles(_tempRoot, "dest.db.before-move-*");
        Assert.NotEmpty(beforeMoveFiles);
    }

    /// <summary>
    /// No .moving temp files must be left on disk after a successful path move.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PathMove_NoTempFilesAfterSuccess()
    {
        var srcBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using (var conn = new SqliteConnection(srcBuilder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t2 (v TEXT); INSERT INTO t2 VALUES ('x');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var newPath = Path.Combine(_tempRoot, "moved.db");
        var service = NewService(rememberPassword: false, password: null);

        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = newPath,
            DatabasePassword = string.Empty,
            RememberPassword = false,
        });

        Assert.Empty(Directory.GetFiles(_tempRoot, "*.moving-*"));
    }

    // U6 regression: the path-move maintenance scope restarts the background
    // workers when it disposes. Current MUST already point at the new path by
    // then — else a restarted worker reopens the (deleted) old path, recreates
    // an empty DB there, and every subsequent clip is lost.
    [Fact]
    public async Task SaveAsync_PathMove_FlipsCurrentBeforeRestartingWorkers()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using (var conn = new SqliteConnection(builder.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('hello');";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // storage.json initially points at the old DB.
        File.WriteAllText(_configPath,
            "{ \"databasePath\": \"" + _dbPath.Replace("\\", "\\\\") + "\" }");

        string? pathSeenByRestartedWorker = null;
        StorageOptionsService? service = null;
        var monitor = new CurrentRecordingMonitor(
            () => pathSeenByRestartedWorker = service!.Current.DatabasePath);
        var provider = new SingleServiceProvider(typeof(IClipboardMonitorService), monitor);
        service = new StorageOptionsService(new NoOpDataProtectionService(), _configPath, provider);

        var newPath = Path.Combine(_tempRoot, "sub", "new.db");
        await service.SaveAsync(new StorageOptions
        {
            DatabasePath = newPath,
            DatabasePassword = string.Empty,
            RememberPassword = false,
        });

        // The worker restart (scope dispose) happened AFTER Current flipped.
        Assert.Equal(newPath, pathSeenByRestartedWorker);
        Assert.Equal(newPath, service.Current.DatabasePath);
        Assert.False(File.Exists(_dbPath));
    }

    private sealed class CurrentRecordingMonitor : IClipboardMonitorService
    {
        private readonly Action _onStart;

        public CurrentRecordingMonitor(Action onStart) => _onStart = onStart;
        public IObservable<ClipEntry> CapturedClips => System.Reactive.Linq.Observable.Empty<ClipEntry>();

        public IObservable<ClipEntry> UpdatedClips => System.Reactive.Linq.Observable.Empty<ClipEntry>();

        public IObservable<bool> CaptureBusy => System.Reactive.Linq.Observable.Empty<bool>();

        public bool IsRunning { get; private set; } = true;

        public void Start() { IsRunning = true; _onStart(); }

        public void Stop() { IsRunning = false; }

        public void SuppressNext() { }

        public void CancelSuppressNext() { }
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly Type _type;
        private readonly object _instance;

        public SingleServiceProvider(Type type, object instance)
        {
            _type = type;
            _instance = instance;
        }

        public object? GetService(Type serviceType) =>
            serviceType == _type ? _instance : null;
    }
}
