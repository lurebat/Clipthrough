using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Integration tests that verify the SQLite concurrency contract:
/// - Private cache (no shared cache) so contention is SQLITE_BUSY, not SQLITE_LOCKED.
/// - busy_timeout=5000 applied to every connection opened via SqliteConnectionFactory.
/// - Parallel writes from multiple tasks succeed without "database is locked" or hangs.
/// </summary>
public sealed class SqliteConcurrencyTests
{
    [Fact]
    public async Task Factory_AppliesBusyTimeout_OnOpen()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        // Intercept the PRAGMA busy_timeout value issued by the factory.
        long? observedTimeout = null;

        await using var conn = scope.ConnectionFactory.CreateConnection();
        conn.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
            {
                // busy_timeout was already issued by the factory's own StateChange handler.
                // Read it back to verify.
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA busy_timeout;";
                var result = cmd.ExecuteScalar();
                if (result is not null)
                    observedTimeout = Convert.ToInt64(result);
            }
        };
        await conn.OpenAsync();

        // The factory sets 5000 ms.
        Assert.Equal(5000L, observedTimeout);
    }

    /// <summary>
    /// The read-only overload exists so maintenance probes stop hand-rolling
    /// their own connection string. It is only worth having if it carries the
    /// same busy_timeout - a probe that skips it fails immediately with
    /// "database is locked" the moment a background worker holds the write
    /// lock, instead of waiting the five seconds every other connection waits.
    /// </summary>
    [Fact]
    public async Task ReadOnlyFactory_AppliesBusyTimeout_OnOpen()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        long? observedTimeout = null;

        await using var conn = scope.ConnectionFactory.CreateReadOnlyConnection();
        conn.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA busy_timeout;";
                var result = cmd.ExecuteScalar();
                if (result is not null)
                {
                    observedTimeout = Convert.ToInt64(result);
                }
            }
        };
        await conn.OpenAsync();

        Assert.Equal(5000L, observedTimeout);
    }

    /// <summary>
    /// Read-only rather than read-write-create is the whole point for a probe:
    /// pointed at a path with no database, it must fail rather than quietly
    /// create an empty file and let the caller report a perfectly healthy
    /// database that contains none of the user's clips.
    /// </summary>
    [Fact]
    public async Task ReadOnlyFactory_MissingDatabase_FailsInsteadOfCreatingOne()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        // Same (existing) directory, no such file - the realistic misconfiguration.
        var missing = scope.DatabasePath + ".nonexistent";
        Assert.False(System.IO.File.Exists(missing));

        var factory = new SqliteConnectionFactory(new TestStorageOptionsService(missing));
        await using var conn = factory.CreateReadOnlyConnection();

        await Assert.ThrowsAsync<SqliteException>(() => conn.OpenAsync());
        Assert.False(System.IO.File.Exists(missing));
    }

    [Fact]
    public async Task ParallelWrites_EmbeddingStyleBatchSaveAndCapture_AllSucceedWithoutLockErrors()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        // Pre-seed clips to claim for embedding.
        const int seedCount = 10;
        var seeded = new List<ClipEntry>();
        for (var i = 0; i < seedCount; i++)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"concurrency seed {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"concurrency seed {i}"),
                SourceApp = "ConcurrencyTest",
            });
            Assert.NotNull(clip);
            seeded.Add(clip!);
        }

        var candidates = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(seedCount);
        Assert.Equal(seedCount, candidates.Count);

        // Task A: simulate EmbeddingWorker-style SaveEmbeddingBatchAsync writes.
        var saveEmbeddings = Task.Run(async () =>
        {
            var records = candidates
                .Select(c => new ClipEmbeddingRecord(c.ClipId, [1f, 0f, 0f, 0f]))
                .ToArray();
            await scope.ClipStoreService.SaveEmbeddingBatchAsync(records, "test-model-v1");
        });

        // Tasks B-D: concurrent CaptureAsync calls from multiple "UI" tasks.
        var captures = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            var result = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"parallel capture {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"parallel capture {i}"),
                SourceApp = "ConcurrencyTest",
            });
            Assert.NotNull(result);
        })).ToArray();

        // Collect all exceptions; none should contain "locked".
        var allTasks = new Task[] { saveEmbeddings }.Concat(captures).ToArray();

        Exception? thrown = null;
        try
        {
            await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is not null)
        {
            var msg = thrown.Message + (thrown.InnerException?.Message ?? string.Empty);
            Assert.DoesNotContain("database is locked", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("database table is locked", msg, StringComparison.OrdinalIgnoreCase);
            // Re-throw to fail the test with the real error if it wasn't a lock error.
            throw thrown;
        }

        // Verify embeddings were persisted.
        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(seedCount, coverage.Embedded);
    }

    [Fact]
    public async Task ParallelCaptures_NoSharedCacheLockedException()
    {
        // This test specifically exercises the private-cache fix (KTD4):
        // with shared cache, concurrent writers from multiple connections would
        // raise SQLITE_LOCKED (not retried by busy_timeout). With private cache
        // they get SQLITE_BUSY, which the 5s busy_timeout retries transparently.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        const int parallelism = 20;
        var tasks = Enumerable.Range(0, parallelism).Select(i => Task.Run(async () =>
        {
            var result = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"stress capture {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"stress capture {i}"),
                SourceApp = "StressTest",
            });
            Assert.NotNull(result);
            return result;
        })).ToArray();

        Exception? thrown = null;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is not null)
        {
            var msg = thrown.Message + (thrown.InnerException?.Message ?? string.Empty);
            Assert.DoesNotContain("database is locked", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("database table is locked", msg, StringComparison.OrdinalIgnoreCase);
            throw thrown;
        }

        var search = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "stress capture" });
        Assert.Equal(parallelism, search.Items.Count);
    }
}
