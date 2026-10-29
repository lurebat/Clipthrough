using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Regex search through the refresh queue (F-11). The queue runs one refresh
/// at a time, so a search that never returned used to freeze the list until
/// restart: clearing the box and new captures no longer updated it.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static string[] Contents(MainWindowViewModel viewModel)
        => [.. viewModel.Clips.Select(c => c.Clip.Content).OrderBy(s => s, StringComparer.Ordinal)];

    [AvaloniaFact]
    public async Task ASlowRegexOnTheBacktrackingEngine_ReportsTooSlow_KeepsTheList_AndDoesNotWedgeTheQueue()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(AppSettings.Default with { RegexSearchEngine = RegexSearchEngine.Backtracking });
        await CaptureTextClipAsync(scope.ClipStoreService, new string('a', 40) + "!");
        await CaptureTextClipAsync(scope.ClipStoreService, "hello world");

        using var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 2);
        viewModel.SetMainWindowVisible(true);
        var before = Contents(viewModel);

        viewModel.UseRegexSearch = true;
        viewModel.SearchText = "(a+)+$";

        Assert.True(
            await WaitUntilAsync(() => viewModel.StatusText == AppText.RegexTooSlowStatus, TimeSpan.FromSeconds(10)),
            $"Status was '{viewModel.StatusText}'.");
        Assert.Equal(before, Contents(viewModel));

        // The next search still runs.
        viewModel.SearchText = "hello";
        Assert.True(
            await WaitUntilAsync(() => Contents(viewModel).SequenceEqual(["hello world"]), TimeSpan.FromSeconds(10)),
            "A search after the slow one never refreshed the list.");

        viewModel.SearchText = string.Empty;
        Assert.True(
            await WaitUntilAsync(() => viewModel.Clips.Count == 2, TimeSpan.FromSeconds(10)),
            "Clearing the search after the slow one never refreshed the list.");
    }

    [AvaloniaFact]
    public async Task ABackreferenceOnTheDefaultEngine_SaysItNeedsBacktracking()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "aa");
        await CaptureTextClipAsync(scope.ClipStoreService, "ab");

        using var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 2);
        viewModel.SetMainWindowVisible(true);

        viewModel.UseRegexSearch = true;
        viewModel.SearchText = @"(\w)\1";

        Assert.True(
            await WaitUntilAsync(() => viewModel.StatusText == AppText.RegexNeedsBacktrackingStatus, TimeSpan.FromSeconds(10)),
            $"Status was '{viewModel.StatusText}'.");
        Assert.Equal(2, viewModel.Clips.Count);
    }

    /// <summary>
    /// A search the user has already moved on from must not hold the queue.
    /// The gated store parks the "beta" search after it has its rows; only
    /// cancellation can release it before the gate's 30 s safety timeout.
    /// </summary>
    [AvaloniaFact]
    public async Task ASupersededSearch_IsCancelled_SoTheNewQueryAppliesPromptly()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "alpha");
        await CaptureTextClipAsync(scope.ClipStoreService, "beta");

        var gated = new GatedSearchClipStore(scope.ClipStoreService);
        using var viewModel = CreateViewModel(
            scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), clipStore: gated);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        Assert.True(await WaitUntilAsync(() => viewModel.Clips.Count == 2, TimeSpan.FromSeconds(10)));

        viewModel.SearchText = "alpha";
        Assert.True(await WaitUntilAsync(() => Contents(viewModel).SequenceEqual(["alpha"]), TimeSpan.FromSeconds(10)));

        gated.ArmGate();
        viewModel.SearchText = "beta";
        Assert.True(await WaitUntilAsync(() => gated.IsParked, TimeSpan.FromSeconds(10)), "Setup: the beta search never parked.");

        viewModel.SearchText = string.Empty;
        try
        {
            Assert.True(
                await WaitUntilAsync(() => viewModel.Clips.Count == 2, TimeSpan.FromSeconds(5)),
                "The cleared search waited behind the superseded one.");
        }
        finally
        {
            gated.ReleaseGate();
        }
    }

    [AvaloniaFact]
    public async Task TheRegexEngineSetting_RevertsWhenSettingsAreClosedWithoutSaving()
    {
        using var scope = new TemporaryDatabaseScope();
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();

        viewModel.Settings.UseBacktrackingRegex = true;
        Assert.Equal(RegexSearchEngine.Backtracking, viewModel.Settings.RegexSearchEngine);
        viewModel.CloseSettingsCommand.Execute().Subscribe();

        Assert.Equal(RegexSearchEngine.NonBacktracking, viewModel.Settings.RegexSearchEngine);
        Assert.False(viewModel.Settings.UseBacktrackingRegex);
    }

    [AvaloniaFact]
    public async Task TheRegexEngineSetting_IsSaved()
    {
        using var scope = new TemporaryDatabaseScope();
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();

        viewModel.Settings.UseBacktrackingRegex = true;
        await System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask(viewModel.SaveSettingsCommand.Execute());

        Assert.Equal(RegexSearchEngine.Backtracking, scope.SettingsService.Current.RegexSearchEngine);
    }
}
