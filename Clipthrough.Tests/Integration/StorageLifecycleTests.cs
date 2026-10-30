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

    // ─── Adding and removing encryption (F-16) ────────────────────────────────

    private const string Marker = "SECRET-PLAINTEXT-MARKER-4711";

    /// <summary>
    /// A real library - full schema, FTS5 triggers and all - with one clip,
    /// opened through the same storage service the rekey goes through.
    /// </summary>
    private async Task<(StorageOptionsService Storage, ClipStoreService Store, string DbPath, long UserVersion)> CreateLibraryAsync(string password)
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        var storage = NewStorage(dbPath);
        storage.SetInMemoryPassword(password);
        var factory = new Clipthrough.Database.SqliteConnectionFactory(storage);
        var sensitivity = new SensitivityService(factory);
        await new Clipthrough.Database.DatabaseInitializer(factory, sensitivity).InitializeAsync();
        var store = new ClipStoreService(factory, sensitivity, new TestSettingsService(), new TestNotificationService());
        var clip = await store.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "invoice " + Marker + " 2024",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("invoice " + Marker + " 2024"),
            SourceApp = "Tests",
        });
        Assert.NotNull(clip);

        long userVersion;
        await using (var connection = factory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        SqliteConnection.ClearAllPools();
        return (storage, store, dbPath, userVersion);
    }

    private static long ReadUserVersion(string dbPath, string password)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool FileContains(string path, string marker)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal" })
        {
            if (File.Exists(file)
                && System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file)).Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The library starts unencrypted by default, and "Re-encrypt database" is
    /// the in-app path to encryption the dialog promises. SQLCipher's PRAGMA
    /// rekey is a no-op on a plaintext file, so this failed verification with
    /// a message blaming the password.
    /// </summary>
    [Fact]
    public async Task Rekey_PlaintextLibrary_BecomesEncryptedAndKeepsEverything()
    {
        var (storage, store, dbPath, userVersion) = await CreateLibraryAsync(string.Empty);
        Assert.True(FileContains(dbPath, Marker), "Setup: a plaintext library should show its content on disk.");

        await storage.RekeyAsync(string.Empty, "new-pw", rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.True(StorageOptionsService.CanOpenWithPassword(dbPath, "new-pw"), "The encrypted library does not open with the new key.");
        Assert.False(StorageOptionsService.CanOpenWithPassword(dbPath, string.Empty), "The library still opens without a key.");
        Assert.False(FileContains(dbPath, Marker), "Clip content is still readable on disk.");
        Assert.Equal(userVersion, ReadUserVersion(dbPath, "new-pw"));

        // The full-text index came across with the data and still answers.
        var found = await store.SearchAsync(new ClipSearchFilters { SearchText = Marker });
        Assert.Equal(1, found.TotalMatchingCount);
    }

    [Fact]
    public async Task Rekey_EncryptedLibrary_ToNoPassword_OpensWithoutAKey()
    {
        var (storage, store, dbPath, userVersion) = await CreateLibraryAsync("old-pw");
        Assert.False(FileContains(dbPath, Marker), "Setup: an encrypted library should not show its content on disk.");

        await storage.RekeyAsync("old-pw", string.Empty, rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.True(StorageOptionsService.CanOpenWithPassword(dbPath, string.Empty), "The decrypted library does not open without a key.");
        Assert.False(StorageOptionsService.RequiresPassword(dbPath));
        Assert.Equal(userVersion, ReadUserVersion(dbPath, string.Empty));

        var found = await store.SearchAsync(new ClipSearchFilters { SearchText = Marker });
        Assert.Equal(1, found.TotalMatchingCount);
    }

    /// <summary>
    /// Removing encryption swaps the plaintext copy in before storage.json is
    /// rewritten. When that write fails - or the app dies anywhere between the
    /// two - storage.json still remembers the old password, and the next start
    /// keyed every connection to a file that has no key: "file is not a
    /// database", with no prompt to fall back on, until storage.json was edited
    /// by hand. A held handle on storage.json is the write failure here; it
    /// leaves exactly the files a crash after the swap leaves.
    /// </summary>
    [Fact]
    public async Task Rekey_ToNoPassword_ConfigWriteFailsAfterTheSwap_NextStartOpensTheLibrary()
    {
        var (storage, _, dbPath, _) = await CreateLibraryAsync("old-pw");
        await storage.SaveAsync(storage.Current with { DatabasePassword = "old-pw", RememberPassword = true });
        Assert.Contains("databasePasswordProtected", File.ReadAllText(_configPath), StringComparison.Ordinal);

        using (new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                storage.RekeyAsync("old-pw", string.Empty, rememberNewPassword: true));
        }

        SqliteConnection.ClearAllPools();
        Assert.False(StorageOptionsService.RequiresPassword(dbPath), "Setup: the swap to the plaintext copy should have happened.");
        Assert.Contains("databasePasswordProtected", File.ReadAllText(_configPath), StringComparison.Ordinal);

        var next = new StorageOptionsService(new FakeDataProtectionService(), _configPath);
        var factory = new Clipthrough.Database.SqliteConnectionFactory(next);
        var store = new ClipStoreService(factory, new SensitivityService(factory), new TestSettingsService(), new TestNotificationService());

        var found = await store.SearchAsync(new ClipSearchFilters { SearchText = Marker });
        Assert.Equal(1, found.TotalMatchingCount);
        Assert.Equal(string.Empty, next.Current.DatabasePassword);
        Assert.DoesNotContain("databasePasswordProtected", File.ReadAllText(_configPath), StringComparison.Ordinal);
    }

    // ─── Backups across a rekey (F-15) ────────────────────────────────────────

    /// <summary>
    /// Settings lists every backup as a restore point. Before the fix each one
    /// taken before a rekey failed with "unreadable or requires a different
    /// password", and the rekey day had no restorable backup at all.
    /// </summary>
    [Fact]
    public async Task Rekey_BackupTakenBefore_StillRestores()
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        Exec(dbPath, "old-pass", "PRAGMA journal_mode=WAL; CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('yesterday');");
        var storage = NewStorage(dbPath);
        storage.SetInMemoryPassword("old-pass");
        var backups = new DatabaseBackupService(storage, null, null, null);
        await backups.EnsureDailyBackupAsync();
        var listed = Assert.Single(backups.ListBackups());

        Exec(dbPath, "old-pass", "INSERT INTO clips VALUES ('today');");
        await storage.RekeyAsync("old-pass", "new-pass", rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.Equal(listed.Path, Assert.Single(backups.ListBackups()).Path);
        await backups.RestoreAsync(listed.Path);
        SqliteConnection.ClearAllPools();

        Assert.Equal("yesterday", ReadRows(dbPath, "new-pass"));
    }

    /// <summary>
    /// Turning encryption on must not leave yesterday's history readable in
    /// plaintext beside it, and that backup must still restore.
    /// </summary>
    [Fact]
    public async Task Rekey_PlaintextBackupTakenBeforeEncrypting_IsEncryptedAndRestores()
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        Exec(dbPath, null, "PRAGMA journal_mode=WAL; CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('" + Marker + "');");
        var storage = NewStorage(dbPath);
        var backups = new DatabaseBackupService(storage, null, null, null);
        await backups.EnsureDailyBackupAsync();
        var listed = Assert.Single(backups.ListBackups());
        Assert.True(FileContains(listed.Path, Marker), "Setup: the backup should start out plaintext.");

        await storage.RekeyAsync(string.Empty, "new-pass", rememberNewPassword: false);
        SqliteConnection.ClearAllPools();

        Assert.False(FileContains(listed.Path, Marker), "The pre-encryption backup is still plaintext.");
        await backups.RestoreAsync(listed.Path);
        SqliteConnection.ClearAllPools();
        Assert.Equal(Marker, ReadRows(dbPath, "new-pass"));
    }

    // ─── storage.json writes (F-22) ───────────────────────────────────────────

    /// <summary>
    /// A crash, power loss or full disk during a storage save leaves a torn
    /// storage.json. Before the fix the next start silently opened the default
    /// library, dropped the remembered password, kept no copy of the file, and
    /// the next save overwrote the evidence.
    /// </summary>
    [Fact]
    public void TornStorageJson_IsSetAsideAndReported()
    {
        var customPath = Path.Combine(_root, "elsewhere", "clipthrough.db");
        NewStorage(customPath);
        var full = File.ReadAllText(_configPath);
        var torn = full[..(full.Length / 2)];
        File.WriteAllText(_configPath, torn);

        var storage = new StorageOptionsService(new FakeDataProtectionService(), _configPath);

        Assert.False(string.IsNullOrEmpty(storage.LoadFault), "A torn storage.json was recovered from silently.");
        var quarantined = Assert.Single(Directory.GetFiles(_root, "storage.json.corrupt-*"));
        Assert.Equal(torn, File.ReadAllText(quarantined));
        Assert.Contains(Path.GetFileName(quarantined), storage.LoadFault, StringComparison.Ordinal);

        // Out of the way, so the next save cannot overwrite it and the first-run
        // flow lets the user point the app back at their library.
        Assert.False(storage.HasSavedConfig);
    }

    /// <summary>
    /// The startup repair drops a remembered key only for a file that holds
    /// data readable without it. A file opening creates is empty until the
    /// first write, and an empty file opens keyless too; dropping the key there
    /// would build the user's library unencrypted from then on, silently.
    /// </summary>
    [Fact]
    public async Task RememberedPassword_OnAnEmptyDatabaseFile_IsKept()
    {
        var (storage, _, dbPath, _) = await CreateLibraryAsync("keep-pw");
        await storage.SaveAsync(storage.Current with { DatabasePassword = "keep-pw", RememberPassword = true });
        SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
        {
            File.Delete(dbPath + sidecar);
        }

        File.WriteAllBytes(dbPath, []);
        Assert.False(StorageOptionsService.RequiresPassword(dbPath), "Setup: an empty file should open without a key.");

        var next = new StorageOptionsService(new FakeDataProtectionService(), _configPath);

        Assert.Equal("keep-pw", next.Current.DatabasePassword);
        Assert.Contains("databasePasswordProtected", File.ReadAllText(_configPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadableStorageJson_ReportsNoFault()
    {
        NewStorage(Path.Combine(_root, "clipthrough.db"));
        var storage = new StorageOptionsService(new FakeDataProtectionService(), _configPath);
        Assert.Null(storage.LoadFault);
    }

    /// <summary>
    /// Saving has to write a new file and rename it over storage.json, never
    /// rewrite storage.json in place: an in-place write that dies part-way is
    /// exactly the torn file above. A second hard link to the old file tells
    /// the two apart deterministically - an in-place write changes what it
    /// points at, a rename does not.
    /// </summary>
    [Fact]
    public async Task SavingStorage_ReplacesStorageJsonInsteadOfRewritingIt()
    {
        var dbPath = Path.Combine(_root, "clipthrough.db");
        var storage = NewStorage(dbPath);
        var before = File.ReadAllText(_configPath);
        var link = Path.Combine(_root, "storage-link.json");
        Assert.True(CreateHardLink(link, _configPath, IntPtr.Zero), "Setup: could not create a hard link.");

        await storage.SaveAsync(new StorageOptions { DatabasePath = dbPath, RememberPassword = true });

        Assert.Contains("\"rememberPassword\": true", File.ReadAllText(_configPath), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(link));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
