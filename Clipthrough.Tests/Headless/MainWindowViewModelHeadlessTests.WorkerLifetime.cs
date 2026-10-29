using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// F-23 through the view model: a settings save while a maintenance pause
/// holds the embedding worker must neither restart it nor be lost.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private sealed class IdleEmbedder : IEmbeddingService
    {
        public int Dimensions => 4;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new float[4]);
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>([.. new float[texts.Count][]]);
    }

    private static async Task<(TemporaryDatabaseScope Scope, EmbeddingWorker Worker, Clipthrough.ViewModels.MainWindowViewModel ViewModel)> WithRealWorkerAsync(bool semanticOn)
    {
        var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = semanticOn });
        var worker = new EmbeddingWorker(scope.ClipStoreService, new IdleEmbedder(), new BackgroundJobIndicator());
        var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), embeddingWorker: worker);
        await viewModel.InitializeAsync();
        viewModel.StartBackgroundServices();
        return (scope, worker, viewModel);
    }

    [AvaloniaFact]
    public async Task TurningSemanticSearchOn_WhilePaused_StartsTheWorkerOnlyOnRelease()
    {
        var (scope, worker, viewModel) = await WithRealWorkerAsync(semanticOn: false);
        using (scope)
        using (worker)
        using (viewModel)
        {
            Assert.False(worker.IsRunning);
            var pause = await worker.PauseAsync();

            scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = true });
            await viewModel.SemanticWorkerTransition;
            Assert.False(worker.IsRunning);

            await pause.DisposeAsync();
            Assert.True(worker.IsRunning);
            await worker.StopAsync();
        }
    }

    [AvaloniaFact]
    public async Task TurningSemanticSearchOff_WhilePaused_KeepsTheWorkerOffAfterRelease()
    {
        var (scope, worker, viewModel) = await WithRealWorkerAsync(semanticOn: true);
        using (scope)
        using (worker)
        using (viewModel)
        {
            Assert.True(worker.IsRunning);
            var pause = await worker.PauseAsync();

            scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = false });
            await viewModel.SemanticWorkerTransition;

            await pause.DisposeAsync();
            Assert.False(worker.IsRunning);
        }
    }
}