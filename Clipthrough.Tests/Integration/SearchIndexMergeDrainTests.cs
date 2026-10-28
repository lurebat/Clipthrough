using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Research note R1 (owner decision (a)): the merges a search-index rebuild
/// leaves pending are finished in short background steps, so the first large
/// captures after a rebuild do not pay for them.
/// </summary>
/// <remarks>
/// A rebuild of a library small enough for a test leaves little merge work, so
/// the pending state is built directly: many single-row segments written with
/// automerge off, then automerge restored. That is the shape a rebuild leaves -
/// segments a level could merge - without its size. The oracle is FTS5's own:
/// a merge step that has nothing to do changes fewer than two rows.
/// </remarks>
public sealed class SearchIndexMergeDrainTests
{
    private static long Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>True when one more merge step would still find work.</summary>
    private static bool MergeWorkLeft(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_changes();";
        var before = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var step = connection.CreateCommand();
        step.CommandText = "INSERT INTO clips_fts(clips_fts, rank) VALUES ('merge', 64);";
        step.ExecuteNonQuery();
        var after = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return after - before >= 2;
    }

    private static string RandomWords(Random rng, int length)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder(length + 12);
        while (sb.Length < length)
        {
            var n = rng.Next(3, 10);
            for (var k = 0; k < n; k++)
            {
                sb.Append(letters[rng.Next(letters.Length)]);
            }

            sb.Append(' ');
        }

        return sb.ToString();
    }

    private static async Task SeedPendingMergesAsync(TemporaryDatabaseScope scope, int segments)
    {
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });
        var rng = new Random(11);
        Execute(scope, "INSERT INTO clips_fts(clips_fts, rank) VALUES ('automerge', 0);");
        for (var i = 0; i < segments; i++)
        {
            // Each segment has to span pages, or a single 64-page merge step
            // finishes all of them and a drain that stopped after one step would
            // look complete. Fifteen stays under crisismerge (16), which would
            // otherwise merge them on the write path. Measured: 15 x 30,000
            // characters needs 6 steps; 40 x 6,000 needed 1.
            var text = $"segment {i} quarterly revenue projection {RandomWords(rng, 30000)}";
            var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
            });
            Assert.NotNull(clip);
        }

        Execute(scope, "INSERT INTO clips_fts(clips_fts, rank) VALUES ('automerge', 4);");
        Execute(scope, "INSERT INTO app_metadata (key, value) VALUES ('fts_merge_pending', '1') ON CONFLICT(key) DO UPDATE SET value = '1';");
    }

    [Fact]
    public async Task AfterARebuild_TheDrainLeavesNoMergeWork_AndKeepsTheIndexSearchable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedPendingMergesAsync(scope, 15);
        var segmentsBefore = Scalar(scope, "SELECT COUNT(DISTINCT segid) FROM clips_fts_idx;");

        var steps = 0;
        DatabaseInitializer.BetweenSearchIndexMergeSteps = _ =>
        {
            steps++;
            return Task.CompletedTask;
        };
        try
        {
            Assert.True(await scope.DatabaseInitializer.DrainSearchIndexMergesAsync());
        }
        finally
        {
            DatabaseInitializer.BetweenSearchIndexMergeSteps = null;
        }

        Assert.True(steps > 1, $"The fixture needed {steps} merge step(s); it has to need several to show the drain keeps going.");

        Assert.False(MergeWorkLeft(scope), "The drain stopped with merges still pending.");
        Assert.True(Scalar(scope, "SELECT COUNT(DISTINCT segid) FROM clips_fts_idx;") < segmentsBefore);
        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM app_metadata WHERE key = 'fts_merge_pending';"));
        Assert.Equal(15, Scalar(scope, "SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH 'revenue';"));

        Assert.False(await scope.DatabaseInitializer.DrainSearchIndexMergesAsync());
    }

    [Fact]
    public async Task WithoutARebuild_TheDrainDoesNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedPendingMergesAsync(scope, 12);
        Execute(scope, "DELETE FROM app_metadata WHERE key = 'fts_merge_pending';");

        Assert.False(await scope.DatabaseInitializer.DrainSearchIndexMergesAsync());
        Assert.True(MergeWorkLeft(scope), "The fixture had no merge work, so this proves nothing.");
    }

    /// <summary>Both rebuild paths record the work they leave: the version-gated one and the repair.</summary>
    [Fact]
    public async Task ARebuild_RecordsThatMergesArePending()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        Execute(scope, "DELETE FROM app_metadata WHERE key = 'fts_merge_pending';");
        Execute(scope, "UPDATE app_metadata SET value = '2' WHERE key = 'schema_version';");

        await scope.DatabaseInitializer.InitializeAsync();
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_metadata WHERE key = 'fts_merge_pending' AND value = '1';"));

        Execute(scope, "DELETE FROM app_metadata WHERE key = 'fts_merge_pending';");
        Execute(scope,
            "DROP TRIGGER IF EXISTS clips_ai; DROP TRIGGER IF EXISTS clips_ad; DROP TRIGGER IF EXISTS clips_au;" +
            "DROP TABLE IF EXISTS clips_fts; CREATE VIRTUAL TABLE clips_fts USING fts5(content, tokenize='unicode61');");
        await scope.DatabaseInitializer.InitializeAsync();
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_metadata WHERE key = 'fts_merge_pending' AND value = '1';"));
    }

    /// <summary>Between steps no transaction is open, so a capture goes straight through.</summary>
    [Fact]
    public async Task WhileTheDrainRuns_ACaptureLandsWithoutWaiting()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedPendingMergesAsync(scope, 15);

        using var held = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        DatabaseInitializer.BetweenSearchIndexMergeSteps = async token =>
        {
            held.Release();
            await release.WaitAsync(token);
        };
        try
        {
            var drain = Task.Run(() => scope.DatabaseInitializer.DrainSearchIndexMergesAsync());
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20)), "The drain never paused between steps.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "captured mid-drain",
                ContentBytes = Encoding.UTF8.GetBytes("captured mid-drain"),
            });
            Assert.NotNull(clip);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The capture waited {clock.ElapsedMilliseconds} ms behind the drain.");

            DatabaseInitializer.BetweenSearchIndexMergeSteps = null;
            release.Release(1000);
            Assert.True(await drain);
            Assert.False(MergeWorkLeft(scope));
        }
        finally
        {
            DatabaseInitializer.BetweenSearchIndexMergeSteps = null;
            release.Release(1000);
        }
    }
}
