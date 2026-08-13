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

    [Fact]
    public async Task Worker_PersistFailure_BacksOffInsteadOfHotLooping()
    {
        var clipStore = new PersistFailureClipStore();
        var worker = new EmbeddingWorker(clipStore, new FakeEmbeddingService(dims: 4), new BackgroundJobIndicator());

        worker.Start();
        worker.Poke();

        await clipStore.SaveAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await worker.StopAsync();

        Assert.InRange(clipStore.ClaimCallCount, 1, 2);
        Assert.Equal(1, clipStore.SaveCallCount);
        Assert.Equal(1, clipStore.SetFailureCallCount);
    }

    [Fact]
    public async Task Worker_InferenceFailure_IdlesInsteadOfHotLooping()
    {
        // EmbedBatchAsync throws persistently — worker must idle (return 0), not spin in a tight loop.
        var store = new InferenceFailureClipStore();
        var failingEmb = new ThrowingEmbeddingService();
        var worker = new EmbeddingWorker(store, failingEmb, new BackgroundJobIndicator());

        worker.Start();
        worker.Poke();

        // Wait for at least one inference attempt to be flagged.
        await store.InferenceFailureFlagged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Give it a short window to accumulate any tight-loop re-claims.
        await Task.Delay(250);
        await worker.StopAsync();

        // After the initial batch, the worker should idle: ClaimCallCount must be very small.
        // If it were hot-looping, it would be much larger in 250ms.
        Assert.InRange(store.ClaimCallCount, 1, 3);
        Assert.True(store.SetFailureCallCount >= 1, "Expected at least one SetEmbeddingFailureAsync call.");
    }

    [Fact]
    public async Task Worker_MissingOnnxModel_IdlesWithoutMarkingFailure()
    {
        // FileNotFoundException from EmbedBatchAsync must idle the worker and NOT mark clips as failed
        // (since the user might place the model file later).
        var store = new InferenceFailureClipStore();
        var missingModel = new FileNotFoundEmbeddingService();
        var worker = new EmbeddingWorker(store, missingModel, new BackgroundJobIndicator());

        worker.Start();
        worker.Poke();

        await store.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await worker.StopAsync();

        // Must have claimed once, but must NOT have called SetEmbeddingFailureAsync
        // (model-missing should not poison the clip's status).
        Assert.InRange(store.ClaimCallCount, 1, 2);
        Assert.Equal(0, store.SetFailureCallCount);
    }

    [Fact]
    public async Task Worker_MissingOnnxModel_LeavesTheBatchClaimableSoItEmbedsOnceTheModelArrives()
    {
        // Claiming marks the batch 'processing', which ClaimPendingEmbeddingsAsync never
        // re-selects. If the model-missing path doesn't release the claim, the clips it
        // promised to "retry after the model is placed" can never be embedded again.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        for (var i = 0; i < 3; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"model arrives later {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"model arrives later {i}"),
                SourceApp = "Test",
            });
        }

        var embedding = new ModelArrivesLateEmbeddingService(dims: 8);
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, new BackgroundJobIndicator());

        var embedded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = worker.BatchCompleted.Subscribe(n => embedded.TrySetResult(n));

        worker.Start();
        worker.Poke();

        // First pass: the model file is "absent".
        await embedding.ModelMissingReported.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The model is now in place; wake the idling worker for a second pass.
        embedding.PlaceModel();
        worker.Poke();

        var done = await Task.WhenAny(embedded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync();
        Assert.Same(embedded.Task, done);

        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, coverage.Embedded);
        Assert.Equal(0, coverage.Pending);
    }

    [Fact]
    public async Task Worker_Start_SweepsClaimsStrandedByAPreviousRun()
    {
        // A batch claimed by a run that was stopped (or crashed) mid-flight stays
        // 'processing' forever, because the claim query only selects null/pending/
        // rerun/failed. Starting the worker must sweep those back to pending.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        for (var i = 0; i < 3; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"stranded {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"stranded {i}"),
                SourceApp = "Test",
            });
        }

        // Simulate the interrupted run: claim, then never save or fail the batch.
        var stranded = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal(3, stranded.Count);
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        var worker = new EmbeddingWorker(scope.ClipStoreService, new FakeEmbeddingService(dims: 8), new BackgroundJobIndicator());
        var embedded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = worker.BatchCompleted.Subscribe(n => embedded.TrySetResult(n));

        worker.Start();
        worker.Poke();

        var done = await Task.WhenAny(embedded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync();
        Assert.Same(embedded.Task, done);

        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, coverage.Embedded);
        Assert.Equal(0, coverage.Pending);
    }

    [Fact]
    public async Task Worker_StalledClaimSweepFailure_IsRetriedInsteadOfStrandingTheRun()
    {
        // A transient SQLite busy on the sweep must not reinstate the very bug the
        // sweep exists to prevent: it has to be retried on a later iteration.
        var store = new InferenceFailureClipStore { FailResetCalls = 1 };
        var worker = new EmbeddingWorker(store, new FakeEmbeddingService(dims: 4), new BackgroundJobIndicator());

        worker.Start();
        worker.Poke();

        await store.ResetSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync();

        Assert.True(store.ResetCallCount >= 2, $"Expected the sweep to be retried after failing; saw {store.ResetCallCount} call(s).");
    }

    [Fact]
    public async Task Worker_MissingOnnxModel_ReleasesTheClaimItRefusedToFail()
    {
        var store = new InferenceFailureClipStore();
        var worker = new EmbeddingWorker(store, new FileNotFoundEmbeddingService(), new BackgroundJobIndicator());

        worker.Start();
        worker.Poke();

        await store.ClaimReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync();

        Assert.Equal(0, store.SetFailureCallCount);
        Assert.Equal([42L], store.ReleasedIds);
    }

    private sealed class InferenceFailureClipStore : IClipStoreService
    {
        private int _claimCallCount;
        private int _setFailureCallCount;
        private int _resetCallCount;
        private bool _claimed;

        public int ClaimCallCount => Volatile.Read(ref _claimCallCount);
        public int SetFailureCallCount => Volatile.Read(ref _setFailureCallCount);
        public int ResetCallCount => Volatile.Read(ref _resetCallCount);

        /// <summary>Number of leading <see cref="ResetStalledEmbeddingClaimsAsync"/> calls that throw.</summary>
        public int FailResetCalls { get; init; }

        public List<long> ReleasedIds { get; } = [];
        public TaskCompletionSource InferenceFailureFlagged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClaimReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResetSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claimCallCount);
            ClaimAttempted.TrySetResult();
            if (_claimed) return Task.FromResult<IReadOnlyList<ClipEmbeddingCandidate>>([]);
            _claimed = true;
            return Task.FromResult<IReadOnlyList<ClipEmbeddingCandidate>>([new ClipEmbeddingCandidate(42, "sample text")]);
        }

        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _setFailureCallCount);
            InferenceFailureFlagged.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _resetCallCount);
            if (call <= FailResetCalls) throw new InvalidOperationException("database is locked");
            ResetSucceeded.TrySetResult();
            return Task.FromResult(0);
        }

        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default)
        {
            lock (ReleasedIds) ReleasedIds.AddRange(clipIds);
            ClaimReleased.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 4;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => throw new InvalidOperationException("inference broken");
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("inference broken");
    }

    private sealed class FileNotFoundEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 4;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => throw new System.IO.FileNotFoundException("model.onnx not found");
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => throw new System.IO.FileNotFoundException("model.onnx not found");
    }

    /// <summary>
    /// Fails like an absent ONNX model until <see cref="PlaceModel"/> is called, then
    /// embeds normally — the "user dropped the model in later" scenario the worker's
    /// model-missing path explicitly promises to support.
    /// </summary>
    private sealed class ModelArrivesLateEmbeddingService : IEmbeddingService
    {
        private readonly int _dims;
        private volatile bool _present;

        public ModelArrivesLateEmbeddingService(int dims) => _dims = dims;

        public TaskCompletionSource ModelMissingReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PlaceModel() => _present = true;

        public int Dimensions => _dims;
        public bool IsReady => true;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => EmbedBatchAsync([text], cancellationToken).ContinueWith(t => t.Result[0], cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            if (!_present)
            {
                ModelMissingReported.TrySetResult();
                throw new System.IO.FileNotFoundException("model.onnx not found");
            }

            var result = new float[texts.Count][];
            var unit = (float)(1.0 / Math.Sqrt(_dims));
            for (var i = 0; i < texts.Count; i++)
            {
                var vec = new float[_dims];
                for (var k = 0; k < _dims; k++) vec[k] = unit;
                result[i] = vec;
            }

            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }
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

    private sealed class PersistFailureClipStore : IClipStoreService
    {
        private int _claimCallCount;
        private int _saveCallCount;
        private int _setFailureCallCount;
        private bool _claimed;

        public int ClaimCallCount => Volatile.Read(ref _claimCallCount);
        public int SaveCallCount => Volatile.Read(ref _saveCallCount);
        public int SetFailureCallCount => Volatile.Read(ref _setFailureCallCount);
        public TaskCompletionSource SaveAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claimCallCount);
            if (_claimed)
            {
                return Task.FromResult<IReadOnlyList<ClipEmbeddingCandidate>>([]);
            }

            _claimed = true;
            return Task.FromResult<IReadOnlyList<ClipEmbeddingCandidate>>([new ClipEmbeddingCandidate(1, "spin-test")]);
        }

        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCallCount);
            SaveAttempted.TrySetResult();
            throw new InvalidOperationException("persist failed");
        }

        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _setFailureCallCount);
            return Task.FromResult(true);
        }

        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
