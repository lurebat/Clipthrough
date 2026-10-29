using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-24. "Re-run all embeddings" held the write lock for one transaction over
/// the whole library - 74 s at 60k clips - and a capture issued a second in
/// failed after busy_timeout with 'database is locked', losing that clipboard
/// entry. A sensitivity-rule save rewrote every row (15 s, 277 MB) to reset a
/// flag that almost never changes.
/// </summary>
public sealed class LongWriteLockTests
{
    private static async Task SeedAsync(TemporaryDatabaseScope scope, int rows)
    {
        await scope.DatabaseInitializer.InitializeAsync();
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $rows)
            INSERT INTO clips (hash, content_type, content_format, content, byte_size,
                               captured_at, first_copied_at, last_copied_at, is_sensitive,
                               sensitivity_scanned_at, embedding_status)
            SELECT 'h' || i, 'text', 'text', 'ordinary body ' || i, 16,
                   datetime('now'), datetime('now'), datetime('now'), 0, datetime('now'), 'succeeded'
            FROM n;
            INSERT INTO clip_embeddings (clip_id, model_version, dimensions, vector, created_at)
            SELECT id, 'm', 1, zeroblob(4), 'x' FROM clips;
            """;
        command.Parameters.AddWithValue("$rows", rows);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(TemporaryDatabaseScope scope, string sql)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ACaptureDuringRerunAll_CommitsBetweenBatches()
    {
        using var scope = new TemporaryDatabaseScope();
        await SeedAsync(scope, 5000);
        var store = scope.ClipStoreService;
        store.RerunBatchSize = 1000;
        var batches = 0;
        long? capturedId = null;
        var captureMs = -1L;
        store.AfterRerunBatchCommitted = async () =>
        {
            if (++batches != 1) return;
            var sw = Stopwatch.StartNew();
            var clip = await store.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "copied during rerun-all",
                ContentBytes = Encoding.UTF8.GetBytes("copied during rerun-all"),
                SkipPostInsertMaintenance = true,
            });
            captureMs = sw.ElapsedMilliseconds;
            capturedId = clip?.Id;
        };

        var ids = await store.MarkAllEmbeddingsForRerunAsync();

        Assert.True(batches >= 5, $"rerun-all committed {batches} time(s); it holds the write lock until it commits.");
        Assert.NotNull(capturedId);
        Assert.True(captureMs < 2000, $"the capture waited {captureMs} ms");
        Assert.Equal(5000, ids.Count);
        Assert.Equal(0, await Scalar(scope, "SELECT count(*) FROM clip_embeddings WHERE clip_id <= 5000;"));
        Assert.Equal(5000, await Scalar(scope, "SELECT count(*) FROM clips WHERE id <= 5000 AND embedding_status = 'rerun' AND embedding_attempts = 0;"));
    }

    [Fact]
    public async Task ARebuildThatChangesNoVerdict_WritesNoClipRow()
    {
        using var scope = new TemporaryDatabaseScope();
        await SeedAsync(scope, 500);
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE clips SET is_sensitive = 1, sensitivity_is_manual = 1, embedding_status = 'excluded' WHERE id = 7;
                DELETE FROM clip_embeddings WHERE clip_id = 7;
                CREATE TABLE clip_write_log (n INTEGER);
                CREATE TRIGGER count_clip_writes AFTER UPDATE ON clips BEGIN INSERT INTO clip_write_log VALUES (1); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await scope.ClipStoreService.RebuildSensitivityMatchesAsync();

        Assert.Equal(0, await Scalar(scope, "SELECT count(*) FROM clip_write_log;"));
        Assert.Equal(1, await Scalar(scope, "SELECT is_sensitive FROM clips WHERE id = 7;"));
        Assert.Equal(499, await Scalar(scope, "SELECT count(*) FROM clips WHERE is_sensitive = 0;"));
    }
}