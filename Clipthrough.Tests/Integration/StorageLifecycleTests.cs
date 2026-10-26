using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// The storage lifecycle off the happy path: move, rekey, backup and config
/// writes, each started from a state a real machine can be in rather than from
/// a clean folder. Every test here was written against a reproduced loss or
/// lock-out (audit 2026-10-25, Phase A), so a red result is a user's library.
/// </summary>
public sealed class StorageLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly string _configPath;

    public StorageLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "storage.json");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private StorageOptionsService NewStorage(string dbPath)
    {
        File.WriteAllText(
            _configPath,
            "{ \"databasePath\": \"" + dbPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\", \"rememberPassword\": false }");
        return new StorageOptionsService(new FakeDataProtectionService(), _configPath);
    }

    private static void Exec(string dbPath, string? password, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadRows(string dbPath, string? password)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(v, ',') FROM (SELECT v FROM clips ORDER BY rowid);";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // ─── Move (F-01) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Leaves <paramref name="targetPath"/> as a database that was not shut down
    /// cleanly: a main file beside a -wal holding committed frames. Any database
    /// later renamed onto that path gets the foreign WAL replayed over it on the
    /// next open.
    ///
    /// <paramref name="emptyMain"/> reproduces the audit probe exactly (a 0-byte
    /// main file, every page in the WAL). Without it the schema is checkpointed
    /// into the main file and only the marker row lives in the WAL - the shape
    /// an ordinary crash leaves, and the only one SQLite can recover from: it
    /// discards a WAL found beside an empty main file.
    /// </summary>
    private void PlantStaleDatabaseWithHotWal(string targetPath, string marker, bool emptyMain)
    {
        var savedWal = Path.Combine(_root, "saved-" + Guid.NewGuid().ToString("N") + ".wal");
        var savedMain = savedWal + ".main";
        var builder = new SqliteConnectionStringBuilder { DataSource = targetPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                    "CREATE TABLE clips (v TEXT); CREATE TABLE other (x BLOB); " +
                    "INSERT INTO other VALUES (zeroblob(20000));";
                command.ExecuteNonQuery();
            }

            if (!emptyMain)
            {
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO clips VALUES ($m);";
                command.Parameters.AddWithValue("$m", marker);
                command.ExecuteNonQuery();
            }

            // A clean close folds the WAL away, so take both files while it is still hot.
            using (var wal = new FileStream(targetPath + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = File.Create(savedWal))
            {
                wal.CopyTo(copy);
            }

            using (var main = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = File.Create(savedMain))
            {
                main.CopyTo(copy);
            }
        }

        File.Delete(targetPath);
        if (emptyMain)
        {
            File.WriteAllBytes(targetPath, Array.Empty<byte>());
            File.Delete(savedMain);
        }
        else
        {
            File.Move(savedMain, targetPath);
        }

        File.Copy(savedWal, targetPath + "-wal", overwrite: true);
        File.Delete(savedWal);
        Assert.True(new FileInfo(targetPath + "-wal").Length > 0, "Setup failed to leave a hot WAL at the target.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathMove_OntoAFolderWithALeftoverWal_KeepsTheMovedRows(bool emptyMain)
    {
        var sourcePath = Path.Combine(_root, "a", "clipthrough.db");
        var targetPath = Path.Combine(_root, "b", "clipthrough.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        Exec(sourcePath, null, "PRAGMA journal_mode=WAL; CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('source-1'); INSERT INTO clips VALUES ('source-2');");
        PlantStaleDatabaseWithHotWal(targetPath, "stale-target", emptyMain);

        var storage = NewStorage(sourcePath);
        await storage.SaveAsync(new StorageOptions { DatabasePath = targetPath, DatabasePassword = string.Empty });
        SqliteConnection.ClearAllPools();

        // Before the fix this read back "stale-target" - the foreign WAL was
        // replayed over the moved library, and the source was already deleted.
        Assert.Equal("source-1,source-2", ReadRows(targetPath, null));
        Assert.False(File.Exists(sourcePath), "The move should still remove the source.");
    }

    [Fact]
    public async Task PathMove_OntoAFolderWithALeftoverWal_KeepsTheOldTargetRecoverable()
    {
        var sourcePath = Path.Combine(_root, "a", "clipthrough.db");
        var targetPath = Path.Combine(_root, "b", "clipthrough.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        Exec(sourcePath, null, "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('source-1');");
        PlantStaleDatabaseWithHotWal(targetPath, "stale-target", emptyMain: false);

        var storage = NewStorage(sourcePath);
        await storage.SaveAsync(new StorageOptions { DatabasePath = targetPath, DatabasePassword = string.Empty });
        SqliteConnection.ClearAllPools();

        // The displaced database is someone's data too. Its main file and its WAL
        // were set aside under one shared stamp; putting them back under a
        // scratch name must yield the row that lived only in the WAL.
        var stashedMain = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(targetPath)!, "clipthrough.db.before-move-*"));
        var stamp = stashedMain[(stashedMain.LastIndexOf(".before-move-", StringComparison.Ordinal) + ".before-move-".Length)..];
        var stashedWal = targetPath + "-wal.before-move-" + stamp;
        Assert.True(File.Exists(stashedWal), "The target's -wal was not set aside with its main file.");

        // The row was only ever in the WAL, so the main file alone must not have it.
        var mainOnly = Path.Combine(_root, "main-only.db");
        File.Copy(stashedMain, mainOnly);
        Assert.Equal(string.Empty, ReadRows(mainOnly, null));

        var recovered = Path.Combine(_root, "recovered.db");
        File.Copy(stashedMain, recovered);
        File.Copy(stashedWal, recovered + "-wal");
        Assert.Equal("stale-target", ReadRows(recovered, null));
    }

    // ─── Rekey password whitespace (F-02, OD-12a) ─────────────────────────────

    /// <summary>
    /// The session and every later unlock trim the password, so the file has to
    /// be keyed with the trimmed value. Before the fix it was keyed with the raw
    /// one, verified with the raw one, and every later open used the trimmed
    /// one: "Incorrect password" forever.
    /// </summary>
    [Fact]
    public async Task Rekey_NewPasswordWithEdgeWhitespace_OpensWithTheKeyTheSessionUses()
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        Exec(dbPath, "old-pw", "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('kept');");
        var storage = NewStorage(dbPath);
        storage.SetInMemoryPassword("old-pw");

        const string typed = "correct horse ";
        await storage.RekeyAsync("old-pw", typed, rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.Equal("correct horse", storage.Current.DatabasePassword);
        Assert.True(
            StorageOptionsService.CanOpenWithPassword(dbPath, storage.Current.DatabasePassword),
            "The rekeyed database does not open with the key the running session uses.");

        // The next launch's unlock prompt: the user types exactly what they chose.
        var nextLaunch = NewStorage(dbPath);
        nextLaunch.SetInMemoryPassword(typed);
        Assert.Equal("kept", ReadRows(dbPath, nextLaunch.Current.DatabasePassword));
    }

    [Fact]
    public async Task Rekey_CurrentPasswordWithEdgeWhitespace_IsTrimmedLikeEveryOtherEntry()
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        Exec(dbPath, "old-pw", "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('kept');");
        var storage = NewStorage(dbPath);
        storage.SetInMemoryPassword("old-pw");

        await storage.RekeyAsync(" old-pw ", "new-pw", rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.Equal("kept", ReadRows(dbPath, "new-pw"));
    }
}
