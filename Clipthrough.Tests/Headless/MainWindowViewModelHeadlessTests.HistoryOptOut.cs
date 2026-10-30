using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>The clipboard-history opt-out setting (OD-10b, S39).</summary>
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

    /// <summary>
    /// Pins the S39 refinement: a drop is a deliberate import, so it is kept
    /// even when its source marks it CanIncludeInClipboardHistory = 0 and the
    /// opt-out setting is on. The same payload copied to the clipboard is
    /// skipped; changing either side should be a decision, not an accident.
    /// </summary>
    [AvaloniaFact]
    public async Task ADropMarkedNotForClipboardHistory_IsStillImported()
    {
        const string Payload = "dropped-NOT-A-REAL-PASSWORD";
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        Assert.True(scope.SettingsService.Current.HonorClipboardHistoryOptOut);

        var drop = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(Payload);
        item.Set(DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory"), new byte[] { 0, 0, 0, 0 });
        drop.Add(item);

        // The fixture is one clipboard capture would refuse.
        Assert.True(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(drop, honorHistoryOptOut: true));

        Assert.Equal(1, await viewModel.ImportDroppedDataAsync(drop, null));
        var stored = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Contains(stored.Items, c => c.Content == Payload);
    }
}