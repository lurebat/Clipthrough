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

    private static int CurrentSchemaVersion =>
        (int)(typeof(DatabaseInitializer)
            .GetField("CurrentSchemaVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue()
            ?? throw new InvalidOperationException(
                "DatabaseInitializer.CurrentSchemaVersion is gone or is no longer a const - this test can no longer track it."));

    private const string OffsetCaptured = "2024-01-02T10:00:00.0000000+05:00";
    private const string OffsetLastCopied = "2024-01-02T11:30:00.0000000-02:30";
    private static readonly string[] OffsetTimestamps = [OffsetCaptured, OffsetCaptured, OffsetLastCopied];
    private static readonly string[] RepairedTimestamps = ["2024-01-02T05:00:00.0000000+00:00", "2024-01-02T05:00:00.0000000+00:00", "2024-01-02T14:00:00.0000000+00:00"];

    /// <summary>
    /// A row as schema 8 could hold it after a ClipAngel import: timestamps with the
    /// importing machine's offset, which Phase G's one-time backfill converts (F-35).
    /// </summary>
    private static void GiveTimestampsAnOffset(TemporaryDatabaseScope scope, long id)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET captured_at = $c, first_copied_at = $c, last_copied_at = $l WHERE id = $id;";
        command.Parameters.AddWithValue("$c", OffsetCaptured);
        command.Parameters.AddWithValue("$l", OffsetLastCopied);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string[] Timestamps(TemporaryDatabaseScope scope, long id)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT captured_at, first_copied_at, last_copied_at FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return [reader.GetString(0), reader.GetString(1), reader.GetString(2)];
    }

    private sealed class SynchronousProgress(Action<LibraryUpgradeProgress> report) : IProgress<LibraryUpgradeProgress>
    {
        public void Report(LibraryUpgradeProgress value) => report(value);
    }

    /// <summary>
    /// Phase G's UTC backfill rewrites rows, so on a deferred start it is background
    /// work like the move: not on the start, which writes nothing the integrity check
    /// protects, but after the background check and the move, when the rows it scans
    /// are small.
    /// </summary>
    [Fact]
    public async Task ADeferredUpgradeStart_LeavesTimestampsAlone_AndTheBackgroundRunRepairsThem()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 3);
        GiveTimestampsAnOffset(scope, ids[0]);

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        Assert.Equal(OffsetTimestamps, Timestamps(scope, ids[0]));

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        Assert.Equal(0, InlineRows(scope));
        Assert.Equal(RepairedTimestamps, Timestamps(scope, ids[0]));
    }

    /// <summary>
    /// The move commits its last batch together with the flag that says it is done.
    /// Closing the app right after that - Dispose cancels the background run - must
    /// not leave the backfill undone for good: the next start's run still does it.
    /// </summary>
    [Fact]
    public async Task ABackgroundRunStoppedRightAfterTheMove_StillRepairsTimestampsOnTheNextStart()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 3);
        GiveTimestampsAnOffset(scope, ids[0]);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        using var closing = new CancellationTokenSource();
        // Stop once the move has committed its done flag, and not before: the last
        // batch of rows also reports 1, before the flag is written.
        var stopAfterTheMove = new SynchronousProgress(p =>
        {
            if (p.Stage == LibraryUpgradeStage.MovingClipData && p.Fraction == 1
                && Scalar(scope, "SELECT COUNT(*) FROM app_metadata WHERE key = 'payload_move_done' AND value = '1';") == 1)
            {
                closing.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(stopAfterTheMove, closing.Token));
        Assert.Equal(0, InlineRows(scope));

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null);

        Assert.Equal(RepairedTimestamps, Timestamps(scope, ids[0]));
        Assert.False(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
    }

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
        Assert.Equal(CurrentSchemaVersion, Scalar(scope, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';"));
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

    private static long AppIconHashIndexCount(TemporaryDatabaseScope scope) =>
        Scalar(scope, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_source_app_icon_hash';");

    private static async Task<ClipEntry> CaptureAsync(TemporaryDatabaseScope scope, string text, int payloadBytes, byte[]? icon = null)
    {
        var payload = new byte[payloadBytes];
        var textBytes = Encoding.UTF8.GetBytes(text);
        Array.Copy(textBytes, payload, Math.Min(textBytes.Length, payload.Length));
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = payload,
            SourceAppIconBytes = icon,
        });
        Assert.NotNull(clip);
        return clip;
    }

    private static byte[] UniqueIcon(int length, int seed) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 7) + seed))];

    /// <summary>
    /// A deferred upgrade start, as schema 8 left it: the icon-hash index does not
    /// exist yet, so orphaned icons are not swept until the background run builds it.
    /// </summary>
    private static async Task<long[]> StartDeferredUpgradeAsync(TemporaryDatabaseScope scope, AppSettings settings)
    {
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(settings);
        var ids = SeedSchema8Library(scope, 3);
        Scalar(scope, "DROP INDEX idx_clips_source_app_icon_hash;");
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        return ids;
    }

    private static readonly AppSettings NoCaps = new()
    {
        EnableMaxLibrarySize = false,
        EnableMaxEntryCount = false,
        EnableNormalClipLifetime = false,
        EnableSensitiveClipLifetime = false,
        MaxClipSizeBytes = 2_097_152,
    };

    /// <summary>
    /// Rubber-duck F-13-5 (owner decision: option 2). While the move is owed,
    /// orphaned icons cannot be swept - finding them means scanning rows that still
    /// hold their payloads inline - so the size total leaves icons out until the
    /// move is done, and then counts each distinct icon once again.
    /// </summary>
    [Fact]
    public async Task WhileTheMoveIsOwed_TheSizeTotalLeavesIconsOut_AndCountsThemOnceItIsDone()
    {
        using var scope = new TemporaryDatabaseScope();
        await StartDeferredUpgradeAsync(scope, NoCaps);
        var orphaned = await CaptureAsync(scope, "copied with an icon", 9, UniqueIcon(4096, 1));
        await scope.ClipStoreService.DeleteAsync(orphaned.Id);
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_icons WHERE LENGTH(png) = 4096;"));

        var during = await scope.ClipStoreService.ApplyMaintenanceAsync();
        Assert.True(during.TotalStoredBytes == 3 * (5 + Icon.Length), $"While the move is owed the size total was {during.TotalStoredBytes}, not the rows' {3 * (5 + Icon.Length)}.");

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        var after = await scope.ClipStoreService.ApplyMaintenanceAsync();
        Assert.Equal((3 * 5) + Icon.Length, after.TotalStoredBytes);
    }

    /// <summary>
    /// F-13-5, the harm: under a size cap, an orphaned icon counted while the move is
    /// owed evicted a clip the library had room for.
    /// </summary>
    [Fact]
    public async Task WhileTheMoveIsOwed_NoClipIsEvictedToPayForIconBytes()
    {
        using var scope = new TemporaryDatabaseScope();
        var ids = await StartDeferredUpgradeAsync(scope, NoCaps);
        var orphaned = await CaptureAsync(scope, "copied with a large icon", 9, UniqueIcon(16_384, 2));
        await scope.ClipStoreService.DeleteAsync(orphaned.Id);
        var x = await CaptureAsync(scope, "first large clip", 520_000);
        var y = await CaptureAsync(scope, "second large clip", 520_000);

        // 1,040,000 + 915 of rows fits 1 MiB; with the 16 KiB orphan it does not.
        scope.SettingsService.SetCurrent(NoCaps with { EnableMaxLibrarySize = true, MaxLibrarySizeMegabytes = 1 });
        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.True(await scope.ClipStoreService.GetByIdAsync(x.Id) is not null, "A clip was evicted to pay for an orphaned icon while the move was owed.");
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(y.Id));
        Assert.Equal(ids.Length + 2, Scalar(scope, "SELECT COUNT(*) FROM clips;"));
    }

    /// <summary>
    /// F-13-5, the other side: while icons are out of the total, evicting the last
    /// clip that uses one frees nothing the total counted, so the eviction walk must
    /// not subtract the icon either - or it stops with the library still over the cap.
    /// </summary>
    [Fact]
    public async Task WhileTheMoveIsOwed_EvictingAnIconsLastClipFreesOnlyItsPayload()
    {
        using var scope = new TemporaryDatabaseScope();
        var ids = await StartDeferredUpgradeAsync(scope, NoCaps);
        var oldest = await CaptureAsync(scope, "small clip with the only copy of an icon", 100, UniqueIcon(65_536, 3));
        const int capBytes = 1_048_576;
        var large = await CaptureAsync(scope, "large clip", capBytes + 1000 - (ids.Length * (5 + Icon.Length)) - 100);

        // Rows are 1,000 bytes over the cap: evicting the small clip is not enough,
        // unless its 64 KiB icon is wrongly counted as freed.
        scope.SettingsService.SetCurrent(NoCaps with { EnableMaxLibrarySize = true, MaxLibrarySizeMegabytes = 1 });
        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(oldest.Id));
        Assert.True(await scope.ClipStoreService.GetByIdAsync(large.Id) is null, "The eviction stopped with the library over its cap: an icon was counted as freed while icons were out of the total.");
        Assert.True(result.TotalStoredBytes <= capBytes, $"The library is {result.TotalStoredBytes} bytes, over its {capBytes}-byte cap.");
    }

    /// <summary>
    /// Rubber-duck F-13-4: the move drops idx_clips_ocr_images, because the
    /// definition a schema-8 library has also required content_bytes, which the
    /// move clears on every row. The blocking start recreates it with its index
    /// DDL right after; the background run did not, so OCR backlog and coverage
    /// scans went without it for the rest of the session.
    /// </summary>
    [Fact]
    public async Task TheBackgroundMove_LeavesTheCurrentOcrIndexInPlace()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);
        Scalar(scope, """
            DROP INDEX idx_clips_ocr_images;
            CREATE INDEX idx_clips_ocr_images ON clips(content_type, ocr_status)
                WHERE content_type = 'image' AND content_bytes IS NOT NULL;
            """);

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        Assert.True(
            Scalar(scope, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_ocr_images' AND sql NOT LIKE '%content_bytes%';") == 1,
            "The background move left the library without the current idx_clips_ocr_images.");
    }

    /// <summary>
    /// Live check F2 (60k, schema 8): the deferred upgrade start spent 19.5-20.7 s
    /// building U-E1's icon-hash index, because building it reads every row of
    /// clips and before the move each row spans a page. After the move the same
    /// build took 0.85 s. So the start leaves it, a restart before the move has
    /// finished leaves it too, and the background run builds it after the move -
    /// and only then removes orphaned icons, which it finds through it.
    /// </summary>
    [Fact]
    public async Task ADeferredUpgradeStart_LeavesTheIconHashIndexToTheBackgroundRun()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 3);
        // Schema 8 never had the index; the scope's current-schema start made one.
        Scalar(scope, "DROP INDEX idx_clips_source_app_icon_hash;");
        Scalar(scope, "INSERT INTO app_icons (hash, png) VALUES ('orphan', x'00');");

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        Assert.Equal(0, AppIconHashIndexCount(scope));

        // Closed before the background run got anywhere: the version is current now,
        // but the move is still owed, and so is the index.
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        Assert.Equal(0, AppIconHashIndexCount(scope));
        Assert.Equal(ids.Length, InlineRows(scope));

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        Assert.Equal(0, InlineRows(scope));
        Assert.Equal(1, AppIconHashIndexCount(scope));
        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM app_icons WHERE hash = 'orphan';"));
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
        await AssertEveryClipReadsAsync(scope, ids);
    }

    /// <summary>The blocking start moves first, so it can build the index itself.</summary>
    [Fact]
    public async Task ABlockingUpgradeStart_BuildsTheIconHashIndexAfterItsMove()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3, version: 7);
        Scalar(scope, "DROP INDEX idx_clips_source_app_icon_hash;");

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        Assert.Equal(0, InlineRows(scope));
        Assert.Equal(1, AppIconHashIndexCount(scope));
    }
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

    /// <summary>
    /// Owner decision on the integrity check (option b): a deferred upgrade start
    /// does not read the whole file to check it - that was 71.5 s of a 73 s start
    /// on the cold 1 GB fixture. A page nothing on that start reads is corrupted,
    /// so only the full-file check could notice it.
    /// </summary>
    [Fact]
    public async Task ADeferredUpgradeStart_DoesNotReadEveryPageToCheckIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);
        await SeedFillerPagesAsync(scope);
        CorruptTheMiddleOfTheFile(scope);

        var steps = await InitializeTracingStepsAsync(scope, allowDeferredPayloadMove: true);

        Assert.DoesNotContain(steps, s => s.Contains("[init-timing] integrity-check @", StringComparison.Ordinal));
        Assert.Equal(CurrentSchemaVersion, Scalar(scope, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';"));
    }

    /// <summary>
    /// The check is not dropped, it is moved: the background move runs it before it
    /// rewrites anything, and a corrupt library is not touched.
    /// </summary>
    [Fact]
    public async Task TheBackgroundMove_ChecksTheLibraryFirst_AndMovesNothingWhenItIsCorrupt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 3);
        GiveTimestampsAnOffset(scope, ids[0]);
        await SeedFillerPagesAsync(scope);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        CorruptTheMiddleOfTheFile(scope);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        Assert.Contains("Database integrity check failed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ids.Length, InlineRows(scope));
        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        Assert.Equal(OffsetTimestamps, Timestamps(scope, ids[0]));
    }

    /// <summary>
    /// Rubber-duck F-13-1: the background upgrade holds a connection of its own, so
    /// a whole-database operation has to pause it as it pauses the other workers.
    /// A restore renames the live file aside, which fails while any handle is open
    /// on it (Windows) or leaves the upgrader writing to the renamed file
    /// (elsewhere). Paused, the run resumes after the restore, from the start and
    /// against the library that is live by then.
    /// </summary>
    [Fact]
    public async Task ARestoreDuringTheBackgroundMove_PausesTheMove_WhichFinishesOnTheRestoredLibrary()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 4);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        // The library as it is before anything moved is what gets restored.
        var backupPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scope.DatabasePath)!, "before-move.db");
        Scalar(scope, $"VACUUM INTO '{backupPath.Replace("'", "''", StringComparison.Ordinal)}';");

        using var held = new SemaphoreSlim(0);
        var heldTokens = new ConcurrentQueue<CancellationToken>();
        using var closing = new CancellationTokenSource();
        DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.Zero;
        DatabaseInitializer.BetweenBackgroundPayloadBatches = async token =>
        {
            heldTokens.Enqueue(token);
            held.Release();
            await Task.Delay(Timeout.Infinite, token);
        };
        try
        {
            var move = Task.Run(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null, closing.Token));
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20)), "The move never reached its first pause.");

            // Only the invocation already holding stays held; a resumed run goes through.
            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;

            var backup = new Clipthrough.Services.DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService, databaseInitializer: scope.DatabaseInitializer);
            await backup.RestoreAsync(backupPath);

            Assert.True(heldTokens.TryPeek(out var heldToken) && heldToken.IsCancellationRequested, "The restore ran without stopping the background move.");
            Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(30)), "The move did not run after the restore.");
            Assert.Equal(0, InlineRows(scope));
            Assert.Equal(ids.Length, Scalar(scope, "SELECT COUNT(*) FROM clip_payloads;"));
            await AssertEveryClipReadsAsync(scope, ids);
        }
        finally
        {
            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.FromMilliseconds(100);
            closing.Cancel();
        }
    }

    /// <summary>
    /// Rubber-duck F-13-2: on a deferred start the UTC backfill waits for the
    /// background, but startup maintenance runs at once, and the lifetime purge
    /// compares timestamps as text - right only for UTC (F-35). Until the backfill
    /// has run it has to compare instants: a clip copied ten minutes ago in a
    /// -05:00 zone reads as five hours older, and one copied two hours ago at
    /// +05:00 reads as three hours in the future.
    /// </summary>
    [Fact]
    public async Task WhileTheUtcBackfillIsOwed_TheLifetimePurgeComparesInstants_NotOffsetText()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedSchema8Library(scope, 2);
        var now = DateTimeOffset.UtcNow;
        var fresh = now.AddMinutes(-10).ToOffset(TimeSpan.FromHours(-5)).ToString("O", CultureInfo.InvariantCulture);
        var expired = now.AddHours(-2).ToOffset(TimeSpan.FromHours(5)).ToString("O", CultureInfo.InvariantCulture);
        Scalar(scope, $"UPDATE clips SET is_sensitive = 1, last_copied_at = '{fresh}' WHERE id = {ids[0]};");
        Scalar(scope, $"UPDATE clips SET is_sensitive = 1, last_copied_at = '{expired}' WHERE id = {ids[1]};");

        // The fixture only discriminates while text and instant order disagree.
        var cutoff = now.AddMinutes(-60).ToString("O", CultureInfo.InvariantCulture);
        Assert.True(string.CompareOrdinal(fresh, cutoff) < 0, "As text, the fresh clip must look expired.");
        Assert.True(string.CompareOrdinal(expired, cutoff) > 0, "As text, the expired clip must look fresh.");

        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_metadata WHERE key = 'utc_backfill_pending';"));
        scope.SettingsService.SetCurrent(new AppSettings
        {
            EnableSensitiveClipLifetime = true,
            SensitiveClipLifetimeMinutes = 60,
            EnableNormalClipLifetime = false,
            EnableMaxEntryCount = false,
            EnableMaxLibrarySize = false,
        });

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.True(Scalar(scope, $"SELECT COUNT(*) FROM clips WHERE id = {ids[0]};") == 1, "A sensitive clip copied ten minutes ago was purged as expired.");
        Assert.True(Scalar(scope, $"SELECT COUNT(*) FROM clips WHERE id = {ids[1]};") == 0, "A sensitive clip copied two hours ago outlived its one-hour lifetime.");
    }

    /// <summary>
    /// Rubber-duck F-13-3: the background UTC backfill reads the rows it will
    /// convert, then writes them, and captures run in between. A re-copy sets
    /// captured_at and last_copied_at, and the backfill wrote its stale converted
    /// values over them, so the clip lost its recent position and could expire as
    /// if never copied again. A column the re-copy did not touch is still
    /// converted.
    /// </summary>
    [Fact]
    public async Task ARecopyDuringTheBackgroundUtcBackfill_KeepsItsNewTimestamps()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 2);
        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "copied before the upgrade",
            ContentBytes = Encoding.UTF8.GetBytes("copied before the upgrade"),
        };
        var clip = await scope.ClipStoreService.CaptureFastAsync(request);
        Assert.NotNull(clip);
        GiveTimestampsAnOffset(scope, clip.Id);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        string? recopiedAt = null;
        DatabaseInitializer.BeforeUtcBackfillWrites = async token =>
        {
            await scope.ClipStoreService.CaptureFastAsync(request, token);
            recopiedAt = Timestamps(scope, clip.Id)[2];
        };
        try
        {
            Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        }
        finally
        {
            DatabaseInitializer.BeforeUtcBackfillWrites = null;
        }

        Assert.NotNull(recopiedAt);
        Assert.NotEqual(RepairedTimestamps[2], recopiedAt);
        Assert.Equal(2, Scalar(scope, $"SELECT copy_count FROM clips WHERE id = {clip.Id};"));
        var after = Timestamps(scope, clip.Id);
        Assert.True(after[2] == recopiedAt, $"The backfill overwrote the re-copy's last_copied_at {recopiedAt} with {after[2]}.");
        Assert.Equal(recopiedAt, after[0]);
        Assert.Equal(RepairedTimestamps[1], after[1]);
    }

    /// <summary>
    /// Rubber-duck F-13-7: a row the move has not reached still holds its icon
    /// inline, counted in its stored_bytes as schema 8 did. Writing the row's
    /// byte_size (an edit, a re-copy) fired the trigger that sets stored_bytes =
    /// byte_size, which dropped that still-live icon from the size total until
    /// the move reached the row. A write that sets an icon of its own makes the
    /// inline one stale, and then it is rightly not counted: app_icons has the new one.
    /// </summary>
    [Fact]
    public async Task WritingARowTheMoveHasNotReached_KeepsCountingItsInlineIcon()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });
        var ids = SeedSchema8Library(scope, 3);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);

        byte[] edited = [1, 2, 3, 4, 5, 6, 7];
        await scope.ClipStoreService.UpdateDeferredContentAsync(ids[0], new ClipCaptureRequest
        {
            ContentText = "edited mid-upgrade",
            ContentBytes = edited,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
        });
        Assert.Equal(Icon.Length, Scalar(scope, $"SELECT LENGTH(source_app_icon) FROM clips WHERE id = {ids[0]} AND source_app_icon_hash IS NULL;"));
        Assert.True(
            Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {ids[0]};") == edited.Length + Icon.Length,
            "An edit to a row the move has not reached dropped its still-inline icon from the size total.");

        // A re-copy that brings an icon sets the hash and byte_size in one statement.
        Scalar(scope, $"INSERT INTO app_icons (hash, png) VALUES ('newer', x'0102'); UPDATE clips SET source_app_icon_hash = 'newer', byte_size = {edited.Length} WHERE id = {ids[1]};");
        Assert.Equal(edited.Length, Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {ids[1]};"));

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        Assert.Equal(edited.Length, Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {ids[0]};"));
    }

    /// <summary>
    /// With the sensitive-clip lifetime on, the backup also purges and vacuums its
    /// copy, and on a damaged library that fails by itself - so "no backup file"
    /// would hold whether or not the integrity guard ran. With the lifetime off the
    /// copy of the damaged file succeeds, and only the guard keeps it from being
    /// published: the case Finding 4b is about.
    /// </summary>
    private static Task KeepBackupCopyingTheDamagedLibraryAsync(TemporaryDatabaseScope scope) =>
        scope.SettingsService.SaveAsync(scope.SettingsService.Current with { EnableSensitiveClipLifetime = false });
    private static string BackupDirectory(TemporaryDatabaseScope scope) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scope.DatabasePath)!, "backups");

    private static string TodaysBackup(TemporaryDatabaseScope scope) =>
        System.IO.Path.Combine(BackupDirectory(scope), $"clipthrough-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.db");

    /// <summary>
    /// Live check F3: the daily backup was written from the library seconds after it
    /// failed its background quick_check, and rotation keeps only a week - so a few
    /// days on a damaged library would replace every good backup with a copy of the
    /// damage. A library that failed its check this session is not backed up, and
    /// nothing is pruned.
    /// </summary>
    [Fact]
    public async Task TheDailyBackup_IsSkipped_AfterTheLibraryFailedItsCheck_AndKeepsTheOlderBackups()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);
        await SeedFillerPagesAsync(scope);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        CorruptTheMiddleOfTheFile(scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));

        // More than the retention keeps, so a backup run would also prune.
        System.IO.Directory.CreateDirectory(BackupDirectory(scope));
        var older = Enumerable.Range(1, Clipthrough.Services.DatabaseBackupService.DefaultRetention + 1)
            .Select(d => System.IO.Path.Combine(BackupDirectory(scope), $"clipthrough-202501{d:00}.db"))
            .ToArray();
        foreach (var path in older)
        {
            System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        }

        await KeepBackupCopyingTheDamagedLibraryAsync(scope);
        var backup = new Clipthrough.Services.DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService, databaseInitializer: scope.DatabaseInitializer);
        await backup.EnsureDailyBackupAsync();

        Assert.False(System.IO.File.Exists(TodaysBackup(scope)), "A backup was taken from a library that failed its integrity check.");
        Assert.All(older, path => Assert.True(System.IO.File.Exists(path), $"{System.IO.Path.GetFileName(path)} was pruned."));
    }

    /// <summary>
    /// The startup backup reads two things the background check writes: whether
    /// the check failed, and whether it is still owed. A failing check sets the
    /// first, then clears the second. Read in that same order, a check that lands
    /// between the two reads is seen as neither failed nor owed, and the backup
    /// copies the damaged library (Finding 4b: never back up a library that failed
    /// its check this session).
    /// </summary>
    [Fact]
    public async Task TheDailyBackup_IsNotTaken_WhenTheCheckFailsBetweenItsTwoReads()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);
        await SeedFillerPagesAsync(scope);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        CorruptTheMiddleOfTheFile(scope);
        Assert.True(scope.DatabaseInitializer.IsIntegrityCheckOwed);

        await KeepBackupCopyingTheDamagedLibraryAsync(scope);
        var checkRan = false;
        Clipthrough.Services.DatabaseBackupService.BetweenIntegrityStateReads = async () =>
        {
            checkRan = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        };
        try
        {
            var backup = new Clipthrough.Services.DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService, databaseInitializer: scope.DatabaseInitializer);
            await backup.EnsureDailyBackupAsync();
        }
        finally
        {
            Clipthrough.Services.DatabaseBackupService.BetweenIntegrityStateReads = null;
        }

        Assert.True(scope.DatabaseInitializer.HasFailedIntegrityCheck || !checkRan, "The forced check did not fail, so this proves nothing.");
        Assert.False(System.IO.File.Exists(TodaysBackup(scope)), "A daily backup was taken from a library that failed its integrity check");
    }

    /// <summary>
    /// The startup backup ran while the background check was still reading the file
    /// (F2: written at t+43 s, the move began at t+46 s), so it could copy a library
    /// about to fail. Until the owed check has passed it waits; afterwards it runs.
    /// </summary>
    [Fact]
    public async Task TheDailyBackup_WaitsForTheBackgroundCheck_ThenRuns()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSchema8Library(scope, 3);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        var backup = new Clipthrough.Services.DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService, databaseInitializer: scope.DatabaseInitializer);

        await backup.EnsureDailyBackupAsync();
        Assert.False(System.IO.File.Exists(TodaysBackup(scope)), "The backup ran before the owed integrity check.");

        Assert.True(await scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
        await backup.EnsureDailyBackupAsync();
        Assert.True(System.IO.File.Exists(TodaysBackup(scope)));
    }

    /// <summary>
    /// Pages of a table of the test's own, so the file is large enough that the
    /// middle of it lands in a page no upgrade step reads.
    /// </summary>
    private static async Task SeedFillerPagesAsync(TemporaryDatabaseScope scope)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS integrity_test_filler (id INTEGER PRIMARY KEY, payload TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = connection.BeginTransaction();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO integrity_test_filler (payload) VALUES ($payload);";
        var payload = insert.Parameters.Add("$payload", Microsoft.Data.Sqlite.SqliteType.Text);
        var filler = new string('x', 400);
        for (var i = 0; i < 3_000; i++)
        {
            payload.Value = filler;
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static void CorruptTheMiddleOfTheFile(TemporaryDatabaseScope scope)
    {
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var file = new System.IO.FileStream(scope.DatabasePath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
        file.Seek(file.Length / 2, System.IO.SeekOrigin.Begin);
        var garbage = new byte[256];
        Array.Fill(garbage, (byte)0xDE);
        file.Write(garbage);
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
