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
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
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
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
        coordinator.Start();

        monitor.EmitUpdate(new ClipEntry { Id = 1 });

        Assert.Equal(1, worker.PokeCount);
    }

    [Fact]
    public void CompletedBatch_ReachesTheSemanticCache()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
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
            var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService { ThrowOnAppend = true };
            using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
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

    /// <summary>
    /// The database sheds a deleted clip's vector by cascade, but this cache is
    /// a snapshot that is otherwise only rebuilt when the sensitivity rules
    /// change. Without this relay a deleted clip stays semantically searchable
    /// for the rest of the session.
    /// </summary>
    [Fact]
    public async Task RemovedClips_AreDroppedFromTheSemanticCache()
    {
        var monitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
        coordinator.Start();

        store.EmitRemoved(3, 9);

        var relayed = await WaitForAsync(() => search.Removed.Count == 2);
        Assert.True(relayed, $"expected ids 3 and 9 to reach the cache; saw [{string.Join(", ", search.Removed)}]");
        Assert.Equal([3L, 9L], search.Removed);
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
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        using var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
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
        var store = new RemovalSignalClipStore();
        var search = new RecordingSemanticSearchService();
        var coordinator = new SemanticIndexCoordinator(monitor, worker, search, store);
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

        public List<long> Removed { get; } = [];

        public bool ThrowOnAppend { get; set; }

        public bool IsAvailable => true;

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

        public Task RemoveEmbeddingsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default)
        {
            Removed.AddRange(clipIds);
            return Task.CompletedTask;
        }
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
    /// <summary>
    /// Carries only the removal signal; every other member is unreachable in
    /// these tests and says so rather than pretending to be a store.
    /// </summary>
    private sealed class RemovalSignalClipStore : IClipStoreService
    {
        private readonly Subject<IReadOnlyList<long>> _removed = new();

        public IObservable<IReadOnlyList<long>> ClipsRemoved => _removed;

        public void EmitRemoved(params long[] ids) => _removed.OnNext(ids);

        private static T No<T>() => throw new NotSupportedException("not reachable from the coordinator");

        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => No<Task<int>>();
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => No<Task<BulkCaptureResult>>();
        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default) => No<Task<ClipSearchResult>>();
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => No<Task>();
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => No<Task>();
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => No<Task>();
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => No<Task>();
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => No<Task>();
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => No<Task>();
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => No<Task<bool>>();
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => No<Task<bool>>();
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => No<Task<bool>>();
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<long>>>();
        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => No<Task<int>>();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => No<Task<bool>>();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<long>>>();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => No<Task<OcrCoverage>>();
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => No<Task<ClipMaintenanceResult>>();
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => No<Task>();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => No<Task<ClipEntry?>>();
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => No<Task<byte[]?>>();
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<ClipEntry>>>();
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<ClipEmbeddingCandidate>>>();
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => No<Task<int>>();
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => No<Task>();
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => No<Task>();
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => No<Task<bool>>();
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<long>>>();
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => No<Task<EmbeddingCoverage>>();
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => No<Task<IReadOnlyList<ClipEmbedding>>>();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => No<Task>();
    }
}
