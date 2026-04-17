using System;
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

public sealed class EmbeddingWorkerTests
{
    [Fact]
    public async Task Worker_EmbedsPendingClipsAndAdvancesCoverage()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        for (var i = 0; i < 3; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"semantic sample {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"semantic sample {i}"),
                SourceApp = "Test",
            });
        }

        var before = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, before.EligibleTotal);
        Assert.Equal(0, before.Embedded);

        var embedding = new FakeEmbeddingService(dims: 8);
        var indicator = new BackgroundJobIndicator();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, indicator);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = worker.BatchCompleted.Subscribe(count => tcs.TrySetResult(count));

        worker.Start();
        worker.Poke();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync();
        Assert.Same(tcs.Task, completed);

        var after = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, after.Embedded);
        Assert.Equal(0, after.Pending);
        Assert.True(embedding.CallCount >= 1);
    }

    [Fact]
    public async Task Worker_RerunAll_ReprocessesExistingEmbeddings()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "rerun me",
            ContentBytes = Encoding.UTF8.GetBytes("rerun me"),
            SourceApp = "Test",
        });

        var embedding = new FakeEmbeddingService(dims: 4);
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, new BackgroundJobIndicator());

        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (worker.BatchCompleted.Subscribe(n => first.TrySetResult(n)))
        {
            worker.Start();
            worker.Poke();
            await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        var midCoverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, midCoverage.Embedded);
        var firstCalls = embedding.CallCount;

        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (worker.BatchCompleted.Subscribe(n => second.TrySetResult(n)))
        {
            await worker.RerunAllAsync();
            await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        await worker.StopAsync();

        var final = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, final.Embedded);
        Assert.True(embedding.CallCount > firstCalls, "Expected re-embed to invoke the embedding service again.");
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        private readonly int _dims;
        public int CallCount;

        public FakeEmbeddingService(int dims) => _dims = dims;

        public int Dimensions => _dims;
        public bool IsReady => true;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(Vector(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            var result = texts.Select(Vector).ToArray();
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }

        private float[] Vector(string s)
        {
            var vec = new float[_dims];
            var hash = s?.GetHashCode() ?? 0;
            for (var i = 0; i < _dims; i++)
            {
                vec[i] = ((hash >> (i % 32)) & 1) == 1 ? 1f / MathF.Sqrt(_dims) : -1f / MathF.Sqrt(_dims);
            }
            return vec;
        }
    }
}
