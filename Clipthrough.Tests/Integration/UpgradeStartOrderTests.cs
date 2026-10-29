using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Option A of the upgrade-start research (docs/plans/2026-10-28-001-...): the
/// first start after an upgrade moves payloads out of the clips row before any
/// step that scans the table, and skips the backfills a library cannot need.
/// </summary>
/// <remarks>
/// While payloads are still inline, every full-table scan walks each row's
/// overflow chain (F-06); after the move the same scans read small rows. The
/// backfills exist for libraries from before the version gate (schema 1) or
/// before byte_size (schema 5), and dedupe only matters until the unique hash
/// index exists.
/// </remarks>
public sealed class UpgradeStartOrderTests
{
    private static long Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql, Action<Microsoft.Data.Sqlite.SqliteCommand>? bind = null)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    /// <summary>A row in the layout before U-E1: payload inline, no text, and byte_size never filled.</summary>
    private static long InsertLegacyImage(TemporaryDatabaseScope scope, string hash, int payloadLength, long byteSize = 0)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clips (content, content_bytes, content_type, content_format, source_app,
                hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
            VALUES (NULL, zeroblob($n), 'image', 'bitmap', 'Paint', $h, $t, $t, $t, $size, $t);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$n", payloadLength);
        command.Parameters.AddWithValue("$h", hash);
        command.Parameters.AddWithValue("$size", byteSize);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetStoredSchemaVersion(TemporaryDatabaseScope scope, int version) =>
        Execute(scope, $"UPDATE app_metadata SET value = '{version.ToString(CultureInfo.InvariantCulture)}' WHERE key = 'schema_version'; DELETE FROM app_metadata WHERE key = 'payload_move_last_id';");

    private static async Task<string[]> InitializeTracingStepsAsync(TemporaryDatabaseScope scope)
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

        return sink.Where(m => m.Contains("[init-timing]", StringComparison.Ordinal)).ToArray();
    }

    private static int StepIndex(string[] steps, string name)
    {
        var index = Array.FindIndex(steps, s => s.Contains($"[init-timing] {name} @", StringComparison.Ordinal));
        Assert.True(index >= 0, $"Step '{name}' did not run. Steps: {string.Join(" | ", steps)}");
        return index;
    }

    /// <summary>
    /// The trap the reorder sets: the byte_size backfill fell back to
    /// LENGTH(content_bytes), and once the move has run that column is NULL. Sizes
    /// recorded as zero would also make the library size cap undercount.
    /// </summary>
    [Fact]
    public async Task ARowWithoutASize_GetsItsPayloadLength_ThoughThePayloadHasMoved()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var id = InsertLegacyImage(scope, "legacy-unsized", payloadLength: 5000);
        SetStoredSchemaVersion(scope, 4);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(0, Scalar(scope, $"SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL;"));
        Assert.Equal(5000, Scalar(scope, $"SELECT byte_size FROM clips WHERE id = {id};"));
        Assert.Equal(5000, Scalar(scope, $"SELECT stored_bytes FROM clips WHERE id = {id};"));
    }

    [Fact]
    public async Task ThePayloadMove_RunsBeforeEveryStepThatScansTheClipsTable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        InsertLegacyImage(scope, "legacy-a", payloadLength: 100);
        Execute(scope, "DROP INDEX IF EXISTS idx_clips_hash_unique;");
        SetStoredSchemaVersion(scope, 0);

        var steps = await InitializeTracingStepsAsync(scope);

        var move = StepIndex(steps, "move-payloads");
        Assert.True(move < StepIndex(steps, "ensure-stored-bytes"), "stored-bytes scanned the table before the move.");
        Assert.True(move < StepIndex(steps, "backfill-aggregation"), "the aggregation backfill scanned the table before the move.");
        Assert.True(move < StepIndex(steps, "backfill-payload"), "the payload backfill scanned the table before the move.");
        Assert.True(move < StepIndex(steps, "dedupe-by-hash"), "dedupe scanned the table before the move.");
    }

    /// <summary>
    /// An upgrade from a library that already has every backfill applied runs
    /// none of them. Asserted on the trace: a skipped backfill and one that ran
    /// over clean rows leave identical data.
    /// </summary>
    [Fact]
    public async Task AnUpgradeFromARecentVersion_SkipsTheBackfillsOnlyOlderLibrariesNeed()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        InsertLegacyImage(scope, "recent", payloadLength: 100, byteSize: 100);
        SetStoredSchemaVersion(scope, 8);

        var steps = await InitializeTracingStepsAsync(scope);

        StepIndex(steps, "move-payloads");
        StepIndex(steps, "backfill-aggregation skipped");
        StepIndex(steps, "backfill-payload skipped");
        StepIndex(steps, "dedupe-by-hash skipped");
    }

    /// <summary>The aggregation backfill still runs for a library from before the version gate.</summary>
    [Fact]
    public async Task ALibraryFromBeforeTheVersionGate_StillGetsTheAggregationBackfill()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var id = InsertLegacyImage(scope, "pre-gate", payloadLength: 100, byteSize: 100);
        Execute(scope, $"UPDATE clips SET copy_count = 0, first_copied_at = '' WHERE id = {id};");
        SetStoredSchemaVersion(scope, 0);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(1, Scalar(scope, $"SELECT copy_count FROM clips WHERE id = {id};"));
        Assert.Equal(1, Scalar(scope, $"SELECT first_copied_at = captured_at FROM clips WHERE id = {id};"));
    }

    /// <summary>Dedupe still runs while the unique hash index is missing, whatever the version.</summary>
    [Fact]
    public async Task ALibraryWithoutTheUniqueHashIndex_IsStillDeduplicated()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        Execute(scope, "DROP INDEX IF EXISTS idx_clips_hash_unique;");
        InsertLegacyImage(scope, "same-hash", payloadLength: 100, byteSize: 100);
        InsertLegacyImage(scope, "same-hash", payloadLength: 100, byteSize: 100);
        SetStoredSchemaVersion(scope, 8);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM clips WHERE hash = 'same-hash';"));
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_hash_unique';"));
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
