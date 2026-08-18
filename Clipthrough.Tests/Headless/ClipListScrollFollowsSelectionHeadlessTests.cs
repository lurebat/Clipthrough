using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Asaf reported that arrowing up and down the clip list does not bring the
/// list to the selected clip. <see cref="ClipListFocusHeadlessTests"/> already
/// covers where *focus* lands after a row is rebuilt, but nothing asserted
/// where the *viewport* ends up, and those are separately breakable: a list can
/// hold focus on a row that is scrolled off screen.
///
/// The measurements here deliberately avoid computing a row height and
/// multiplying. An earlier attempt at this did exactly that, divided the scroll
/// extent by the row count, got 56 where the container is 50 high, and invented
/// a six-pixel bug that did not exist. So ask the container where it actually
/// is, via TranslatePoint into the scroll viewport, and compare against the
/// viewport's own bounds.
///
/// KNOWN LIMIT - read before trusting a green run here. These tests pin the
/// user-visible *outcome* (the selected row ends up on screen). They do NOT
/// prove MainWindow's own ScrollIntoView calls work, and must not be cited as
/// if they did. Measured: replacing the body of ScrollSelectedClipIntoView with
/// a no-op leaves all four green, because focusing a ListBoxItem makes Avalonia
/// call BringIntoView itself and that alone satisfies every assertion below.
///
/// So the headless environment cannot distinguish "the app scrolled the list"
/// from "focus scrolled the list", and no test in this file can settle Asaf's
/// report that the list does not follow the selection. Settling it needs either
/// a path that moves the selection without moving focus, or observation of the
/// real window.
/// </summary>
public sealed class ClipListScrollFollowsSelectionHeadlessTests
{
    private static ScrollViewer Viewport(MainWindowTestHarness harness)
        => harness.ClipList.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>
    /// The selected row's rectangle expressed in the scroll viewport's own
    /// coordinates, or null when the row is not realised at all (virtualisation
    /// has recycled it, which means it is far outside the viewport).
    /// </summary>
    private static Rect? SelectedRowInViewport(MainWindowTestHarness harness)
    {
        var list = harness.ClipList;
        if (list.SelectedIndex < 0 || list.ContainerFromIndex(list.SelectedIndex) is not ListBoxItem row)
        {
            return null;
        }

        var viewport = Viewport(harness);
        if (row.TranslatePoint(default, viewport) is not { } topLeft)
        {
            return null;
        }

        return new Rect(topLeft, row.Bounds.Size);
    }

    private static void AssertSelectedRowIsVisible(MainWindowTestHarness harness, string because)
    {
        var viewport = Viewport(harness);
        var row = SelectedRowInViewport(harness);

        Assert.True(
            row is not null,
            $"{because}: the selected row has no realised container, so it is nowhere near the viewport.");

        // A row flush against either edge is fine; one hanging over it is not.
        Assert.True(
            row!.Value.Top >= -0.5 && row.Value.Bottom <= viewport.Bounds.Height + 0.5,
            $"{because}: selected row occupies {row.Value.Top:F1}..{row.Value.Bottom:F1} "
                + $"but the viewport is 0..{viewport.Bounds.Height:F1}.");
    }

    private static void Arrow(MainWindowTestHarness harness, Key key)
    {
        harness.Window.KeyPress(
            key,
            RawInputModifiers.None,
            key == Key.Down ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp,
            null);
        Dispatcher.UIThread.RunJobs();
        harness.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Walking down past the bottom of the viewport has to carry the viewport
    /// along. Asserted after every single press rather than only at the end,
    /// because a later press that happens to scroll correctly would otherwise
    /// paper over an earlier one that did not.
    /// </summary>
    [AvaloniaFact]
    public void ArrowingDownTheWholeList_KeepsTheSelectedRowOnScreen()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(40);
        harness.FocusSearchBox();

        var everLeftTheFirstScreen = false;
        for (var i = 0; i < 40; i++)
        {
            Arrow(harness, Key.Down);
            if (Viewport(harness).Offset.Y > 0)
            {
                everLeftTheFirstScreen = true;
            }

            AssertSelectedRowIsVisible(harness, $"after {i + 1} Down press(es)");
        }

        // Without this the test passes on a list short enough to need no
        // scrolling at all, which would assert nothing about following.
        Assert.True(everLeftTheFirstScreen, "the list never scrolled, so nothing was actually tested");
    }

    /// <summary>
    /// And back up again. Up has its own branch in the handler, including one
    /// that leaves the list entirely at index 0.
    /// </summary>
    [AvaloniaFact]
    public void ArrowingBackUpTheList_KeepsTheSelectedRowOnScreen()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(40);
        harness.FocusSearchBox();

        for (var i = 0; i < 39; i++)
        {
            Arrow(harness, Key.Down);
        }

        Assert.True(Viewport(harness).Offset.Y > 0, "setup failed: the list did not scroll on the way down");

        for (var i = 0; i < 38; i++)
        {
            Arrow(harness, Key.Up);
            AssertSelectedRowIsVisible(harness, $"after {i + 1} Up press(es)");
        }
    }

    /// <summary>
    /// The scenario the focus tests never covered: background enrichment (OCR,
    /// the sensitivity scan, source icons) rebuilds the selected row seconds
    /// after capture, while the user is partway down the list. Rebuilding a row
    /// destroys and recreates its container, and nothing in that path asks the
    /// list to scroll afterwards — so the selection can stay correct while the
    /// viewport snaps back to the top, which is exactly "it doesn't jump to the
    /// selected clip".
    /// </summary>
    [AvaloniaFact]
    public void RebuildingTheSelectedRowMidList_LeavesItOnScreen()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(40);
        harness.FocusSearchBox();

        for (var i = 0; i < 25; i++)
        {
            Arrow(harness, Key.Down);
        }

        Assert.True(Viewport(harness).Offset.Y > 0, "setup failed: the list did not scroll");
        AssertSelectedRowIsVisible(harness, "before the row was rebuilt");

        var index = harness.ClipList.SelectedIndex;
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
            IsSensitive = true,
        };

        harness.ViewModel.Clips.RemoveAt(index);
        harness.ViewModel.Clips.Insert(index, new ClipItemViewModel(entry));
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[index];
        Dispatcher.UIThread.RunJobs();
        harness.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertSelectedRowIsVisible(harness, "after the row was rebuilt");

        // And the keyboard still moves from where the user actually is, rather
        // than from the top of the list.
        var before = harness.ViewModel.SelectedClip!.Id;
        Arrow(harness, Key.Down);
        Assert.NotEqual(before, harness.ViewModel.SelectedClip!.Id);
        Assert.Equal(index + 1, harness.ClipList.SelectedIndex);
        AssertSelectedRowIsVisible(harness, "after arrowing on from the rebuilt row");
    }
}
