using System;
using System.Collections.Generic;
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

    // Mirrors ClipStoreService.ClaimPendingEmbeddingsAsync. Kept literal rather than
    // shared, so that changing the real query without re-checking its plan fails here.
    private const string ClaimQuery = """
        SELECT id, (CASE WHEN content_type = 'image' THEN ocr_text ELSE content END) AS etext
        FROM clips
        WHERE (embedding_status IS NULL OR embedding_status IN ('pending','rerun')
               OR (embedding_status = 'failed' AND embedding_attempts < 3))
          AND is_sensitive = 0
          AND sensitivity_scanned_at IS NOT NULL
          AND (
              (content_type IN ('text','richtext','files') AND content IS NOT NULL AND TRIM(content) <> '')
              OR (content_type = 'image' AND ocr_status = 'succeeded' AND ocr_text IS NOT NULL AND TRIM(ocr_text) <> '')
          )
        ORDER BY COALESCE(last_copied_at, captured_at) DESC
        LIMIT 32;
        """;

    [Fact]
    public async Task EmbeddingClaim_DoesNotScanTheWholeTable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, 4000);

        var plan = await PlanAsync(scope, ClaimQuery);

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN clips", StringComparison.Ordinal));
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
