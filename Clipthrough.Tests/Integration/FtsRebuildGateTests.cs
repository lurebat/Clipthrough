using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-E1, owner decision (B): a schema-version bump rebuilds the FTS index only
/// when it has to. The rebuild was the largest part of the first start after an
/// upgrade - 17.2 s of 28.5 s on a 420 MB library - and ran on every bump since
/// v0.9.3 moved it behind the version gate, although no bump after version 3
/// changed what the index holds.
/// </summary>
public sealed class FtsRebuildGateTests
{
    private static async Task CaptureAsync(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
        });

        Assert.NotNull(clip);
    }

    private static string Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Every block of the index's own storage. A rebuild writes the same rows into
    /// fresh segments, so the hits match either way and only the blocks tell the
    /// two apart: three captures leave three segments, and a rebuild leaves one.
    /// </summary>
    private static string IndexBlocks(TemporaryDatabaseScope scope) =>
        Scalar(scope, "SELECT group_concat(id || ':' || hex(block), ',') FROM (SELECT id, block FROM clips_fts_data ORDER BY id);");

    /// <summary>A MATCH, so it is answered by the index and not by the content table.</summary>
    private static long FtsMatchCount(TemporaryDatabaseScope scope, string term) =>
        long.Parse(Scalar(scope, $"SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH '{term}';"), CultureInfo.InvariantCulture);

    private static long StoredSchemaVersion(TemporaryDatabaseScope scope) =>
        long.Parse(Scalar(scope, "SELECT value FROM app_metadata WHERE key = 'schema_version';"), CultureInfo.InvariantCulture);

    private static void SetStoredSchemaVersion(TemporaryDatabaseScope scope, long version) =>
        Execute(scope, $"UPDATE app_metadata SET value = '{version.ToString(CultureInfo.InvariantCulture)}' WHERE key = 'schema_version';");

    [Fact]
    public async Task AVersionBumpThatChangesNoIndexedContent_LeavesTheIndexIntactAndSearchable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        await CaptureAsync(scope, "annual revenue headcount plan");
        await CaptureAsync(scope, "deploy checklist for friday");

        var current = StoredSchemaVersion(scope);
        var blocksBefore = IndexBlocks(scope);
        SetStoredSchemaVersion(scope, current - 1);

        var steps = await CaptureInitializationStepsAsync(scope);

        // The gate really ran: without this, a gate that never opened would pass.
        Assert.Equal(current, StoredSchemaVersion(scope));
        Assert.Contains(steps, step => step.Contains("version-write", StringComparison.Ordinal));

        Assert.Equal(blocksBefore, IndexBlocks(scope));
        Assert.Equal(2, FtsMatchCount(scope, "revenue"));
        var hits = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "revenue" });
        Assert.Equal(
            ["annual revenue headcount plan", "quarterly revenue projection"],
            hits.Items.Select(i => i.ContentPrefix).OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>
    /// The other side: a library from before the last change to what the index
    /// holds (version 3, the trigram tokenizer) still gets the rebuild. The index
    /// is emptied first so the outcome shows whether it ran.
    /// </summary>
    [Fact]
    public async Task ALibraryOlderThanTheLastSearchIndexChange_IsStillRebuilt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        await CaptureAsync(scope, "annual revenue headcount plan");

        Execute(scope, "INSERT INTO clips_fts(clips_fts) VALUES ('delete-all');");
        Assert.Equal(0, FtsMatchCount(scope, "revenue"));
        SetStoredSchemaVersion(scope, 2);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(2, FtsMatchCount(scope, "revenue"));
    }

    /// <summary>
    /// A version bump that also finds an old FTS schema recreates the table empty,
    /// so it has to rebuild even though the stored version is past the last index
    /// change. The repair tests in FtsSchemaRepairTests cover the current-version
    /// path; this is the bump path.
    /// </summary>
    [Fact]
    public async Task AnFtsSchemaRecreatedDuringAVersionBump_IsRebuilt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        await CaptureAsync(scope, "annual revenue headcount plan");

        var current = StoredSchemaVersion(scope);
        Execute(scope,
            "DROP TRIGGER IF EXISTS clips_ai;" +
            "DROP TRIGGER IF EXISTS clips_ad;" +
            "DROP TRIGGER IF EXISTS clips_au;" +
            "DROP TABLE IF EXISTS clips_fts;" +
            "CREATE VIRTUAL TABLE clips_fts USING fts5(content, tokenize='unicode61');");
        SetStoredSchemaVersion(scope, current - 1);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(current, StoredSchemaVersion(scope));
        Assert.Equal(2, FtsMatchCount(scope, "revenue"));
    }

    private static async Task<string[]> CaptureInitializationStepsAsync(TemporaryDatabaseScope scope)
    {
        var sink = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            await scope.DatabaseInitializer.InitializeAsync();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        return sink.Where(message => message.Contains("[init-timing]", StringComparison.Ordinal)).ToArray();
    }

    private sealed class TraceCaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }
    }
}
