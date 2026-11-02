using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Clipthrough.Database;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-C9. The app runs on SQLite3 Multiple Ciphers configured for SQLCipher 4,
/// so every installed library stays readable and a rollback to the
/// SQLCipher build stays possible. That configuration lives in one place,
/// <see cref="SqliteConnectionFactory.DataSourceFor"/>; a connection without it
/// writes the library's own default cipher, which no SQLCipher build reads.
/// </summary>
/// <remarks>
/// The oracle here is deliberately not the library under test. Asking the
/// connection (<c>PRAGMA cipher</c>) only reports how that connection was
/// configured, and reading the file back through the same library proves
/// nothing about its format. <see cref="IsSqlCipher4"/> instead decrypts page 1
/// with .NET's PBKDF2 and AES using SQLCipher 4's published defaults
/// (PBKDF2-HMAC-SHA512, 256000 iterations, AES-256-CBC, 4096-byte pages with
/// 80 reserved bytes: a 16-byte IV, then a 64-byte HMAC) and checks the SQLite
/// header fields the page must hold. Its own discrimination is checked first,
/// against the fixture the SQLCipher 4.5.2 build wrote and against a plaintext file.
/// </remarks>
public sealed class CipherRecipeTests : IDisposable
{
    private const int PageSize = 4096;
    private const int Reserve = 80;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ct-cipher-" + Guid.NewGuid().ToString("N"));

    public CipherRecipeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private static bool IsSqlCipher4(string path, string passphrase)
    {
        var page = new byte[PageSize];
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (file.Read(page, 0, PageSize) != PageSize)
            {
                return false;
            }
        }

