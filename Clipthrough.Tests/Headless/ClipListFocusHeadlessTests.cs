using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A refresh rebuilds the view model of every row whose clip changed, and
/// background enrichment — OCR text, the sensitivity scan, source-app icons —
/// makes that happen seconds after a capture, while the user is still arrowing
/// through the list.
///
/// Replacing a row destroys its container, and Avalonia then leaves *nothing*
/// focused rather than moving focus somewhere sensible. Measured before the
/// fix: focus went from a ListBoxItem to null, and the next Down arrow did
/// nothing at all — the keyboard was dead until the user clicked or tabbed back
/// in. That is most of what "arrow movement is glitchy" was.
/// </summary>
public sealed class ClipListFocusHeadlessTests
{
    /// <summary>
    /// Rebuilds the row at <paramref name="index"/> exactly as
    /// ApplyRefreshResultIncremental does when a clip's fields changed: dispose
    /// the old view model, insert a fresh one, restore the selection by id.
    /// </summary>
    private static void ReplaceRow(MainWindowTestHarness harness, int index, bool isSensitive = true)
    {
        var old = harness.ViewModel.Clips[index];
        var entry = new ClipEntry
        {
            Id = old.Id,
            Content = old.Clip.Content,
            ContentType = old.Clip.ContentType,
            ContentFormat = old.Clip.ContentFormat,
            SourceApp = old.Clip.SourceApp,
            Hash = old.Clip.Hash,
            LastCopiedAt = old.Clip.LastCopiedAt,
            FirstCopiedAt = old.Clip.FirstCopiedAt,
            IsSensitive = isSensitive,
        };

        var wasSelected = ReferenceEquals(harness.ViewModel.SelectedClip, old);
        harness.ViewModel.Clips.RemoveAt(index);
        harness.ViewModel.Clips.Insert(index, new ClipItemViewModel(entry));
        if (wasSelected)
        {
            harness.ViewModel.SelectedClip = harness.ViewModel.Clips[index];
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void ArrowIntoTheList(MainWindowTestHarness harness, int rows)
    {
        harness.FocusSearchBox();
        for (var i = 0; i < rows; i++)
        {
            harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.IsType<ListBoxItem>(TopLevel.GetTopLevel(harness.Window)!.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void ReplacingTheFocusedRow_LeavesTheKeyboardWorking()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        ArrowIntoTheList(harness, 2);

        var selectedBefore = harness.ViewModel.SelectedClip!.Id;
        ReplaceRow(harness, harness.ViewModel.Clips.IndexOf(harness.ViewModel.SelectedClip!));

        Assert.IsType<ListBoxItem>(TopLevel.GetTopLevel(harness.Window)!.FocusManager!.GetFocusedElement());
        Assert.True(harness.ClipList.IsKeyboardFocusWithin);

        // The real regression: focus can look plausible and the arrow keys still
        // be dead, because TryHandleArrowKeyNavigation bails out when the list
        // does not hold focus.
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(selectedBefore, harness.ViewModel.SelectedClip!.Id);
    }

    /// <summary>
    /// Replacing a row the user is not on must not drag focus onto it.
    /// </summary>
    [AvaloniaFact]
    public void ReplacingAnUnfocusedRow_LeavesTheSelectionAlone()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        ArrowIntoTheList(harness, 2);

        var selectedBefore = harness.ViewModel.SelectedClip!.Id;
        ReplaceRow(harness, 4);

        Assert.Equal(selectedBefore, harness.ViewModel.SelectedClip!.Id);
        Assert.True(harness.ClipList.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// A filter that matches nothing has no row to return to. Leaving focus
    /// nowhere strands the keyboard exactly as before, and the search box is the
    /// only place the user can act from.
    /// </summary>
    [AvaloniaFact]
    public void EmptyingTheList_ReturnsTheKeyboardToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        ArrowIntoTheList(harness, 2);

        harness.ViewModel.SelectedClip = null;
        harness.ViewModel.Clips.Clear();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// The restore must be confined to focus the list actually lost. A capture
    /// arriving while the user is typing a filter must not yank them out of the
    /// search box — and a clip stays selected after they arrow back up to refine
    /// the filter, so "there is a row to focus" is not permission to focus it.
    /// </summary>
    [AvaloniaFact]
    public void RefreshWhileTypingAFilter_DoesNotStealFocusFromTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);

        // Arrowing in first is what makes this test able to fail: with nothing
        // selected the restore has no row to move to and would leave the search
        // box focused however broken its guard was.
        ArrowIntoTheList(harness, 2);
        harness.FocusSearchBox();
        Assert.NotNull(harness.ViewModel.SelectedClip);

        ReplaceRow(harness, 0);
        harness.ViewModel.Clips.RemoveAt(3);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.False(harness.ClipList.IsKeyboardFocusWithin);
    }
}
