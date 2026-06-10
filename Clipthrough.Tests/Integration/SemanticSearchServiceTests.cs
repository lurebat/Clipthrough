using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class SemanticSearchServiceTests
{
    [Fact]
    public async Task QueryAsync_RanksCachedClipsByCosineSimilarity()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        // Capture three clips with known distinct bodies — the fake embedder hashes each text
        // deterministically so each gets its own vector in the cache.
        var phrases = new[] { "red apple fruit", "blue ocean water", "green forest tree" };
        foreach (var phrase in phrases)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = phrase,
                ContentBytes = Encoding.UTF8.GetBytes(phrase),
                SourceApp = "Test",
            });
        }

        // Seed embeddings by running the worker once.
        var embedding = new DeterministicEmbeddingService(dims: 16);
        var indicator = new Clipthrough.Services.BackgroundJobIndicator();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, indicator);
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();
        Assert.True(semantic.IsReady);
        Assert.Equal(3, semantic.CachedCount);

        // Querying with the same text as one of the stored clips should rank that clip first.
        var ranked = await semantic.QueryAsync("red apple fruit", topK: 3);
        Assert.Equal(3, ranked.Count);
        var appleId = await FindClipIdByText(scope, "red apple fruit");
        Assert.Equal(appleId, ranked[0].ClipId);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyWhenCacheEmpty()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var embedding = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();

        Assert.True(semantic.IsReady);
        Assert.Equal(0, semantic.CachedCount);

        var hits = await semantic.QueryAsync("anything", topK: 5);
        Assert.Empty(hits);
    }


    // ======== U14: QueryAsync is race-free under concurrent RefreshCacheAsync ========

    [Fact]
    public async Task QueryAsync_ConcurrentRefresh_NoIndexOutOfRange()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        // Seed a mix of clip counts so RefreshCacheAsync keeps changing the snapshot size.
        var phrases = new[] { "cat", "dog", "bird", "fish", "ant" };
        foreach (var p in phrases)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
        }
        var emb = new DeterministicEmbeddingService(dims: 8);
        var worker = new EmbeddingWorker(scope.ClipStoreService, emb, new BackgroundJobIndicator());
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await semantic.RefreshCacheAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new ConcurrentBag<Exception>();

        // Concurrently run RefreshCacheAsync and QueryAsync — must not throw IndexOutOfRangeException.
        var tasks = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { await semantic.RefreshCacheAsync(cts.Token); } catch (OperationCanceledException) { }
                    await Task.Delay(5, default);
                }
            }));
        }
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { await semantic.QueryAsync("cat", topK: 3, cts.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { exceptions.Add(ex); }
                    await Task.Delay(3, default);
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }

    // ======== U17: Incremental AppendEmbeddingsAsync — O(M) not O(M*N) ========

    [Fact]
    public async Task AppendEmbeddingsAsync_AppendsNewRecordsWithoutFullReload()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var phrases = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta" };
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);

        // Seed the first two clips into the cache via full refresh.
        var firstTwoRecords = new List<ClipEmbeddingRecord>
        {
            new(ids[0], emb.VectorFor(phrases[0])),
            new(ids[1], emb.VectorFor(phrases[1])),
        };
        // Simulate: save to DB and call RefreshCacheAsync to populate the cache.
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(firstTwoRecords, "test");
        await semantic.RefreshCacheAsync();
        Assert.Equal(2, semantic.CachedCount);

        // Now append the third clip's record without a full reload.
        var third = new ClipEmbeddingRecord(ids[2], emb.VectorFor(phrases[2]));
        await semantic.AppendEmbeddingsAsync(new[] { third });

        Assert.Equal(3, semantic.CachedCount);

        // Query confirms the newly appended record is findable.
        var hits = await semantic.QueryAsync(phrases[2], topK: 3);
        Assert.NotEmpty(hits);
        Assert.Equal(ids[2], hits[0].ClipId);
    }

    [Fact]
    public async Task AppendEmbeddingsAsync_DuplicateId_NotAddedTwice()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "dedupe test",
            ContentBytes = Encoding.UTF8.GetBytes("dedupe test"),
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var emb = new DeterministicEmbeddingService(dims: 4);
        var record = new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("dedupe test"));
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(new[] { record }, "test");

        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await semantic.RefreshCacheAsync();
        Assert.Equal(1, semantic.CachedCount);

        // Append the same record again — should not increase count.
        await semantic.AppendEmbeddingsAsync(new[] { record });
        Assert.Equal(1, semantic.CachedCount);
    }

    [Fact]
    public async Task AppendEmbeddingsAsync_EmptyCacheFallsBackToFullRefresh()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "fresh start",
            ContentBytes = Encoding.UTF8.GetBytes("fresh start"),
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var emb = new DeterministicEmbeddingService(dims: 4);
        var record = new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("fresh start"));
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(new[] { record }, "test");

        // SemanticSearchService with empty cache: AppendEmbeddingsAsync should fall back to full refresh.
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        Assert.Equal(0, semantic.CachedCount);

        await semantic.AppendEmbeddingsAsync(new[] { record });
        Assert.Equal(1, semantic.CachedCount);
    }

    private static async Task<long> FindClipIdByText(TemporaryDatabaseScope scope, string text)
    {
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 50 });
        return result.Items.First(c => c.Content == text).Id;
    }

    private static async Task WaitForBatchAsync(EmbeddingWorker worker)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = worker.BatchCompleted.Subscribe(n => tcs.TrySetResult(n));
        worker.Poke();
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private sealed class DeterministicEmbeddingService : IEmbeddingService
    {
        private readonly int _dims;
        public DeterministicEmbeddingService(int dims) { _dims = dims; }
        public int Dimensions => _dims;
        public bool IsReady => true;

        public Task<float[]> EmbedAsync(string text, System.Threading.CancellationToken ct = default)
            => Task.FromResult(Vector(text));

        public Task<System.Collections.Generic.IReadOnlyList<float[]>> EmbedBatchAsync(
            System.Collections.Generic.IReadOnlyList<string> texts,
            System.Threading.CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<float[]>>(texts.Select(Vector).ToArray());

        private float[] Vector(string s)
        {
            var vec = new float[_dims];
            var hash = s?.GetHashCode() ?? 0;
            double sumSq = 0;
            for (var i = 0; i < _dims; i++)
            {
                vec[i] = ((hash >> (i % 32)) & 1) == 1 ? 1f : -1f;
                // Perturb to break ties against the query text.
                vec[i] += 0.01f * ((hash >> i) & 0xFF) / 255f;
                sumSq += vec[i] * vec[i];
            }
            var norm = (float)Math.Sqrt(sumSq);
            if (norm > 0) for (var i = 0; i < _dims; i++) vec[i] /= norm;
            return vec;
        }

        /// <summary>Expose the same deterministic vector for test assertions without async overhead.</summary>
        public float[] VectorFor(string text) => Vector(text);
    }
}
