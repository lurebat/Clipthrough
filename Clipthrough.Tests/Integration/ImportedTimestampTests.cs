using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-35. Timestamps are stored as round-trip text and compared as text, which
/// only orders correctly when every value is UTC. A ClipAngel import kept each
/// row's local offset, so imported clips sorted and expired wrong by the
/// offset. They are converted at the store boundary, and a one-time backfill
/// repairs rows already imported.
/// </summary>
public sealed class ImportedTimestampTests
{
    private static async Task<string[]> TimestampsAsync(TemporaryDatabaseScope scope, string where)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT captured_at, first_copied_at, last_copied_at FROM clips WHERE {where};";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "no matching clip");
        return [reader.GetString(0), reader.GetString(1), reader.GetString(2)];
    }

    [Fact]
    public async Task AClipAngelImport_StoresTheCreatedTimeInUtc()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        // The importer finishes with retention maintenance; a 2024 clip would be
        // purged by the default lifetime before it could be read back.
        scope.SettingsService.SetCurrent(Clipthrough.Models.AppSettings.Default with { EnableNormalClipLifetime = false, EnableSensitiveClipLifetime = false });
        var dir = Directory.CreateTempSubdirectory("ct-ca-utc-").FullName;
        try
        {
            var fixture = Path.Combine(dir, "plain.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Clips (Type TEXT, Text TEXT, Title TEXT, Application TEXT, Window TEXT, Created TEXT,
                                        Binary BLOB, RichText TEXT, HtmlText TEXT, Url TEXT, Favorite INTEGER, AppPath TEXT);
                    INSERT INTO Clips (Type, Text, Created, Favorite) VALUES ('txt', 'imported from clipangel', '2024-01-02 10:00:00+05:00', 0);
                    """;
                command.ExecuteNonQuery();
            }

            var importer = new ClipAngelImportService(scope.ClipStoreService);
            var result = await importer.ImportCoreAsync(
                _ => { var copy = Path.Combine(dir, "copy.db"); File.Copy(fixture, copy); return Task.FromResult(copy); },
                progress: null,
                CancellationToken.None);
            Assert.Equal(1, result.Imported);

            var stamps = await TimestampsAsync(scope, "content = 'imported from clipangel'");
            Assert.All(stamps, s => Assert.Equal("2024-01-02T05:00:00.0000000+00:00", s));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ASingleCaptureWithAnOverride_StoresItInUtc()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(Clipthrough.Models.AppSettings.Default with { EnableNormalClipLifetime = false, EnableSensitiveClipLifetime = false });

        await scope.ClipStoreService.CaptureAsync(new Clipthrough.Models.ClipCaptureRequest
        {
            ContentType = Clipthrough.Models.ContentType.Text,
            ContentFormat = Clipthrough.Models.ClipContentFormat.PlainText,
            ContentText = "captured with an override",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("captured with an override"),
            CapturedAtOverride = new DateTimeOffset(2024, 1, 2, 10, 0, 0, TimeSpan.FromHours(5)),
            SkipPostInsertMaintenance = true,
        });

        Assert.All(await TimestampsAsync(scope, "content = 'captured with an override'"), s => Assert.Equal("2024-01-02T05:00:00.0000000+00:00", s));
    }
    [Fact]
    public async Task RowsAlreadyImportedWithAnOffset_AreRepairedOnce_AndNothingElseChanges()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (hash, content_type, content_format, content, byte_size, captured_at, first_copied_at, last_copied_at)
                VALUES ('h1', 'text', 'text', 'offset row', 10,
                        '2024-01-02T10:00:00.0000000+05:00', '2024-01-02T10:00:00.0000000+05:00', '2024-01-02T11:30:00.0000000-02:30');
                INSERT INTO clips (hash, content_type, content_format, content, byte_size, captured_at, first_copied_at, last_copied_at)
                VALUES ('h2', 'text', 'text', 'utc row', 7,
                        '2024-03-04T01:02:03.0000000+00:00', '2024-03-04T01:02:03.0000000+00:00', '2024-03-04T01:02:03.0000000+00:00');
                INSERT INTO clips (hash, content_type, content_format, content, byte_size, captured_at, first_copied_at, last_copied_at)
                VALUES ('h3', 'text', 'text', 'bare row', 8, '2024-05-06 07:08:09', '2024-05-06 07:08:09', '2024-05-06 07:08:09');
                UPDATE app_metadata SET value = '8' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(
            ["2024-01-02T05:00:00.0000000+00:00", "2024-01-02T05:00:00.0000000+00:00", "2024-01-02T14:00:00.0000000+00:00"],
            await TimestampsAsync(scope, "content = 'offset row'"));
        Assert.All(await TimestampsAsync(scope, "content = 'utc row'"), s => Assert.Equal("2024-03-04T01:02:03.0000000+00:00", s));
        Assert.All(await TimestampsAsync(scope, "content = 'bare row'"), s => Assert.Equal("2024-05-06 07:08:09", s));
    }
}