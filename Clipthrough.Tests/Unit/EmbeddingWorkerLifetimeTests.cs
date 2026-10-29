using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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