using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The semantic pipeline has two links, and both used to live as inline
/// subscriptions inside the application object: capture wakes the embedding
/// worker, and a finished batch is appended to the in-memory cache. Nothing
/// outside <c>App</c> could construct either, so the entire suite ran against a
/// disconnected pipeline and would have kept passing if either link were
/// deleted.
///
/// These tests exist to make that impossible. They assert the relay itself, not
/// the embedding or the search, because the relay is what nothing else covers.
/// </summary>
public class SemanticIndexCoordinatorTests
{
    [Fact]
    public void CapturedClip_WakesTheEmbeddingWorker()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
        coordinator.Start();

        monitor.Emit(new ClipEntry { Id = 1 });

        Assert.Equal(1, worker.PokeCount);
    }

    /// <summary>
    /// An edited clip is re-embedded too - its text changed, so its vector is
    /// stale. Only merging both streams covers that.
    /// </summary>
    [Fact]
    public void UpdatedClip_WakesTheEmbeddingWorker()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
        coordinator.Start();

        monitor.EmitUpdate(new ClipEntry { Id = 1 });

        Assert.Equal(1, worker.PokeCount);
    }

    [Fact]
    public void CompletedBatch_ReachesTheSemanticCache()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
        coordinator.Start();

        var records = new List<ClipEmbeddingRecord> { new(7, [0.5f, 0.5f]) };
        worker.EmitBatch(records);

        var appended = Assert.Single(search.Appended);
        Assert.Equal(7, Assert.Single(appended).ClipId);
    }

    /// <summary>
    /// The append runs detached so the worker is not held up by the cache, which
    /// leaves its faults with nowhere to surface: an <c>async Task</c> that
    /// throws faults its task silently, and since nobody awaits it the failure
    /// is swallowed whole. Search then degrades with no trace of why. The catch
    /// exists to report it, and reporting is the contract worth asserting -
    /// the subscription survives either way.
    /// </summary>
    [Fact]
    public async Task FailingAppend_IsReportedRatherThanSwallowed()
    {
        var sink = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            var monitor = new TestClipboardMonitorService();
            var worker = new RecordingEmbeddingWorker();
            var search = new RecordingSemanticSearchService { ThrowOnAppend = true };
            using var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
            coordinator.Start();

            worker.EmitBatch([new ClipEmbeddingRecord(1, [1f])]);

            var reported = await WaitForAsync(() => sink.Any(m => m.Contains("semantic cache", StringComparison.Ordinal)));
            Assert.True(reported, $"the failed append was never reported; saw: {string.Join(" | ", sink)}");

            // And the relay keeps working: one bad batch must not end the pipeline.
            search.ThrowOnAppend = false;
            worker.EmitBatch([new ClipEmbeddingRecord(2, [1f])]);
            Assert.Equal(2, Assert.Single(search.Appended)[0].ClipId);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDoubleSubscribe()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
        coordinator.Start();
        coordinator.Start();

        monitor.Emit(new ClipEntry { Id = 1 });

        Assert.Equal(1, worker.PokeCount);
    }

    [Fact]
    public void Dispose_StopsRelaying()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var search = new RecordingSemanticSearchService();
        var coordinator = new SemanticIndexCoordinator(monitor, worker, search);
        coordinator.Start();
        coordinator.Dispose();

        monitor.Emit(new ClipEntry { Id = 1 });
        worker.EmitBatch([new ClipEmbeddingRecord(1, [1f])]);

        Assert.Equal(0, worker.PokeCount);
        Assert.Empty(search.Appended);
    }

    private sealed class RecordingEmbeddingWorker : IEmbeddingWorker
    {
        private readonly Subject<IReadOnlyList<ClipEmbeddingRecord>> _batchRecords = new();

        public int PokeCount { get; private set; }

        public IObservable<int> BatchCompleted => Observable.Empty<int>();

        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted => _batchRecords.AsObservable();

        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Poke() => PokeCount++;

        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingCoverage(0, 0, 0, 0, 0));

        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void EmitBatch(IReadOnlyList<ClipEmbeddingRecord> records) => _batchRecords.OnNext(records);
    }

    private sealed class RecordingSemanticSearchService : ISemanticSearchService
    {
        public List<IReadOnlyList<ClipEmbeddingRecord>> Appended { get; } = [];

        public bool ThrowOnAppend { get; set; }

        public bool IsReady => true;

        public int CachedCount => 0;

        public Task RefreshCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AppendEmbeddingsAsync(IReadOnlyList<ClipEmbeddingRecord> records, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAppend)
            {
                throw new InvalidOperationException("cache is unavailable");
            }

            Appended.Add(records);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(long ClipId, float Score)>> QueryAsync(string text, int topK, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(long, float)>>([]);
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
