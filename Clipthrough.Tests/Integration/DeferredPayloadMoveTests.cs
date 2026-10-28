using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Upgrade-start research, U4 (owner decision (c)): an upgrade from a schema-8
/// library opens without moving payloads, and the move runs afterwards in the
/// background while the app is in use.
/// </summary>
/// <remarks>
/// The seams on <see cref="DatabaseInitializer"/> are static; the assembly runs its
/// tests one at a time, and every test restores them in <c>finally</c>.
/// </remarks>
public sealed class DeferredPayloadMoveTests
{
    private static readonly byte[] Icon = Enumerable.Range(0, 300).Select(static i => (byte)(i * 11)).ToArray();

    private static long Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static byte[] Payload(int i) => [(byte)i, 9, 9, 9, 9];

    /// <summary>
    /// Rows as a schema-8 library holds them: payload and icon inline, and
    /// stored_bytes counting the icon, as schema 8's triggers did.
    /// </summary>
    private static long[] SeedSchema8Library(TemporaryDatabaseScope scope, int count, int version = 8)
    {
        var ids = new long[count];
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        for (var i = 0; i < count; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_icon,
                    hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
                VALUES ($c, $b, 'image', 'bitmap', 'Paint', $icon, $h, $t, $t, $t, 5, $t);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$c", $"Image {i}");
            command.Parameters.AddWithValue("$b", Payload(i));
            command.Parameters.AddWithValue("$icon", Icon);
            command.Parameters.AddWithValue("$h", $"v8-{i}");
            command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"));
            ids[i] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var stamp = connection.CreateCommand();
        stamp.CommandText = $"""
            UPDATE clips SET stored_bytes = byte_size + LENGTH(source_app_icon);
            UPDATE app_metadata SET value = '{version.ToString(CultureInfo.InvariantCulture)}' WHERE key = 'schema_version';
            DELETE FROM app_metadata WHERE key IN ('payload_move_last_id', 'payload_move_done');
            """;
        stamp.ExecuteNonQuery();
        return ids;
    }

    private static long InlineRows(TemporaryDatabaseScope scope) =>
        Scalar(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;");

    private static async Task<string[]> InitializeTracingStepsAsync(TemporaryDatabaseScope scope, bool allowDeferredPayloadMove)
    {
        var sink = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        return sink.Where(m => m.Contains("[init-timing]", StringComparison.Ordinal)).ToArray();
    }

    private static async Task AssertEveryClipReadsAsync(TemporaryDatabaseScope scope, long[] ids)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            var full = await scope.ClipStoreService.GetByIdAsync(ids[i]);
            Assert.Equal(Payload(i), full!.ContentBytes);
            Assert.Equal(Icon, full.SourceAppIconBytes);
        }
    }

