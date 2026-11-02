using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// F-36 / OD-9b. Search history recorded the query every time a refresh landed,
/// so typing with pauses stored each prefix: ["invoice 2024", "invoice 2",
/// "inv"]. It now records only when the user commits: Enter, choosing a
/// result, or moving into the results with the keyboard.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static async Task SettleRefreshAsync()
    {
        // The search box refreshes 300 ms after the last keystroke.
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(TemporaryDatabaseScope Scope, Clipthrough.ViewModels.MainWindowViewModel ViewModel)> SearchableAsync()
    {
        var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "invoice 2024 for the quarter");
        var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        await SettleRefreshAsync();
        return (scope, viewModel);
    }

    [AvaloniaFact]
    public async Task TypingWithPauses_RecordsNothing()
    {
        var (scope, viewModel) = await SearchableAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.SearchText = "inv";
            await SettleRefreshAsync();
            viewModel.SearchText = "invoice 2";
            await SettleRefreshAsync();
            viewModel.SearchText = "invoice 2024";
            await SettleRefreshAsync();

            Assert.Contains(viewModel.Clips, c => c.Clip.Content.Contains("invoice 2024", StringComparison.Ordinal));
            Assert.Empty(await scope.SearchHistoryService.GetRecentSearchesAsync());
        }
    }

    [AvaloniaFact]
    public async Task Enter_RecordsTheQueryOnce()
    {
        var (scope, viewModel) = await SearchableAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.SearchText = "invoice 2024";
            await SettleRefreshAsync();

            await viewModel.CommitSearchQueryAsync();
            await viewModel.CommitSearchQueryAsync();

            Assert.Equal(["invoice 2024"], await scope.SearchHistoryService.GetRecentSearchesAsync());
        }
    }

    [AvaloniaFact]
    public async Task ChoosingAResult_RecordsTheQueryOnce()
    {
        var (scope, viewModel) = await SearchableAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.SearchText = "invoice 2024";
            await SettleRefreshAsync();
            viewModel.SelectedClip = viewModel.Clips.First();

            await viewModel.CopySelectedCommand.Execute().ToTask();
            await viewModel.CopySelectedCommand.Execute().ToTask();

            Assert.Equal(["invoice 2024"], await WaitForRecordedSearchesAsync(scope));
        }
    }

    /// <summary>
    /// Choosing a result records the query on the thread pool and does not wait
    /// for it, so the row lands some time after the copy returns. A fixed 100 ms
    /// sleep lost that race whenever the write was slow (a loaded machine, or a
    /// background writer holding the lock). Waits for the row itself.
    /// </summary>
    private static async Task<System.Collections.Generic.IReadOnlyList<string>> WaitForRecordedSearchesAsync(TemporaryDatabaseScope scope)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var searches = await scope.SearchHistoryService.GetRecentSearchesAsync();
            if (searches.Count > 0 || DateTime.UtcNow > deadline)
            {
                return searches;
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Rubber-duck F-09-3. Ctrl+Shift+C (and the Copy as plain text menu items)
    /// copies the chosen result just as Copy does, but went through its own
    /// method that never committed the search, so the query was lost.
    /// </summary>
    [AvaloniaFact]
    public async Task CopyingAResultAsPlainText_RecordsTheQuery()
    {
        var (scope, viewModel) = await SearchableAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.SearchText = "invoice 2024";
            await SettleRefreshAsync();
            viewModel.SelectedClip = viewModel.Clips.First();

            await viewModel.CopySelectedAsPlainTextAsync();

            // The commit is fire-and-forget; wait for it rather than for a fixed delay.
            var recorded = await scope.SearchHistoryService.GetRecentSearchesAsync();
            for (var i = 0; i < 100 && recorded.Count == 0; i++)
            {
                await Task.Delay(50);
                recorded = await scope.SearchHistoryService.GetRecentSearchesAsync();
            }

            Assert.Equal(["invoice 2024"], recorded);
        }
    }

    [AvaloniaFact]
    public async Task AnEmptyQuery_IsNeverRecorded()
    {
        var (scope, viewModel) = await SearchableAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.SearchText = "   ";
            await SettleRefreshAsync();
            await viewModel.CommitSearchQueryAsync();
            viewModel.SelectedClip = viewModel.Clips.First();
            await viewModel.CopySelectedCommand.Execute().ToTask();
            await Task.Delay(100);

            Assert.Empty(await scope.SearchHistoryService.GetRecentSearchesAsync());
        }
    }
}