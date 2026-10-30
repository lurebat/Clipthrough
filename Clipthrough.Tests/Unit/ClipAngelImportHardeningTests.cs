using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// A ClipAngel database is a file anyone can hand the user, encrypted with a
/// password hard-coded in ClipAngel itself, so its schema is attacker-chosen
/// (F-21). Making Clips a view let the import run SQL the file's author
/// wrote, on the bundled SQLite 3.39.2 - below the CVE-2025-6965 fix - and
/// set any field, including a source URL. The import now accepts only a real
/// Clips table in a schema with no views or triggers, and reads it with
/// trusted_schema off and query_only on.
/// </summary>
public sealed class ClipAngelImportHardeningTests : IDisposable
{
    private const int PageSize = 4096;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ct-clipangel-" + Guid.NewGuid().ToString("N"));

    public ClipAngelImportHardeningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private const string ClipsColumns =
        "Type TEXT, Text TEXT, Title TEXT, Application TEXT, Window TEXT, Created TEXT, Binary BLOB, " +
        "RichText TEXT, HtmlText TEXT, Url TEXT, Favorite INTEGER, AppPath TEXT";

    /// <summary>
    /// Builds a ClipAngel-format database: plain SQLite, then every page run
    /// through ClipAngel's RC4 page codec (it is symmetric).
    /// </summary>
    private string BuildClipAngelDatabase(string schemaAndData)
    {
        var plain = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".plain.db");
        using (var connection = TestConnections.Create(new SqliteConnectionStringBuilder { DataSource = plain, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA page_size = {PageSize}; CREATE TABLE filler(x); INSERT INTO filler VALUES (randomblob(3000)); " + schemaAndData;
            command.ExecuteNonQuery();
        }

        var bytes = File.ReadAllBytes(plain);
        var encrypted = new byte[bytes.Length];
        for (var offset = 0; offset + PageSize <= bytes.Length; offset += PageSize)
        {
            var page = new byte[PageSize];
            Buffer.BlockCopy(bytes, offset, page, 0, PageSize);
            Buffer.BlockCopy(ClipAngelImportService.Rc4DecryptPage(page), 0, encrypted, offset, PageSize);
        }

        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".clipangel.db");
        File.WriteAllBytes(path, encrypted);
        return path;
    }

    private const string HostileView =
        "CREATE VIEW Clips AS SELECT 'text' AS Type, 'x' AS Text, NULL AS Title, NULL AS Application, NULL AS Window, " +
        "datetime('now') AS Created, NULL AS Binary, NULL AS RichText, NULL AS HtmlText, " +
        "'\\\\attacker-host\\share\\payload.exe' AS Url, 0 AS Favorite, NULL AS AppPath;";

    [Fact]
    public async Task AClipsView_IsRejectedBeforeAnythingIsImported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ClipAngel import is Windows-only.");
        var hostile = BuildClipAngelDatabase(HostileView);
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var service = new ClipAngelImportService(scope.ClipStoreService);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(hostile));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(hostile, null));

        Assert.Null(await scope.ClipStoreService.GetClipAtOffsetAsync(0));
    }

    [Fact]
    public async Task ATriggerAnywhereInTheSchema_IsRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ClipAngel import is Windows-only.");
        var hostile = BuildClipAngelDatabase(
            $"CREATE TABLE Clips ({ClipsColumns}); CREATE TABLE log(x); " +
            "CREATE TRIGGER t AFTER INSERT ON log BEGIN SELECT 1; END; " +
            "INSERT INTO Clips (Type, Text, Created) VALUES ('text', 'hello', '2026-01-01 10:00:00');");
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var service = new ClipAngelImportService(scope.ClipStoreService);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(hostile, null));
    }

    /// <summary>
    /// The type check on its own: no Clips table at all is refused with the
    /// same clear message, not a raw "no such table".
    /// </summary>
    [Fact]
    public async Task NoClipsTable_IsRejectedWithAClearMessage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ClipAngel import is Windows-only.");
        var odd = BuildClipAngelDatabase("CREATE TABLE NotClips (x);");
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var service = new ClipAngelImportService(scope.ClipStoreService);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(odd));
        Assert.Equal(Clipthrough.Localization.AppText.ClipAngelImportUnexpectedSchema, ex.Message);
    }

    /// <summary>Anti-vacuity: an ordinary ClipAngel database still imports.</summary>
    [Fact]
    public async Task AnOrdinaryClipsTable_StillImports()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ClipAngel import is Windows-only.");
        var ordinary = BuildClipAngelDatabase(
            $"CREATE TABLE Clips ({ClipsColumns}); " +
            "INSERT INTO Clips (Type, Text, Created) VALUES ('text', 'hello from clipangel', '2026-01-01 10:00:00');");
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var service = new ClipAngelImportService(scope.ClipStoreService);

        var preview = await service.PreviewAsync(ordinary);
        var result = await service.ImportAsync(ordinary, null);

        Assert.Equal(1, preview.TotalClips);
        Assert.Equal(1, result.Imported);
        Assert.Equal("hello from clipangel", (await scope.ClipStoreService.GetClipAtOffsetAsync(0))?.Content);
    }
}