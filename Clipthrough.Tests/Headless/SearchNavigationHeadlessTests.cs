using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    /// <summary>
    /// Tab follows visual order rather than jumping to the clip list. It used to
    /// skip straight from the search box to the list, which left the filter
    /// toggles sitting visually between them reachable only by mouse. Down is
    /// still the one-key path into the list, so nothing got slower.
    /// </summary>
    [AvaloniaFact]
    public void Tab_FromSearchBox_MovesToTheFilterControlsNotStraightToTheList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();

        harness.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.False(harness.ClipList.IsKeyboardFocusWithin);

        var focused = TopLevel.GetTopLevel(harness.Window)!.FocusManager!.GetFocusedElement();
        Assert.IsType<ToggleButton>(focused);
    }

    /// <summary>
    /// The clip list must stay reachable by Tab alone now that the shortcut into
    /// it is gone, and the ring has to come back round rather than dead-ending.
    /// Both were real regressions waiting to happen when the jump was removed.
    /// </summary>
    [AvaloniaFact]
    public void TabRing_ReachesTheClipListAndWrapsBackToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();

        var reachedClipList = false;
        var wrappedToSearch = false;

        for (var i = 0; i < 60; i++)
        {
            harness.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();

            if (harness.ClipList.IsKeyboardFocusWithin)
            {
                reachedClipList = true;
            }
            else if (reachedClipList && harness.SearchBox.IsKeyboardFocusWithin)
            {
                wrappedToSearch = true;
                break;
            }
        }

        Assert.True(reachedClipList, "Tab never reaches the clip list, so it is mouse-only.");
        Assert.True(wrappedToSearch, "The tab ring does not wrap back to the search box.");
    }

    /// <summary>
    /// The splitter is a tab stop, so a screen reader has to be able to say what
    /// landing on it means; unnamed it is announced only as "custom".
    /// </summary>
    [AvaloniaFact]
    public void GridSplitter_HasAnAccessibleName()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(1);

        var splitter = harness.Window.GetVisualDescendants().OfType<GridSplitter>().Single();

        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(splitter)));
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

    /// <summary>
    /// A selection that is no longer in the list (the clip was deleted, or the
    /// filter changed under it) leaves <c>IndexOf</c> at -1. Up used to compute
    /// index -2, fall through every branch and still mark the key handled, so
    /// the user was stranded in the list with no way back to the search box.
    /// </summary>
    [AvaloniaFact]
    public void Up_WithASelectionThatIsNoLongerInTheList_ReturnsFocusToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();

        var orphan = harness.ViewModel.Clips[1];
        harness.ViewModel.Clips.Remove(orphan);
        Dispatcher.UIThread.RunJobs();

        // Assigned after the removal: removing a selected item makes the list
        // pick a neighbour, which would hide the dead end under test.
        harness.ViewModel.SelectedClip = orphan;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(-1, harness.ViewModel.Clips.IndexOf(harness.ViewModel.SelectedClip!));

        harness.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// Focusing the search box schedules retries at lower priorities to beat the
    /// menu bar's auto-focus on window activation. Those retries used to fire
    /// unconditionally, so a focus move issued after them -- the clip list taking
    /// focus, say -- was undone a frame later and the caret jumped back to the
    /// search box. The most recent request must win.
    /// </summary>
    [AvaloniaFact]
    public void ASearchBoxFocusRetry_DoesNotOverrideALaterMoveToTheClipList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();

        harness.Window.FocusSearchBox();
        harness.Window.FocusSelectedClipForTests();

        // Drains the queued focus jobs, including the low-priority retries.
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.True(harness.ClipList.IsKeyboardFocusWithin);
    }
    /// <summary>
    /// Shift+Tab is plain reverse traversal now that the jump back to the search
    /// box is gone. It must still leave the list - the editor's Tab trap fix
    /// depends on reverse traversal working - but it lands on whatever precedes
    /// the list visually, not on the search box.
    /// </summary>
    [AvaloniaFact]
    public void ShiftTab_FromClipList_MovesBackwardsInVisualOrder()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusClipList();

        harness.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.ClipList.IsKeyboardFocusWithin);
        Assert.NotNull(TopLevel.GetTopLevel(harness.Window)!.FocusManager!.GetFocusedElement());
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
