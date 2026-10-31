using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-25. With nothing pending, the startup sensitivity recovery and the OCR
/// coverage aggregate still read every clip: 2.9 s and 1.9 s at 60k clips
/// (16 s and 9.9 s encrypted). Partial indexes reach only the rows they count.
/// Plans are read from the queries the store runs, and asserted by index name.
/// </summary>
public sealed class StartupScanIndexTests
{
    private static async Task<IReadOnlyList<string>> PlanAsync(TemporaryDatabaseScope scope, string sql)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 2000)
                INSERT INTO clips (hash, content_type, content_format, content, content_bytes, byte_size,
                                   captured_at, first_copied_at, last_copied_at, sensitivity_scanned_at, ocr_status)
                SELECT 'h' || i, CASE WHEN i % 10 = 0 THEN 'image' ELSE 'text' END, 'text', 'body ' || i, x'00', 1,
                       datetime('now'), datetime('now'), datetime('now'), datetime('now'),
                       CASE WHEN i % 10 = 0 THEN 'succeeded' END
                FROM n;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) steps.Add(reader.GetString(3));
        return steps;
    }

    [Fact]
    public async Task PendingSensitivityRecovery_ReadsOnlyUnscannedRows()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var plan = await PlanAsync(scope, ClipStoreService.PendingSensitivitySql);

        Assert.True(plan.Any(s => s.Contains("idx_clips_sensitivity_pending", StringComparison.Ordinal)), string.Join(" | ", plan));
    }

    [Fact]
    public async Task OcrCoverage_IsAnsweredFromTheImageIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var plan = await PlanAsync(scope, ClipStoreService.OcrCoverageSql);

        // Not COVERING on the bundled SQLite: the row is still visited for the
        // content_bytes IS NOT NULL term. ocr_status - stored after the payload,
        // so reading it from the row walked the overflow chain - comes from the index.
        Assert.True(plan.Any(s => s.Contains("USING INDEX idx_clips_ocr_images", StringComparison.Ordinal) || s.Contains("USING COVERING INDEX idx_clips_ocr_images", StringComparison.Ordinal)), string.Join(" | ", plan));
    }
}