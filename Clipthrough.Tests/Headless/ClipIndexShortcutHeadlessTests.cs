using System;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Coverage for the Ctrl+1..9 and Alt+1..9 clip shortcuts.
///
/// Ctrl+digit used to copy the clip and minimise the window, leaving the user to
/// press Ctrl+V themselves - which is the keystroke the shortcut exists to save.
/// It now drives the same copy/restore-foreground/hide/paste sequence Enter uses.
/// </summary>
public sealed class ClipIndexShortcutHeadlessTests
{
    [AvaloniaFact]
    public async Task CtrlDigit_PastesThatClipInsteadOfOnlyCopyingIt()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        harness.FocusClipList();

        harness.Window.KeyPress(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, "3");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Clips[2], harness.ViewModel.SelectedClip);

        await WaitForPasteAsync(harness);

        Assert.Equal(1, harness.SystemInteraction.SimulatedPasteCount);
        Assert.Equal("clip-3", harness.SystemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task CtrlDigit_BeyondTheEndOfTheList_DoesNothing()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(2);
        harness.FocusClipList();

        // Select something first, so an out-of-range shortcut that failed to bail
        // out would paste *this* clip rather than silently doing nothing.
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();

        harness.Window.KeyPress(Key.D5, RawInputModifiers.Control, PhysicalKey.Digit5, "5");
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Clips[0], harness.ViewModel.SelectedClip);
        Assert.Equal(0, harness.SystemInteraction.SimulatedPasteCount);
        Assert.Null(harness.SystemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task AltDigit_StillOnlySelects()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        harness.FocusClipList();

        harness.Window.KeyPress(Key.D2, RawInputModifiers.Alt, PhysicalKey.Digit2, "2");
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Clips[1], harness.ViewModel.SelectedClip);
        Assert.Equal(0, harness.SystemInteraction.SimulatedPasteCount);
    }

    private static async Task WaitForPasteAsync(MainWindowTestHarness harness)
    {
        // The paste sequence yields for the OS to process the foreground change,
        // so the keystroke lands well after the key event returns.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.SystemInteraction.SimulatedPasteCount == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
