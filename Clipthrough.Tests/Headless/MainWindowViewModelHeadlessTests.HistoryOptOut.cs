using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>The clipboard-history opt-out setting through the Settings draft (OD-10b).</summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    [AvaloniaFact]
    public async Task TheHistoryOptOutSetting_IsSaved_AndRevertsWhenClosedWithoutSaving()
    {
        using var scope = new TemporaryDatabaseScope();
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        Assert.True(viewModel.Settings.HonorClipboardHistoryOptOut);

        viewModel.Settings.HonorClipboardHistoryOptOut = false;
        viewModel.CloseSettingsCommand.Execute().Subscribe();
        Assert.True(viewModel.Settings.HonorClipboardHistoryOptOut);

        viewModel.Settings.HonorClipboardHistoryOptOut = false;
        await viewModel.SaveSettingsCommand.Execute().ToTask();
        Assert.False(scope.SettingsService.Current.HonorClipboardHistoryOptOut);
    }
}