using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The motion policy reaches the UI as the <c>motion</c> class on the popup and
/// on the windows it owns; every transition and animation in ModernTheme.axaml
/// is scoped under it, so a window without the class does not animate at all.
/// </summary>
public sealed class MotionPolicyHeadlessTests
{
    private static (MainWindowTestHarness Harness, MotionService Motion) Create(MotionPreference preference, bool osAnimations = true)
    {
        var system = new TestSystemInteractionService { OsAnimationsEnabled = osAnimations };
        MotionService? motion = null;
        var harness = MainWindowTestHarness.Create(
            configureSettings: s => s with { Motion = preference },
            motionFactory: settings => motion = new MotionService(settings, system));
        return (harness, motion!);
    }

    [AvaloniaFact]
    public void WithMotionOn_ThePopupCarriesTheMotionClass()
    {
        var (harness, _) = Create(MotionPreference.On);
        using var _ = harness;

        Assert.Contains("motion", harness.Window.Classes);
    }

    [AvaloniaFact]
    public void System_WithWindowsAnimationsOff_ThePopupDoesNotAnimate()
    {
        var (harness, _) = Create(MotionPreference.System, osAnimations: false);
        using var _ = harness;

        Assert.DoesNotContain("motion", harness.Window.Classes);
    }

    [AvaloniaFact]
    public void TurningMotionOff_TakesTheClassOffAtOnce()
    {
        var (harness, _) = Create(MotionPreference.On);
        using var _ = harness;

        harness.Settings.SetCurrent(harness.Settings.Current with { Motion = MotionPreference.Off });
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("motion", harness.Window.Classes);
    }

    [AvaloniaFact]
    public void AnOwnedWindowOpenedLater_FollowsThePolicyToo()
    {
        var (harness, _) = Create(MotionPreference.On);
        using var _ = harness;

        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();

        Assert.Contains("motion", settings.Classes);

        harness.Settings.SetCurrent(harness.Settings.Current with { Motion = MotionPreference.Off });
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("motion", settings.Classes);
        settings.Close();
    }

    [AvaloniaFact]
    public async Task TheSettingsDialog_LoadsAndSavesTheMotionPreference()
    {
        using var harness = MainWindowTestHarness.Create(configureSettings: s => s with { Motion = MotionPreference.On });
        await HeadlessStartup.InitializeAndWaitForDatabaseAsync(harness.ViewModel);
        for (var i = 0; i < 100 && harness.ViewModel.IsLoadingDatabase; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MotionPreference.On, harness.ViewModel.Settings.Motion);
        Assert.Equal([MotionPreference.System, MotionPreference.On, MotionPreference.Off], harness.ViewModel.Settings.MotionOptions);

        harness.ViewModel.Settings.Motion = MotionPreference.Off;
        await harness.ViewModel.SaveSettingsCommand.Execute().ToTask();

        Assert.Equal(MotionPreference.Off, harness.Settings.Current.Motion);
    }
}