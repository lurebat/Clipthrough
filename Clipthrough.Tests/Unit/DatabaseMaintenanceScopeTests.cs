using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class DatabaseMaintenanceScopeTests
{
    // Bug #11: EnterAsync stops the workers sequentially. If a later StopAsync
    // throws, the scope is never returned to the caller, so its DisposeAsync — the
    // only restart path — never runs and the already-stopped workers stay down.
    // EnterAsync must restart what it stopped before surfacing the failure.
    [Fact]
    public async Task EnterAsync_WhenWorkerStopThrows_RestartsAlreadyStoppedWorkers()
    {
        var monitor = new RecordingMonitor();
        var ocr = new RecordingOcrQueue();
        var embedding = new ThrowOnStopEmbeddingWorker();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseMaintenanceScope.EnterAsync(monitor, ocr, embedding));

        // monitor + ocr were stopped before the embedding stop threw; all three
        // must be restarted so the session's workers are not left permanently down.
        Assert.Equal(1, monitor.StopAsyncCount);
        Assert.Equal(0, monitor.StopCount);
        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(1, ocr.StopCount);
        Assert.Equal(1, ocr.StartCount);
        Assert.Equal(1, embedding.StartCount);
    }

    // K4: DisposeAsync used to Start() all three unconditionally, so a worker the
    // user had deliberately turned off (capture paused, semantic search disabled)
    // came back to life the moment any whole-database operation ran.
    [Fact]
    public async Task DisposeAsync_RestartsOnlyTheWorkersThatWereRunningOnEntry()
    {
        var monitor = new RecordingMonitor { IsRunning = true };
        var ocr = new RecordingOcrQueue { IsRunning = false };
        var embedding = new RecordingEmbeddingWorker { IsRunning = false };

        var scope = await DatabaseMaintenanceScope.EnterAsync(monitor, ocr, embedding);
        await scope.DisposeAsync();

        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(0, ocr.StartCount);
        Assert.Equal(0, embedding.StartCount);
    }

    /// <summary>
    /// The monitor has to be stopped through the awaited path. Stop() off the UI
    /// thread only posts, so EnterAsync could clear the connection pools - and
    /// return to a caller about to move or rekey the file - while a capture was
    /// still writing. (round 2, arch-sol A6)
    /// </summary>
    [Fact]
    public async Task EnterAsync_WaitsForTheMonitorToDrainBeforeReturning()
    {
        var draining = new TaskCompletionSource();
        var monitor = new GatedMonitor(draining.Task);

        var entering = DatabaseMaintenanceScope.EnterAsync(monitor, null, null);

        Assert.False(
            entering.IsCompleted,
            "EnterAsync returned while the monitor was still draining, so maintenance can overlap a clipboard write");

        draining.SetResult();
        var scope = await entering;
        await scope.DisposeAsync();

        Assert.Equal(0, monitor.StopCount);
        Assert.Equal(1, monitor.StopAsyncCount);
    }

    /// <summary>A monitor whose drain the test decides when to finish.</summary>
    private sealed class GatedMonitor(Task drain) : IClipboardMonitorService
    {
        public int StopCount;
        public int StopAsyncCount;

        public IObservable<ClipEntry> CapturedClips { get; } = new Subject<ClipEntry>();
        public IObservable<ClipEntry> UpdatedClips { get; } = new Subject<ClipEntry>();
        public IObservable<bool> CaptureBusy { get; } = new Subject<bool>();
        public bool IsRunning { get; private set; } = true;

        public void Start() => IsRunning = true;
        public void Stop() { StopCount++; IsRunning = false; }

        public async Task StopAsync()
        {
            StopAsyncCount++;
            await drain;
            IsRunning = false;
        }

        public void SuppressNext() { }

        public void CancelSuppressNext() { }
    }

    private sealed class RecordingMonitor : IClipboardMonitorService
    {
        public int StartCount;
        public int StopCount;
        public bool IsRunning { get; set; } = true;

        public IObservable<ClipEntry> CapturedClips => Observable.Empty<ClipEntry>();
        public IObservable<ClipEntry> UpdatedClips => Observable.Empty<ClipEntry>();
        public IObservable<bool> CaptureBusy => Observable.Empty<bool>();

        public void Start() { StartCount++; IsRunning = true; }
        public void Stop() { StopCount++; IsRunning = false; }

        /// <summary>
        /// Counted separately from <see cref="Stop"/> on purpose. Maintenance has
        /// to use the awaited path, and a shared counter would let a revert to
        /// the posted one pass.
        /// </summary>
        public int StopAsyncCount;

        public Task StopAsync() { StopAsyncCount++; IsRunning = false; return Task.CompletedTask; }

        public void SuppressNext() { }

        public void CancelSuppressNext() { }
    }

    private sealed class RecordingOcrQueue : IBackgroundOcrQueue
    {
        public int StartCount;
        public int StopCount;
        public bool IsRunning { get; set; } = true;

        public IObservable<long> OcrCompleted => Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged => Observable.Empty<System.Reactive.Unit>();

        public void Start() { StartCount++; IsRunning = true; }
        public Task StopAsync() { StopCount++; IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingEmbeddingWorker : IEmbeddingWorker
    {
        public int StartCount;
        public int StopCount;
        public bool IsRunning { get; set; } = true;

        public IObservable<int> BatchCompleted => Observable.Empty<int>();
        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted =>
            Observable.Empty<IReadOnlyList<ClipEmbeddingRecord>>();

        public void Start() { StartCount++; IsRunning = true; }
        public Task StopAsync() { StopCount++; IsRunning = false; return Task.CompletedTask; }
        public void Poke() { }
        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<EmbeddingCoverage>(default!);
        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowOnStopEmbeddingWorker : IEmbeddingWorker
    {
        public int StartCount;
        public bool IsRunning => true;

        public IObservable<int> BatchCompleted => Observable.Empty<int>();
        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted =>
            Observable.Empty<IReadOnlyList<ClipEmbeddingRecord>>();

        public void Start() => StartCount++;
        public Task StopAsync() => throw new InvalidOperationException("simulated stop failure");
        public void Poke() { }
        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<EmbeddingCoverage>(default!);
        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
