using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
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
    }
}
