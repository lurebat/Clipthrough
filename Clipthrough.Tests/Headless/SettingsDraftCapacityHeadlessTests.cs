using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The main window's storage and entry capacity readouts are derived from the settings
/// *draft*, so they follow a limit as the user types it rather than waiting for a save.
///
/// That dependency used to be four <c>RaisePropertyChanged</c> calls copied into the
/// setters of four limit properties on the host view model. Moving those properties to
/// <see cref="SettingsViewModel"/> would have dropped the notifications on the floor, and
/// nothing would have failed: the readout would simply have stopped updating until some
/// unrelated refresh happened to raise it. Nothing covered this before.
/// </summary>
public sealed class SettingsDraftCapacityHeadlessTests
{
    [AvaloniaFact]
    public void EditingTheLibraryLimit_UpdatesTheStorageCapacityReadout()
    {
        using var harness = MainWindowTestHarness.Create();
        var raised = Track(harness.ViewModel);

        harness.ViewModel.Settings.EnableMaxLibrarySize = true;
        harness.ViewModel.Settings.MaxLibrarySizeMegabytes = "512";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(MainWindowViewModel.StorageCapacityText), raised);
        Assert.Contains(nameof(MainWindowViewModel.StorageUsagePercent), raised);

        // The value, not just the notification: a readout that fires and reports the old
        // number is no better than one that never fires.
        Assert.Contains("512", harness.ViewModel.StorageCapacityText);
    }

    [AvaloniaFact]
    public void EditingTheEntryLimit_UpdatesTheEntryCapacityReadout()
    {
        using var harness = MainWindowTestHarness.Create();
        var raised = Track(harness.ViewModel);

        harness.ViewModel.Settings.EnableMaxEntryCount = true;
        // Under a thousand on purpose: the readout formats with {0:N0}, so a larger
        // number acquires a group separator that differs by culture.
        harness.ViewModel.Settings.MaxEntryCount = "424";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(MainWindowViewModel.EntryCapacityText), raised);
        Assert.Contains(nameof(MainWindowViewModel.EntryUsagePercent), raised);
        Assert.Contains("424", harness.ViewModel.EntryCapacityText);
    }

    /// <summary>
    /// Turning a limit off must say "unlimited" rather than keep showing the number that
    /// is no longer applied.
    /// </summary>
    [AvaloniaFact]
    public void DisablingALimit_ReportsUnlimited()
    {
        using var harness = MainWindowTestHarness.Create();

        harness.ViewModel.Settings.EnableMaxLibrarySize = true;
        harness.ViewModel.Settings.MaxLibrarySizeMegabytes = "512";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("512", harness.ViewModel.StorageCapacityText);

        harness.ViewModel.Settings.EnableMaxLibrarySize = false;
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("512", harness.ViewModel.StorageCapacityText);
    }

    /// <summary>
    /// Anti-vacuity: the subscription must be selective. If it re-raised the capacity
    /// properties for every change on the draft, the tests above would pass while the
    /// notification carried no information, and every keystroke anywhere in the settings
    /// form would invalidate unrelated bindings.
    /// </summary>
    [AvaloniaFact]
    public void EditingAnUnrelatedSetting_DoesNotTouchTheCapacityReadouts()
    {
        using var harness = MainWindowTestHarness.Create();
        var raised = Track(harness.ViewModel);

        harness.ViewModel.Settings.ExternalEditorPath = @"C:\somewhere\editor.exe";
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(nameof(MainWindowViewModel.StorageCapacityText), raised);
        Assert.DoesNotContain(nameof(MainWindowViewModel.EntryCapacityText), raised);
    }

    private static List<string> Track(MainWindowViewModel viewModel)
    {
        var raised = new List<string>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
            {
                raised.Add(name);
            }
        };
        return raised;
    }
}