    [Fact]
    public async Task AnUpgradeFromSchema8_OpensWithoutMoving_ThenTheBackgroundMoveFinishesIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 5);

        var steps = await InitializeTracingStepsAsync(scope, allowDeferredPayloadMove: true);

        Assert.Contains(steps, s => s.Contains("move-payloads deferred", StringComparison.Ordinal));
        Assert.Equal(ids.Length, InlineRows(scope));
        Assert.Equal(9, Scalar(scope, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';"));
        await AssertEveryClipReadsAsync(scope, ids);

        // Until a row moves, its icon is still inline and still counted with it.
        Assert.Equal(ids.Length * (5 + Icon.Length), Scalar(scope, "SELECT SUM(stored_bytes) FROM clips;"));

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        Assert.Equal(0, InlineRows(scope));
        Assert.Equal(ids.Length, Scalar(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        Assert.Equal(ids.Length * 5, Scalar(scope, "SELECT SUM(stored_bytes) FROM clips;"));
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
        await AssertEveryClipReadsAsync(scope, ids);

        Assert.False(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
    }

    /// <summary>Older libraries need scanning backfills that are only cheap after the move.</summary>
    [Fact]
    public async Task AnUpgradeFromBeforeSchema8_StillMovesBeforeOpening()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3, version: 7);

        var steps = await InitializeTracingStepsAsync(scope, allowDeferredPayloadMove: true);

        Assert.DoesNotContain(steps, s => s.Contains("move-payloads deferred", StringComparison.Ordinal));
        Assert.Equal(0, InlineRows(scope));
        Assert.False(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
    }

    /// <summary>
    /// Without the caller's permission, the move stays synchronous: the
    /// default overloads keep their contract.
    /// </summary>
    [Fact]
    public async Task WithoutPermission_AnUpgradeFromSchema8_StillMovesBeforeOpening()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(0, InlineRows(scope));
    }

    /// <summary>
    /// Background batches are bounded by time, not only by size: each one is a
    /// write transaction a capture waits behind. With a zero budget every batch
    /// holds one row.
    /// </summary>
    [Fact]
    public async Task TheBackgroundMove_CommitsInBatchesBoundedByItsTimeBudget()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 6);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        var batches = 0;
        DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.Zero;
        DatabaseInitializer.BetweenBackgroundPayloadBatches = _ =>
        {
            batches++;
            return Task.CompletedTask;
        };
        try
        {
            await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null);
        }
        finally
        {
            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.FromMilliseconds(100);
        }

        Assert.Equal(ids.Length, batches);
        Assert.Equal(0, InlineRows(scope));
    }

    /// <summary>
    /// Between batches no transaction is open, so the app's own writes go
    /// through - and a write to a clip the move has not reached is kept.
    /// </summary>
    [Fact]
    public async Task WhileTheBackgroundMoveRuns_ACaptureAndAnEditLand_AndTheEditIsKept()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });
        var ids = SeedSchema8Library(scope, 4);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        using var held = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.Zero;
        DatabaseInitializer.BetweenBackgroundPayloadBatches = async token =>
        {
            held.Release();
            await release.WaitAsync(token);
        };
        try
        {
            var move = Task.Run(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20)), "The move never reached its first pause.");

            // The moved row counts its payload only; its icon is counted once in app_icons.
            Assert.Equal(5, Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {ids[0]};"));
            Assert.Equal(5 + Icon.Length, Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {ids[1]};"));

            var clock = Stopwatch.StartNew();
            var captured = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "copied mid-upgrade",
                ContentBytes = Encoding.UTF8.GetBytes("copied mid-upgrade"),
            });
            Assert.NotNull(captured);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The capture waited {clock.ElapsedMilliseconds} ms behind the move.");

            var editedPayload = Encoding.UTF8.GetBytes("<html><body>edited mid-upgrade</body></html>");
            await scope.ClipStoreService.UpdateDeferredContentAsync(ids[^1], new ClipCaptureRequest
            {
                ContentText = "edited mid-upgrade",
                ContentBytes = editedPayload,
                ContentType = ContentType.RichText,
                ContentFormat = ClipContentFormat.Html,
            });

            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            release.Release(ids.Length);
            Assert.True(await move);

            Assert.Equal(0, InlineRows(scope));
            Assert.Equal(editedPayload, (await scope.ClipStoreService.GetByIdAsync(ids[^1]))!.ContentBytes);
            Assert.Equal("copied mid-upgrade", (await scope.ClipStoreService.GetByIdAsync(captured.Id))!.Content);
        }
        finally
        {
            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.FromMilliseconds(100);
            release.Release(100);
        }
    }

    /// <summary>
    /// A crash mid-move: the next start is an ordinary one - the version was
    /// written on the upgrade start, so no integrity check - and the next
    /// background run resumes where the last committed batch left off.
    /// </summary>
    [Fact]
    public async Task ABackgroundMoveInterruptedMidBatch_ResumesWithoutAnotherIntegrityCheck()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 4);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        DatabaseInitializer.PayloadMoveBatchRows = 1;
        DatabaseInitializer.AfterPayloadRowMoved = id =>
        {
            if (id == ids[2])
            {
                throw new InvalidOperationException("injected crash in the background move");
            }
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        }
        finally
        {
            DatabaseInitializer.AfterPayloadRowMoved = null;
            DatabaseInitializer.PayloadMoveBatchRows = 200;
        }

        Assert.Equal(2, InlineRows(scope));

        var steps = await InitializeTracingStepsAsync(scope, allowDeferredPayloadMove: true);
        Assert.DoesNotContain(steps, s => s.Contains("integrity-check", StringComparison.Ordinal));
        await AssertEveryClipReadsAsync(scope, ids);

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        Assert.Equal(0, InlineRows(scope));
        await AssertEveryClipReadsAsync(scope, ids);
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
