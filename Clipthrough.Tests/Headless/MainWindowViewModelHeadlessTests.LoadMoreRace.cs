using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A refresh decides how many rows to re-read when it builds its request, and
/// replaces the list with that many when it lands. A load-more page that landed
/// in between was therefore thrown away: the list shrank from 201 rows back to
/// 200 and the page the user had just scrolled in disappeared. Any refresh can
/// do it - a capture, an OCR result, a filter change - while a page is loading.
/// The refresh now discards a result that a load-more overtook and re-reads at
/// the new depth.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static async Task<(BlockingWriteClipStore Store, MainWindowViewModel ViewModel)> TwoHundredOfTwoHundredAndOneLoadedAsync(TemporaryDatabaseScope scope)
    {
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });
        var seedRequests = Enumerable.Range(1, 201)
            .Select(i => new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"page {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"page {i}"),
            })
            .ToList();
        Assert.Equal(201, (await scope.ClipStoreService.CaptureBatchAsync(seedRequests)).Imported);

        var store = new BlockingWriteClipStore(scope.ClipStoreService);
        var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: store);
        await viewModel.InitializeAsync();

        // The loading flag drops only once the startup refresh has landed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (viewModel.IsLoadingDatabase)
        {
            Assert.True(DateTime.UtcNow < deadline, "The view model's database did not finish opening.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        await viewModel.WhenRefreshesIdleAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, viewModel.Clips.Count);
        Assert.True(viewModel.HasMoreResults);
        return (store, viewModel);
    }

    /// <summary>
    /// The ordering the list's own scroll handler allows: the page starts loading
    /// while nothing is busy, a refresh is requested before it lands, and the
    /// refresh lands last.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_KeepsItsPage_WhenARefreshRequestedWhileItLoadsLandsAfterIt()
    {
        using var scope = new TemporaryDatabaseScope();
        var (store, viewModel) = await TwoHundredOfTwoHundredAndOneLoadedAsync(scope);
        using (viewModel)
        {
            var pageHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.BeforeSearch = filters =>
            {
                if (filters.Offset > 0)
                {
                    pageHeld.TrySetResult();
                    return releasePage.Task;
                }

                refreshHeld.TrySetResult();
                return releaseRefresh.Task;
            };

            Assert.False(viewModel.IsBusy);
            var loading = viewModel.LoadMoreCommand.Execute().ToTask();
            await pageHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var refreshing = viewModel.RefreshCommand.Execute().ToTask();
            await refreshHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

            releasePage.SetResult();
            await loading;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(201, viewModel.Clips.Count);

            releaseRefresh.SetResult();
            await refreshing;
            await viewModel.WhenRefreshesIdleAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(201, viewModel.Clips.Count);
        }
    }

    /// <summary>
    /// The other ordering: a refresh is already in flight when the page is
    /// requested. The scroll handler skips loading while the view model reports
    /// busy, but the command itself does not refuse, and the busy flag is raised
    /// by a dispatcher hop that a scroll can beat.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_KeepsItsPage_WhenItLandsDuringAnInFlightRefresh()
    {
        using var scope = new TemporaryDatabaseScope();
        var (store, viewModel) = await TwoHundredOfTwoHundredAndOneLoadedAsync(scope);
        using (viewModel)
        {
            var refreshHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.BeforeSearch = filters =>
            {
                if (filters.Offset > 0)
                {
                    return Task.CompletedTask;
                }

                refreshHeld.TrySetResult();
                return releaseRefresh.Task;
            };

            var refreshing = viewModel.RefreshCommand.Execute().ToTask();
            await refreshHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await viewModel.LoadMoreCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(201, viewModel.Clips.Count);

            releaseRefresh.SetResult();
            await refreshing;
            await viewModel.WhenRefreshesIdleAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(201, viewModel.Clips.Count);
        }
    }
}
