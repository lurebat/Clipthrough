using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Regression coverage for keyboard navigation between the search box and the
/// clip list.
///
/// <c>TryHandleArrowKeyNavigation</c> used to bail out for key events sourced
/// from <em>any</em> text input control. The search box is a TextBox, so every
/// branch below that guard which tested <c>isSearchFocused</c> — Tab into the
/// list, Down into the list, Alt+Up/Down and plain Up through the search
/// history — was unreachable: the code existed but never ran. The guard now
/// only excludes multi-line editors, matching the Escape handler.
///
/// Home/End deliberately keep their caret-movement meaning inside the search
/// box and only jump the clip list when the list itself has focus.
/// </summary>
public sealed class SearchNavigationHeadlessTests
{
    [AvaloniaFact]
    public void Tab_FromSearchBox_MovesFocusToClipList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();

        harness.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.NotNull(harness.ViewModel.SelectedClip);
    }

    [AvaloniaFact]
    public void Down_FromSearchBox_MovesFocusToClipListAndSelectsAClip()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.NotNull(harness.ViewModel.SelectedClip);
    }

    /// <summary>
    /// Down must not steal focus when there is nothing to move to, otherwise the
    /// user is stranded outside the search box on an empty history.
    /// </summary>
    [AvaloniaFact]
    public void Down_FromSearchBox_DoesNothingWhenThereAreNoClips()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.FocusSearchBox();

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.Null(harness.ViewModel.SelectedClip);
    }

    [AvaloniaFact]
    public void Down_ThenUp_WalksTheClipListAndReturnsToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[1], harness.ViewModel.SelectedClip);

        harness.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[0], harness.ViewModel.SelectedClip);

        // Up from the first clip returns focus to the search box.
        harness.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void ShiftTab_FromClipList_ReturnsFocusToSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();

        harness.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    [AvaloniaTheory]
    [InlineData(Key.Home, PhysicalKey.Home)]
    [InlineData(Key.End, PhysicalKey.End)]
    public void HomeAndEnd_InTheSearchBox_MoveTheCaretRatherThanTheSelection(Key key, PhysicalKey physicalKey)
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();

        harness.Window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.Null(harness.ViewModel.SelectedClip);
    }

    [AvaloniaFact]
    public void Home_InTheClipList_JumpsToTheFirstClip()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[2];

        harness.Window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Clips[0], harness.ViewModel.SelectedClip);
    }

    [AvaloniaFact]
    public void End_InTheClipList_JumpsToTheLastClip()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];

        harness.Window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Clips[^1], harness.ViewModel.SelectedClip);
    }
}
