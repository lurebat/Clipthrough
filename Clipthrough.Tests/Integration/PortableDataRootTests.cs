using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// A portable copy is one folder the user carries around - to another drive
/// letter, another machine, another parent folder. The library's location in
/// storage.json must survive that, so a library inside the portable folder is
/// written relative to it. The Settings move still works: it moves the library
/// within the folder and records where it went the same way.
/// </summary>
public sealed class PortableDataRootTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ct-portable-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private StorageOptionsService NewStorage(AppDataRoot root)
        => new(new FakeDataProtectionService(), root, _provider);

    private static string StoredDatabasePath(AppDataRoot root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(root.StorageConfigPath));
        return document.RootElement.GetProperty("databasePath").GetString()!;
    }

    private static void Exec(string dbPath, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var connection = TestConnections.Create(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadRows(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = TestConnections.Create(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(v, ',') FROM clips;";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task ALibraryMovedWithinThePortableFolder_IsRecordedRelative_AndFollowsTheFolder()
    {
        var first = AppDataRoot.Chosen(Path.Combine(_work, "usb-E", "ClipthroughData"), AppDataRootSource.Portable);
        var storage = NewStorage(first);
        Directory.CreateDirectory(first.Directory);
        Exec(storage.Current.DatabasePath, "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('kept');");

        // What a user types into Settings may itself be relative.
        await storage.SaveAsync(new StorageOptions { DatabasePath = Path.Combine("library", "clips.db") });

        var moved = Path.Combine(first.Directory, "library", "clips.db");
        Assert.Equal(moved, storage.Current.DatabasePath);
        Assert.False(File.Exists(first.DefaultDatabasePath), "The move should have removed the library from its old place.");
        Assert.Equal(Path.Combine("library", "clips.db"), StoredDatabasePath(first));

        SqliteConnection.ClearAllPools();
        Directory.Move(Path.Combine(_work, "usb-E"), Path.Combine(_work, "usb-F"));
        var second = AppDataRoot.Chosen(Path.Combine(_work, "usb-F", "ClipthroughData"), AppDataRootSource.Portable);

        var reopened = NewStorage(second);

        Assert.Equal(Path.Combine(second.Directory, "library", "clips.db"), reopened.Current.DatabasePath);
        Assert.Equal("kept", ReadRows(reopened.Current.DatabasePath));
    }

    [Fact]
    public async Task APortableLibraryOutsideThePortableFolder_StaysAbsolute()
    {
        var root = AppDataRoot.Chosen(Path.Combine(_work, "usb", "ClipthroughData"), AppDataRootSource.Portable);
        var outside = Path.Combine(_work, "elsewhere", "clips.db");
        var storage = NewStorage(root);

        await storage.SaveAsync(new StorageOptions { DatabasePath = outside });

        Assert.Equal(outside, StoredDatabasePath(root));
    }

    // A folder chosen with --data-dir or CLIPTHROUGH_DATA_DIR is named by its
    // full path each time; storage.json keeps the full path too, as before.
    [Fact]
    public async Task ALibraryInsideAFolderChosenByPath_StaysAbsolute()
    {
        var root = AppDataRoot.Chosen(Path.Combine(_work, "chosen"), AppDataRootSource.CommandLine);
        var inside = Path.Combine(root.Directory, "library", "clips.db");
        var storage = NewStorage(root);

        await storage.SaveAsync(new StorageOptions { DatabasePath = inside });

        Assert.Equal(inside, StoredDatabasePath(root));
    }
}
