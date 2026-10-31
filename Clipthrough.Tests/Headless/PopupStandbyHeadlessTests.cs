using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Dismissing the popup cloaks it into standby rather than hiding it
/// (MainWindow.Presentation.cs). A hidden window stops rendering and comes back
/// showing its last frame, so the popup used to reopen on the previous search,
/// then reset, then jump - every summon after a search. What these pin is the
/// state the window is in while nobody can see it: whatever the next summon
/// will show has to be right before the uncloak, because the uncloak is the
/// first frame the user gets.
/// </summary>
public sealed class PopupStandbyHeadlessTests
{
    private const int ClipCount = 30;

    private static async Task<MainWindowTestHarness> ReadyAsync()
    {
        var harness = MainWindowTestHarness.Create();
        for (var i = 0; i < ClipCount; i++)
        {
            var text = i == 3 ? "the needle clip" : $"ordinary clip {i}";
            await harness.ClipStore.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
                SourceApp = "Tests",
            });
        }

        harness.Settings.SetHasSavedSettings(true);
        harness.StorageOptions.SetHasSavedConfig(true);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetMainWindowVisible(true);
        await PumpUntilAsync(() => !harness.ViewModel.IsLoadingDatabase && harness.ViewModel.Clips.Count == ClipCount);
        Assert.Equal(ClipCount, harness.ViewModel.Clips.Count);
        return harness;
    }

    private static async Task SearchNeedleAsync(MainWindowTestHarness harness)
    {
        harness.ViewModel.SearchText = "needle";
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 1);
        Assert.Single(harness.ViewModel.Clips);
    }

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string? NewestContent(MainWindowTestHarness harness) => harness.ViewModel.Clips[0].Clip.Content;

    [AvaloniaFact]
    public async Task Dismiss_CloaksTheWindowInsteadOfHidingIt()
    {
        using var harness = await ReadyAsync();

        harness.Window.Dismiss();

        Assert.True(harness.Window.IsVisible, "the window was hidden, so it stopped rendering and will reopen on a stale frame");
        Assert.Contains(harness.Window, harness.SystemInteraction.CloakedWindows);
        Assert.True(harness.Window.IsInStandby);
        Assert.False(harness.Window.IsPresentedToUser);
        Assert.Contains(harness.SystemInteraction.CloakCalls, c => c.Window == harness.Window && c.Cloaked && c.ReleasedForeground);
    }

    /// <summary>
    /// The regression the change exists for: the first frame of a summon after a
    /// search must be the reset list, not the search results.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterASearch_TheSummonShowsTheResetList_NotTheSearch()
    {
        using var harness = await ReadyAsync();
        await SearchNeedleAsync(harness);

        string? searchAtUncloak = null;
        int clipsAtUncloak = -1;
        object? selectedAtUncloak = null;
        harness.SystemInteraction.OnUncloak = w =>
        {
            if (w != harness.Window) return;
            searchAtUncloak = harness.ViewModel.SearchText;
            clipsAtUncloak = harness.ViewModel.Clips.Count;
            selectedAtUncloak = harness.ViewModel.SelectedClip;
        };

        harness.Window.Dismiss();
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == ClipCount);
        Assert.True(await harness.Window.TryLeaveStandbyAsync());

        Assert.Equal(string.Empty, searchAtUncloak);
        Assert.Equal(ClipCount, clipsAtUncloak);
        Assert.Same(harness.ViewModel.Clips[0], selectedAtUncloak);
        Assert.True(harness.Window.IsPresentedToUser);
        Assert.DoesNotContain(harness.Window, harness.SystemInteraction.CloakedWindows);
    }

    /// <summary>
    /// A summon that comes before the cleared search has re-queried holds the
    /// window cloaked until it has, rather than uncloaking onto the old results.
    /// </summary>
    [AvaloniaFact]
    public async Task ASummonRightAfterTheDismiss_WaitsForTheResetBeforeUncloaking()
    {
        using var harness = await ReadyAsync();
        await SearchNeedleAsync(harness);

        int clipsAtUncloak = -1;
        harness.SystemInteraction.OnUncloak = w =>
        {
            if (w == harness.Window) clipsAtUncloak = harness.ViewModel.Clips.Count;
        };

        harness.Window.Dismiss();
        // No pumping: the re-query has not landed yet.
        Assert.Single(harness.ViewModel.Clips);
        Assert.True(await harness.Window.TryLeaveStandbyAsync());

        Assert.Equal(ClipCount, clipsAtUncloak);
    }

    /// <summary>
    /// Reset before cloaking: the selection is already back on the newest clip at
    /// the moment the window goes out of sight.
    /// </summary>
    [AvaloniaFact]
    public async Task Dismiss_SelectsTheNewestClip_BeforeTheCloak()
    {
        using var harness = await ReadyAsync();
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[7];

        object? selectedAtCloak = null;
        harness.SystemInteraction.OnCloak = w =>
        {
            if (w == harness.Window) selectedAtCloak = harness.ViewModel.SelectedClip;
        };

        harness.Window.Dismiss();

        Assert.Same(harness.ViewModel.Clips[0], selectedAtCloak);
    }

    [AvaloniaFact]
    public async Task WhileInStandby_ThereIsNoTaskbarButton_AndTheSummonBringsItBack()
    {
        using var harness = await ReadyAsync();
        Assert.True(harness.Window.ShowInTaskbar);

        bool? taskbarAtCloak = null;
        harness.SystemInteraction.OnCloak = w =>
        {
            if (w == harness.Window) taskbarAtCloak = harness.Window.ShowInTaskbar;
        };

        harness.Window.Dismiss();

        // Already off when the cloak hides and re-shows the window, or the
        // re-show would put the button straight back.
        Assert.False(taskbarAtCloak);
        Assert.False(harness.Window.ShowInTaskbar);

        await harness.Window.TryLeaveStandbyAsync();

        Assert.True(harness.Window.ShowInTaskbar);
    }

    /// <summary>
    /// The list stays live in standby, so a clip copied while the popup is
    /// dismissed is already in it - selected - when the popup comes back.
    /// </summary>
    [AvaloniaFact]
    public async Task ACaptureWhileInStandby_IsInTheListBeforeTheSummon()
    {
        using var harness = await ReadyAsync();
        harness.Window.Dismiss();
        await PumpUntilAsync(() => false, 100);

        var copied = await harness.ClipStore.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "copied while dismissed",
            ContentBytes = Encoding.UTF8.GetBytes("copied while dismissed"),
            SourceApp = "Tests",
        });
        Assert.NotNull(copied);
        harness.ClipboardMonitor.Emit(copied!);
        await PumpUntilAsync(() => NewestContent(harness) == "copied while dismissed");

        Assert.True(harness.Window.IsInStandby);
        Assert.Equal("copied while dismissed", NewestContent(harness));
        Assert.Same(harness.ViewModel.Clips[0], harness.ViewModel.SelectedClip);
    }

    /// <summary>
    /// The list stays live in standby, but a global hotkey still has to treat the
    /// popup as gone: its selection is not something the user is looking at.
    /// </summary>
    [AvaloniaFact]
    public async Task InStandby_TheSelectionIsNotAHotkeyTarget()
    {
        using var harness = await ReadyAsync();
        Assert.NotNull(harness.ViewModel.HotkeyTargetClip);

        harness.Window.Dismiss();

        Assert.NotNull(harness.ViewModel.SelectedClip);
        Assert.Null(harness.ViewModel.HotkeyTargetClip);

        await harness.Window.TryLeaveStandbyAsync();

        Assert.Same(harness.ViewModel.SelectedClip!.Clip, harness.ViewModel.HotkeyTargetClip);
    }

    [AvaloniaFact]
    public async Task WithoutCloaking_DismissHidesTheWindowAsBefore()
    {
        using var harness = await ReadyAsync();
        harness.SystemInteraction.CloakSupported = false;

        harness.Window.Dismiss();

        Assert.False(harness.Window.IsVisible);
        Assert.False(harness.Window.IsInStandby);
        Assert.True(harness.Window.ShowInTaskbar);
        Assert.Null(harness.ViewModel.HotkeyTargetClip);
        Assert.False(await harness.Window.TryLeaveStandbyAsync());
    }

    /// <summary>
    /// An owned window - Settings, the AI prompt, the logs - leaves with the
    /// popup and comes back with it, instead of floating on its own.
    /// </summary>
    [AvaloniaFact]
    public async Task OwnedWindows_GoIntoStandbyWithThePopup()
    {
        using var harness = await ReadyAsync();
        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        Assert.True(settings.IsVisible);

        harness.Window.Dismiss();

        Assert.Contains(settings, harness.SystemInteraction.CloakedWindows);
        Assert.Contains(harness.SystemInteraction.CloakCalls, c => c.Window == settings && !c.ReleasedForeground);

        await harness.Window.TryLeaveStandbyAsync();

        Assert.DoesNotContain(settings, harness.SystemInteraction.CloakedWindows);
        settings.Close();
    }

    /// <summary>
    /// Opening an owned window while the popup is in standby brings the popup
    /// back: the old check was IsVisible, which a cloaked window still is.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningAnOwnedWindowFromStandby_SummonsThePopup()
    {
        using var harness = await ReadyAsync();
        harness.Window.Dismiss();
        Assert.False(harness.Window.IsPresentedToUser);

        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Window.IsPresentedToUser);
        Assert.DoesNotContain(harness.Window, harness.SystemInteraction.CloakedWindows);
        harness.Window.OwnedWindows.OfType<SettingsWindow>().Single().Close();
    }

    /// <summary>
    /// Paste-and-hide: hand the foreground back, go into standby, then wait for
    /// the target - not a fixed sleep - and only then send Ctrl+V.
    /// </summary>
    [AvaloniaFact]
    public async Task Enter_PastesAfterTheTargetIsForward_NotAfterAFixedSleep()
    {
        using var harness = await ReadyAsync();
        harness.FocusClipList();

        harness.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        await PumpUntilAsync(() => harness.SystemInteraction.SimulatedPasteCount > 0);

        Assert.Equal(["restore", "cloak", "wait", "paste"], harness.SystemInteraction.Events.Where(e => e != "uncloak").ToArray());
        Assert.Equal([PasteTiming.AfterHideBudget], harness.SystemInteraction.PasteTargetWaits);
        Assert.True(harness.Window.IsInStandby);
    }
}