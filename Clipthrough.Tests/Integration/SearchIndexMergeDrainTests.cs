using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// segments a level could merge - without its size. The oracle reads FTS5's
/// structure record (rubber-duck F-13-6): it must not be the drain's own stop
/// rule, a merge step changing fewer than two rows, or a wrong rule would pass.
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

    /// <summary>
    /// True when one more merge step would still find work, by FTS5's rule for a
    /// positive 'merge' (fts5IndexMerge): some level is part-way through an
    /// incremental merge, or holds at least 'usermerge' segments. Read from the
    /// structure record, without running a step, so it shares nothing with the
    /// drain's total_changes() test.
    /// </summary>
    private static bool MergeWorkLeft(TemporaryDatabaseScope scope)
    {
        var userMerge = Scalar(scope, "SELECT COALESCE((SELECT v FROM clips_fts_config WHERE k = 'usermerge'), 4);");
        return ReadStructureLevels(scope).Any(level => level.Merging > 0 || (long)level.Segments >= userMerge);
    }

    /// <summary>
    /// Decodes FTS5's structure record (clips_fts_data id 10, fts5StructureDecode):
    /// a 4-byte cookie, an optional 4-byte V2 marker, then varints - level count,
    /// segment count, write counter, and per level the segments in an incremental
    /// merge and the segment count followed by each segment (three varints, eight
    /// in V2). Asserts the totals and the length agree, so a misread fails loudly.
    /// </summary>
    private static List<(ulong Merging, ulong Segments)> ReadStructureLevels(TemporaryDatabaseScope scope)
    {
        byte[] data;
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT block FROM clips_fts_data WHERE id = 10;";
            data = (byte[])command.ExecuteScalar()!;
        }

        var i = 4;
        var v2 = data.Length >= 8 && data[4] == 0xFF && data[5] == 0 && data[6] == 0 && data[7] == 1;
        if (v2)
        {
            i += 4;
        }

        var levelCount = ReadVarint(data, ref i);
        var segmentCount = ReadVarint(data, ref i);
        ReadVarint(data, ref i);
        var levels = new List<(ulong Merging, ulong Segments)>();
        ulong segmentsSeen = 0;
        for (ulong level = 0; level < levelCount; level++)
        {
            var merging = ReadVarint(data, ref i);
            var segments = ReadVarint(data, ref i);
            for (ulong segment = 0; segment < segments * (v2 ? 8UL : 3UL); segment++)
            {
                ReadVarint(data, ref i);
            }

            segmentsSeen += segments;
            levels.Add((merging, segments));
        }

        Assert.Equal(segmentCount, segmentsSeen);
        Assert.Equal(data.Length, i);
        return levels;
    }

    /// <summary>SQLite's varint: big-endian 7-bit groups, the ninth byte whole.</summary>
    private static ulong ReadVarint(byte[] data, ref int i)
    {
        ulong value = 0;
        for (var n = 0; n < 8; n++)
        {
            var b = data[i++];
            value = (value << 7) | ((ulong)b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        return (value << 8) | data[i++];
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
        Assert.True(MergeWorkLeft(scope), "The fixture has no merge work by the structure record, so this proves nothing.");

        // One step leaves the merge part-way: the oracle has to see that, or a
        // drain that stopped early would pass.
        Execute(scope, "INSERT INTO clips_fts(clips_fts, rank) VALUES ('merge', 64);");
        Assert.True(MergeWorkLeft(scope), "The oracle reads a part-way merge as finished.");

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