        var salt = page.AsSpan(0, 16).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, 256000, HashAlgorithmName.SHA512, 32);
        var iv = page.AsSpan(PageSize - Reserve, 16).ToArray();
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(page.AsSpan(16, PageSize - Reserve - 16), iv, PaddingMode.None);

        // plain[0] is header byte 16: page size (big-endian), then the file
        // format versions, the reserved-space byte and the fixed payload fractions.
        return plain[0] == 0x10 && plain[1] == 0x00
            && plain[2] is 1 or 2 && plain[3] is 1 or 2
            && plain[4] == Reserve
            && plain[5] == 64 && plain[6] == 32 && plain[7] == 32;
    }

    private static SqliteConnection Open(string path, string key, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = SqliteConnectionFactory.Create(path, key, mode, pooling: false);
        connection.Open();
        return connection;
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private string CreateLibrary(string key, bool wal = true)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
        using (var connection = Open(path, key))
        {
            Scalar(connection, wal ? "PRAGMA journal_mode = WAL;" : "PRAGMA journal_mode = DELETE;");
            Scalar(connection, "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('alpha'), ('beta'), ('gamma');");
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void TheOracle_RecognisesWhatSqlCipher452Wrote_AndRejectsPlaintext()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SqliteCompat", "b-enc-sqlcipher452.db");
        var manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SqliteCompat", "manifest.json"));
        var password = System.Text.Json.JsonDocument.Parse(manifest).RootElement.GetProperty("Fixtures").EnumerateArray()
            .Single(f => f.GetProperty("Name").GetString() == "b-enc-sqlcipher452").GetProperty("Password").GetString()!;

        Assert.True(IsSqlCipher4(fixture, password));
        Assert.False(IsSqlCipher4(fixture, password + "x"));
        Assert.False(IsSqlCipher4(CreateLibrary(string.Empty), password));
    }

    [Fact]
    public void ANewEncryptedLibrary_IsWrittenAsSqlCipher4()
    {
        Assert.True(IsSqlCipher4(CreateLibrary("correct horse"), "correct horse"));
    }

    [Fact]
    public void EncryptingAPlaintextWalLibrary_WritesSqlCipher4_AndKeepsItInWal()
    {
        var source = CreateLibrary(string.Empty);
        var destination = source + ".rekeyed";

        StorageOptionsService.WriteRekeyedCopy(source, string.Empty, "new-key", destination);
        SqliteConnection.ClearAllPools();

        Assert.True(IsSqlCipher4(destination, "new-key"), "The encrypted copy is not in the SQLCipher 4 format.");
        Assert.True(StorageOptionsService.RequiresPassword(destination));
        using var connection = Open(destination, "new-key", SqliteOpenMode.ReadOnly);
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal("alpha,beta,gamma", Scalar(connection, "SELECT group_concat(v, ',') FROM (SELECT v FROM clips ORDER BY rowid);"));
    }

    [Fact]
    public void DecryptingAnEncryptedWalLibrary_WritesPlaintext_AndKeepsItInWal()
    {
        var source = CreateLibrary("old-key");
        var destination = source + ".rekeyed";

        StorageOptionsService.WriteRekeyedCopy(source, "old-key", string.Empty, destination);
        SqliteConnection.ClearAllPools();

        Assert.False(StorageOptionsService.RequiresPassword(destination));
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(destination), 0, 16));
        using var connection = Open(destination, string.Empty, SqliteOpenMode.ReadOnly);
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal("alpha,beta,gamma", Scalar(connection, "SELECT group_concat(v, ',') FROM (SELECT v FROM clips ORDER BY rowid);"));
    }

    [Fact]
    public void ChangingTheKey_WritesSqlCipher4UnderTheNewKeyOnly()
    {
        var source = CreateLibrary("old-key");
        var destination = source + ".rekeyed";

        StorageOptionsService.WriteRekeyedCopy(source, "old-key", "new-key", destination);
        SqliteConnection.ClearAllPools();

        Assert.True(IsSqlCipher4(destination, "new-key"));
        Assert.False(StorageOptionsService.CanOpenWithPassword(destination, "old-key"));
        Assert.True(StorageOptionsService.CanOpenWithPassword(source, "old-key"), "The source must only be read.");
    }

    [Fact]
    public void AFailedRekeyCopy_LeavesNoDestinationBehind()
    {
        var source = CreateLibrary("old-key");
        var destination = source + ".rekeyed";

        Assert.ThrowsAny<SqliteException>(() => StorageOptionsService.WriteRekeyedCopy(source, "wrong-key", "new-key", destination));
        SqliteConnection.ClearAllPools();

        Assert.Empty(Directory.EnumerateFiles(_root, Path.GetFileName(destination) + "*"));
    }

    [Theory]
    [InlineData(@"C:\Users\x\clips.db", "file:///C:/Users/x/clips.db?cipher=sqlcipher&legacy=4")]
    [InlineData(@"C:\a b\c#d%e.db", "file:///C:/a%20b/c%23d%25e.db?cipher=sqlcipher&legacy=4")]
    [InlineData(@"C:\Users\Zoë\clips.db", "file:///C:/Users/Zoë/clips.db?cipher=sqlcipher&legacy=4")]
    [InlineData(@"\\server\share\clips.db", "file:////server/share/clips.db?cipher=sqlcipher&legacy=4")]
    [InlineData(":memory:", ":memory:")]
    public void TheDataSource_CarriesTheRecipe(string path, string expected)
    {
        if (!OperatingSystem.IsWindows() && path.Contains('\\', StringComparison.Ordinal))
        {
            return;
        }

        Assert.Equal(expected, SqliteConnectionFactory.DataSourceFor(path));
    }

    [Fact]
    public void ACallerBuiltUri_IsRejected_SoItCannotChooseItsOwnCipher()
    {
        Assert.Throws<ArgumentException>(() => SqliteConnectionFactory.DataSourceFor("file:///C:/x.db?cipher=chacha20"));
    }

    [Fact]
    public void APathWithSpacesHashAndPercent_OpensAndRoundTrips()
    {
        var folder = Path.Combine(_root, "a b#c%d");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "clips.db");
        using (var connection = Open(path, "k"))
        {
            Scalar(connection, "CREATE TABLE t (x); INSERT INTO t VALUES (42);");
        }

        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(path), "The URI was not decoded back to the intended file name.");
        Assert.True(IsSqlCipher4(path, "k"));
    }
}
