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
        Assert.Equal(StandbyLeave.Left, await harness.Window.TryLeaveStandbyAsync());

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
        Assert.Equal(StandbyLeave.Left, await harness.Window.TryLeaveStandbyAsync());

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
        Assert.Equal(StandbyLeave.NotInStandby, await harness.Window.TryLeaveStandbyAsync());
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

    /// <summary>
    /// A cloaked window is still a visible window to Windows, which can activate
    /// it - when the app in front closes, say. Typing would then land in a search
    /// box nobody can see, so an activation in standby hands the foreground on
    /// again instead of keeping it.
    /// </summary>
    [AvaloniaFact]
    public async Task AnActivationInStandby_HandsTheForegroundOnAgain()
    {
        using var harness = await ReadyAsync();
        harness.Window.Dismiss();
        var releases = harness.SystemInteraction.CloakCalls.Count(c => c.Window == harness.Window && c.Cloaked && c.ReleasedForeground);

        harness.Window.Activate();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Window.IsInStandby);
        Assert.Equal(releases + 1, harness.SystemInteraction.CloakCalls.Count(c => c.Window == harness.Window && c.Cloaked && c.ReleasedForeground));
    }

    [AvaloniaFact]
    public async Task AnActivationWhilePresented_IsLeftAlone()
    {
        using var harness = await ReadyAsync();

        harness.Window.Activate();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(harness.SystemInteraction.CloakCalls);
    }

    /// <summary>
    /// Alt+V twice before the first summon has shown anything: the second press
    /// cancels the first, as it would have hidden a popup that had appeared. The
    /// pending summon must then neither uncloak nor ask to be activated.
    /// </summary>
    [AvaloniaFact]
    public async Task ASecondToggleBeforeTheSummonLands_CancelsIt()
    {
        using var harness = await ReadyAsync();
        await SearchNeedleAsync(harness);
        harness.Window.Dismiss();

        var summon = harness.Window.TryLeaveStandbyAsync();
        Assert.True(harness.Window.IsSummonPending);
        harness.Window.CancelPendingSummon();

        Assert.Equal(StandbyLeave.Superseded, await summon);
        Assert.True(harness.Window.IsInStandby);
        Assert.Contains(harness.Window, harness.SystemInteraction.CloakedWindows);
        Assert.False(harness.Window.IsSummonPending);
    }

    /// <summary>
    /// A summon still waiting on the old reset must not undo what happened while
    /// it waited: here an owned window brought the popup back and it was then
    /// dismissed again. The old summon would have uncloaked the new standby.
    /// </summary>
    [AvaloniaFact]
    public async Task AStaleSummon_DoesNotUndoALaterDismiss()
    {
        using var harness = await ReadyAsync();
        await SearchNeedleAsync(harness);
        harness.Window.Dismiss();

        var stale = harness.Window.TryLeaveStandbyAsync();
        Assert.True(harness.Window.LeaveStandbyNow());
        harness.Window.Dismiss();

        Assert.Equal(StandbyLeave.Superseded, await stale);
        Assert.True(harness.Window.IsInStandby);
        Assert.Contains(harness.Window, harness.SystemInteraction.CloakedWindows);
    }

    /// <summary>A summon still waiting when the window closes uncloaks nothing.</summary>
    [AvaloniaFact]
    public async Task ASummonPendingWhenTheWindowCloses_IsDropped()
    {
        using var harness = await ReadyAsync();
        await SearchNeedleAsync(harness);
        harness.Window.Dismiss();

        var summon = harness.Window.TryLeaveStandbyAsync();
        harness.Window.Close();

        Assert.Equal(StandbyLeave.Superseded, await summon);
        Assert.DoesNotContain(harness.SystemInteraction.CloakCalls, c => c.Window == harness.Window && !c.Cloaked);
    }
}