using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-23. The embedding worker had four uncoordinated owners. A settings save
/// during a rekey, move, restore or ClipAngel import restarted it while the
/// database was meant to be quiet, and a maintenance scope restored the state
/// it saw on entry even when the user had changed the setting meanwhile. The
/// worker now owns its lifetime: wishes are recorded, pauses hold it stopped.
/// </summary>
public sealed class EmbeddingWorkerLifetimeTests
{
    private sealed class IdleEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 4;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new float[4]);
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>([.. new float[texts.Count][]]);
    }

    private static (TemporaryDatabaseScope Scope, EmbeddingWorker Worker) NewWorker()
    {
        var scope = new TemporaryDatabaseScope();
        scope.DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();
        return (scope, new EmbeddingWorker(scope.ClipStoreService, new IdleEmbeddingService(), new BackgroundJobIndicator()));
    }

    [Fact]
    public async Task TurningSemanticSearchOn_DuringAMaintenanceScope_WaitsForTheScopeToEnd()
    {
        var (scope, worker) = NewWorker();
        using (scope)
        using (worker)
        {
            await using (await DatabaseMaintenanceScope.EnterAsync(null, null, worker))
            {
                worker.Start(); // what ApplySemanticSearchWorkerState does on SettingsChanged
                Assert.False(worker.IsRunning);
            }

            Assert.True(worker.IsRunning);
            await worker.StopAsync();
        }
    }

    [Fact]
    public async Task TurningSemanticSearchOff_DuringAMaintenanceScope_IsNotUndoneByTheScope()
    {
        var (scope, worker) = NewWorker();
        using (scope)
        using (worker)
        {
            worker.Start();
            Assert.True(worker.IsRunning);

            await using (await DatabaseMaintenanceScope.EnterAsync(null, null, worker))
            {
                Assert.False(worker.IsRunning);
                await worker.StopAsync();
            }

            Assert.False(worker.IsRunning);
        }
    }

    /// <summary>
    /// Stands in for a batch the loop is in the middle of when it is stopped:
    /// it ignores cancellation, as a running SQLite statement or ONNX inference
    /// effectively does, until the test lets it finish.
    /// </summary>
    private sealed class BlockingEmbeddingService : IEmbeddingService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Dimensions => 4;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new float[4]);

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return [.. new float[texts.Count][]];
        }
    }

    /// <summary>
    /// Rubber-duck F-09-1. Turning semantic search off stops the worker, and a
    /// rekey, move, restore or import that starts before the loop has drained
    /// pauses it. The pause used to see the worker already marked stopped and
    /// return at once, so the maintenance went ahead while the loop was still
    /// inside a batch with the database open.
    /// </summary>
    [Fact]
    public async Task Pause_WhileAStopIsStillDraining_WaitsForTheLoopToFinish()
    {
        var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "in flight when stopped",
            ContentBytes = Encoding.UTF8.GetBytes("in flight when stopped"),
            SourceApp = "Test",
        });

        var embedding = new BlockingEmbeddingService();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, new BackgroundJobIndicator());
        using (scope)
        using (worker)
        {
            worker.Start();
            worker.Poke();
            await embedding.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var stop = worker.StopAsync();
            var pause = worker.PauseAsync();

            Assert.False(stop.IsCompleted, "StopAsync returned while the loop was still inside a batch.");
            Assert.False(pause.IsCompleted, "PauseAsync returned while a concurrent StopAsync was still draining the loop.");

            embedding.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            var handle = await pause.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.DisposeAsync();
            Assert.False(worker.IsRunning);
        }
    }

    [Fact]
    public async Task OverlappingPauses_HoldTheWorkerUntilTheLastIsReleased()
    {
        var (scope, worker) = NewWorker();
        using (scope)
        using (worker)
        {
            worker.Start();
            var first = await worker.PauseAsync();
            var second = await worker.PauseAsync();

            await first.DisposeAsync();
            Assert.False(worker.IsRunning);
            await first.DisposeAsync(); // releasing twice must not count twice
            Assert.False(worker.IsRunning);

            await second.DisposeAsync();
            Assert.True(worker.IsRunning);
            await worker.StopAsync();
        }
    }
}