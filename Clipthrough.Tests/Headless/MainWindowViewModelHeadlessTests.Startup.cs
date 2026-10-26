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
}