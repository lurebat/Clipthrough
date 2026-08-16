using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services.Search;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The semantic-coverage chip is the only progress the user gets while the
/// embedding backlog drains. It froze at its startup value for the whole run:
/// the DB reached 100% while the chip still read "0/1577 (0%) · 1577 queued",
/// which reads as "enabled it and nothing is happening".
///
/// The cause was where the subscription is set up, not the worker.
/// <c>StartDatabaseInBackgroundAsync</c> awaits with <c>ConfigureAwait(false)</c>
/// and then calls <c>StartBackgroundServices</c>, so that method runs on a
/// thread-pool thread. Its last statement subscribed a sampler bound to the
/// Avalonia dispatcher scheduler, and setting that up off the UI thread throws,
/// which both loses the subscription and aborts the rest of the startup tail.
/// The OCR chip above it survived only because it samples on the task pool.
/// </summary>
public class SemanticCoverageProgressHeadlessTests
{
    /// <summary>
    /// Drives <see cref="BatchCompleted"/> on demand and reports a coverage
    /// figure the test can move, so "did the chip follow the work?" is
    /// answerable without running real inference.
    /// </summary>
    private sealed class DrivableEmbeddingWorker : IEmbeddingWorker
    {
        private readonly Subject<int> _batchCompleted = new();
        private long _embedded;

        public IObservable<int> BatchCompleted => _batchCompleted;

        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted { get; } =
            Observable.Empty<IReadOnlyList<ClipEmbeddingRecord>>();

        public bool IsRunning { get; private set; }

        public long Eligible { get; set; } = 100;

        public void Start() => IsRunning = true;

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Poke() { }

        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
        {
            var embedded = Interlocked.Read(ref _embedded);
            return Task.FromResult(new EmbeddingCoverage(Eligible, embedded, Eligible - embedded, 0, 0));
        }

        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>Simulates one batch landing in the store.</summary>
        public void CompleteBatch(int count)
        {
            Interlocked.Add(ref _embedded, count);
            _batchCompleted.OnNext(count);
        }
    }

    private static async Task PumpAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The regression: startup calls StartBackgroundServices from a thread-pool
    // thread, so the chip must still track the backlog. Asserting on the text
    // rather than on a subscription count keeps this honest — it is exactly what
    // the user reads off the window.
    [AvaloniaFact]
    public async Task SemanticCoverage_WhenStartedOffTheUiThread_FollowsCompletedBatches()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = true });

        var worker = new DrivableEmbeddingWorker { Eligible = 100 };
        using var viewModel = CreateViewModelForCoverage(scope, worker);
        await viewModel.InitializeAsync();

        // Exactly how StartDatabaseInBackgroundAsync reaches it: off the UI thread.
        await Task.Run(() => viewModel.StartBackgroundServices());

        await PumpAsync(TimeSpan.FromMilliseconds(300));
        var atStartup = viewModel.SemanticCoverageText;

        worker.CompleteBatch(32);
        await PumpAsync(TimeSpan.FromSeconds(3));

        Assert.NotEqual(atStartup, viewModel.SemanticCoverageText);
        Assert.Contains("32/100", viewModel.SemanticCoverageText, StringComparison.Ordinal);
    }

    // Guards the whole startup tail, not just the chip: the subscription was the
    // last statement in StartBackgroundServices, so a throw there also silently
    // skipped the maintenance kick-off that follows it in the caller.
    [AvaloniaFact]
    public async Task StartBackgroundServices_OffTheUiThread_DoesNotThrow()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = true });

        var worker = new DrivableEmbeddingWorker();
        using var viewModel = CreateViewModelForCoverage(scope, worker);
        await viewModel.InitializeAsync();

        var failure = await Record.ExceptionAsync(() => Task.Run(() => viewModel.StartBackgroundServices()));

        Assert.Null(failure);
    }

    private sealed class SilentOcrQueue : Clipthrough.Services.IBackgroundOcrQueue
    {
        public IObservable<long> OcrCompleted { get; } = Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = Observable.Empty<System.Reactive.Unit>();
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static MainWindowViewModel CreateViewModelForCoverage(
        TemporaryDatabaseScope scope,
        IEmbeddingWorker embeddingWorker)
        => new(
            scope.ClipStoreService,
            new TestClipboardMonitorService(),
            new TestClipSampleDataService(),
            scope.SettingsService,
            new TestSystemInteractionService(),
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            new TestSessionLogService(),
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new SilentOcrQueue(),
            new Clipthrough.Services.BackgroundJobIndicator(),
            scope.DatabaseInitializer,
            embeddingWorker: embeddingWorker);
}
