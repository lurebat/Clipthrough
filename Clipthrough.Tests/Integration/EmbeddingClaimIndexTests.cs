using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// The embedding worker is poked once per captured clip, so its claim query runs
/// on every copy the user makes. It used to be unservable by any index - the
/// backlog includes <c>embedding_status IS NULL</c> and the index was partial on
/// <c>IS NOT NULL</c> - which made every claim a full table scan: 124 ms at 60k
/// rows even when there was nothing at all to embed.
/// </summary>
public class EmbeddingClaimIndexTests
{
    // Seeded, but deliberately never ANALYZEd: the application does not run ANALYZE,
    // so production databases carry no sqlite_stat1 and the planner works from its
    // default selectivity estimates. Collecting statistics here would test a planner
    // state that no user ever has. On an empty table the planner will pick any index
    // at all, so some rows are needed for the plan to mean anything.
    private static async Task SeedAsync(TemporaryDatabaseScope scope, int rows)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $rows)
                INSERT INTO clips (hash, content_type, content_format, content, byte_size,
                                   captured_at, first_copied_at, last_copied_at, is_sensitive,
                                   sensitivity_scanned_at, embedding_status)
                SELECT 'h' || i, 'text', 'plain', 'body ' || i, 32,
                       datetime('now', '-' || i || ' seconds'),
                       datetime('now', '-' || i || ' seconds'),
                       datetime('now', '-' || i || ' seconds'),
                       0, datetime('now'), 'succeeded'
                FROM n;
                """;
            insert.Parameters.AddWithValue("$rows", rows);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
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
    public async Task EmbeddingClaim_DoesNotScanTheWholeTable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, 4000);

        var plan = await PlanAsync(scope, Clipthrough.Services.ClipStoreService.EmbeddingClaimSql
            .Replace("$maxAttempts", "3", StringComparison.Ordinal)
            .Replace("$limit", "32", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN clips", StringComparison.Ordinal));
    }

    /// <summary>
    /// F-05. "Not a full scan" was satisfied by the plan the planner actually
    /// chose at 60k clips - idx_clips_retention on is_sensitive plus a temp
    /// B-tree, which still visits every row: 2.3 s per claim with nothing to
    /// claim, 17 s encrypted. Only the backlog index is acceptable, so assert it
    /// by name, against the query the store really runs.
    /// </summary>
    [Fact]
    public async Task EmbeddingClaim_UsesTheBacklogIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, 4000);

        var sql = Clipthrough.Services.ClipStoreService.EmbeddingClaimSql
            .Replace("$maxAttempts", "3", StringComparison.Ordinal)
            .Replace("$limit", "32", StringComparison.Ordinal);
        var plan = await PlanAsync(scope, sql);

        Assert.Contains(plan, step => step.Contains("idx_clips_embedding_backlog", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("idx_clips_retention", StringComparison.Ordinal));
    }

    /// <summary>
    /// F-05. The claim's SELECT ran inside BEGIN IMMEDIATE, so every claim - one
    /// per capture, and every 30 s while idle - held the write lock for the
    /// length of the scan and a capture waited behind it. With nothing to claim
    /// it now never asks for the write lock at all.
    /// </summary>
    [Fact]
    public async Task AClaimWithNothingPending_DoesNotWaitForTheWriteLock()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, 100);

        await using var writer = scope.ConnectionFactory.CreateConnection();
        await writer.OpenAsync();
        await using var held = (Microsoft.Data.Sqlite.SqliteTransaction)await writer.BeginTransactionAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(32);
        sw.Stop();

        Assert.Empty(claimed);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"The claim waited {sw.ElapsedMilliseconds} ms for a write lock it did not need.");
    }

    [Fact]
    public async Task AClaim_MarksOnlyTheRowsItReturns_AsProcessing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, 50);
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using (var pending = connection.CreateCommand())
        {
            pending.CommandText = "UPDATE clips SET embedding_status = 'pending' WHERE id <= 10;";
            await pending.ExecuteNonQueryAsync();
        }

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(4);

        Assert.Equal(4, claimed.Count);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT id FROM clips WHERE embedding_status = 'processing' ORDER BY id;";
        var processing = new List<long>();
        await using (var reader = await check.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) processing.Add(reader.GetInt64(0));
        }

        Assert.Equal(claimed.Select(c => c.ClipId).Order(), processing);
    }

    [Fact]
    public async Task Initialize_DropsTheOldPartialIndexFromExistingDatabases()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        // Recreate the index an already-installed copy of the app is carrying, and wind
        // the stored schema version back so initialization takes the upgrade path the
        // way a real installed copy will. Without the version bump the migration gate
        // skips every Ensure* helper and the stale index survives forever.
        await using (var recreate = connection.CreateCommand())
        {
            recreate.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_clips_embedding_status ON clips(embedding_status) WHERE embedding_status IS NOT NULL;
                UPDATE app_metadata SET value = '6' WHERE key = 'schema_version';
                """;
            await recreate.ExecuteNonQueryAsync();
        }

        await scope.DatabaseInitializer.InitializeAsync();

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_index_list('clips') WHERE name = 'idx_clips_embedding_status';";
        Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task EmbeddingBacklogIndex_CoversRowsWithNoStatusYet()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        // A partial "WHERE embedding_status IS NOT NULL" index omits exactly the rows
        // the claim is looking for, so the query planner cannot use it at all.
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE((SELECT partial FROM pragma_index_list('clips') WHERE name = 'idx_clips_embedding_backlog'), -1);";
        var partial = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(0, partial);
    }
}
