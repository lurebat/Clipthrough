using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// The library size cap has to total what a row really costs on disk - payload plus
/// the source-app icon stored inline beside it. Spelled as an expression that total
/// is unindexable and reads the entire library off disk on every capture (445 ms at
/// 60k clips); materialised into <c>stored_bytes</c> it is a covering-index scan
/// (2.3 ms). These tests defend both halves: that the column stays exact through
/// every write path, and that it stays cheap to total.
/// </summary>
public class ClipStoredBytesTests
{
    private static async Task<long> ScalarAsync(TemporaryDatabaseScope scope, string sql)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> DriftedRowsAsync(TemporaryDatabaseScope scope)
        => await ScalarAsync(scope, "SELECT COUNT(*) FROM clips WHERE stored_bytes <> byte_size + COALESCE(LENGTH(source_app_icon), 0);");

    /// <summary>
    /// The plan assertion below has to exercise the expression the service really sums,
    /// not a copy of it pasted into the test - otherwise reverting the service to the
    /// unindexable spelling leaves the test passing.
    /// </summary>
    private static string StoredRowBytes()
    {
        var field = typeof(Clipthrough.Services.ClipStoreService)
            .GetField("StoredRowBytes", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ClipStoreService.StoredRowBytes is gone; the size cap no longer has a single definition of what a row costs.");
        return (string)field.GetRawConstantValue()!;
    }

    private static async Task<IReadOnlyList<string>> PlanAsync(TemporaryDatabaseScope scope, string sql)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(3));
        }
        return steps;
    }

    [Fact]
    public async Task StoredBytes_IsExactWhenTheIconArrivesAfterTheClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "hello",
            ContentBytes = Encoding.UTF8.GetBytes("hello"),
            SourceApp = "Editor",
            SkipPostInsertMaintenance = true,
        });
        Assert.NotNull(clip);
        Assert.Equal(0, await DriftedRowsAsync(scope));

        var beforeIcon = await ScalarAsync(scope, "SELECT stored_bytes FROM clips;");

        // The source-app icon is resolved by a background lookup and written well
        // after the clip lands. A total captured at insert time would miss it.
        var icon = new byte[4096];
        new Random(7).NextBytes(icon);
        await scope.ClipStoreService.UpdateSourceAppIconAsync(clip!.Id, icon);

        Assert.Equal(0, await DriftedRowsAsync(scope));
        Assert.Equal(beforeIcon + 4096, await ScalarAsync(scope, "SELECT stored_bytes FROM clips;"));
    }

    [Fact]
    public async Task StoredBytes_IsExactAfterRecapturingTheSameContent()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "recapture me",
            ContentBytes = Encoding.UTF8.GetBytes("recapture me"),
            SourceApp = "Editor",
            SkipPostInsertMaintenance = true,
            IncrementExistingCopyCount = true,
        };

        await scope.ClipStoreService.CaptureAsync(request);
        await scope.ClipStoreService.CaptureAsync(request);

        Assert.Equal(0, await DriftedRowsAsync(scope));
    }

    [Fact]
    public async Task StoredBytes_TracksAGrowingPayload()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4_194_304 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "small",
            ContentBytes = Encoding.UTF8.GetBytes("small"),
            SourceApp = "Editor",
            SkipPostInsertMaintenance = true,
            IncrementExistingCopyCount = true,
        });

        var small = await ScalarAsync(scope, "SELECT SUM(stored_bytes) FROM clips;");

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = new string('x', 100_000),
            ContentBytes = Encoding.UTF8.GetBytes(new string('x', 100_000)),
            SourceApp = "Editor",
            SkipPostInsertMaintenance = true,
            IncrementExistingCopyCount = true,
        });

        Assert.Equal(0, await DriftedRowsAsync(scope));
        Assert.True(await ScalarAsync(scope, "SELECT SUM(stored_bytes) FROM clips;") > small);
    }

    [Fact]
    public async Task LibraryTotal_IsServedByACoveringIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var plan = await PlanAsync(scope, $"SELECT COALESCE(SUM({StoredRowBytes()}), 0) FROM clips;");

        // A plain "SCAN clips" here means the total is reading content and icon blobs
        // off disk for every row, on every capture and every refresh.
        Assert.Contains(plan, step => step.Contains("COVERING INDEX idx_clips_stored_bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LastCapturedStat_IsServedByTheRecencyIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var plan = await PlanAsync(scope, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;");

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN clips", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.Contains("idx_clips_recency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_BackfillsStoredBytesOnAnExistingDatabase()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "pre-existing row",
            ContentBytes = Encoding.UTF8.GetBytes("pre-existing row"),
            SourceApp = "Editor",
            SkipPostInsertMaintenance = true,
        });

        // Simulate the database of an installed copy that predates the column: wipe
        // the values, drop the triggers, and wind the schema version back.
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER IF EXISTS clips_stored_bytes_ai;
                DROP TRIGGER IF EXISTS clips_stored_bytes_au;
                UPDATE clips SET stored_bytes = 0;
                UPDATE app_metadata SET value = '7' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await DriftedRowsAsync(scope));

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(0, await DriftedRowsAsync(scope));
    }
}
