using System;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The startup helper every headless test relies on (F-30). The settings double
/// holds the background database start for 1.5 s - the injection that failed 11
/// of 65 view-model tests - and the helper must not return before it is done.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    [AvaloniaFact]
    public async Task TheStartupHelper_ReturnsOnlyOnceTheDatabaseIsOpen_EvenWhenOpeningIsSlow()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetHasSavedSettings(false);
        scope.SettingsService.InitializeDelay = TimeSpan.FromMilliseconds(1500);
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());

        await HeadlessStartup.InitializeAndWaitForDatabaseAsync(viewModel);

        Assert.False(viewModel.IsLoadingDatabase);
        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "visible after startup");
        await viewModel.RefreshCommand.Execute().ToTask();
        Assert.Contains(viewModel.Clips, c => c.Clip.Id == clip.Id);
    }

    /// <summary>
    /// Rubber-duck F-08-3. CreateLoadedViewModelAsync used to poll the clip count
    /// for about a second and then assert it, so a startup slower than that - a
    /// loaded CI agent - failed its seven callers with a genuine assertion, which
    /// the CI gate rightly refuses to retry. Startup is held here for three
    /// seconds, well past that budget; the helper must wait for startup itself.
    /// </summary>
    [AvaloniaFact]
    public async Task TheLoadedViewModelHelper_WaitsForASlowStartup()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "first");
        await CaptureTextClipAsync(scope.ClipStoreService, "second");
        scope.SettingsService.SetHasSavedSettings(false);
        scope.SettingsService.InitializeDelay = TimeSpan.FromSeconds(3);

        using var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 2);

        Assert.True(viewModel.DatabaseStartupTask is { IsCompleted: true }, "The helper returned before the database start finished.");
        Assert.False(viewModel.IsLoadingDatabase);
    }
}