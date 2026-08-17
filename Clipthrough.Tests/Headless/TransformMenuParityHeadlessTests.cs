using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Clipthrough.Models;
using Clipthrough.Views;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The list of text transformations exists three times: the enum, the table
/// MainWindow.axaml.cs builds the Edit menu and toolbar flyout from, and the
/// clip context menu written out by hand in MainWindow.axaml.
///
/// The third copy is not redundant - the context menu applies a transform to the
/// clip that was right-clicked rather than to the current selection, so its items
/// bind a different command on a different view model and cannot be generated
/// from the same table. What it is, is unenforced: AGENTS.md asks whoever adds a
/// transform to remember to add it in both places, and a menu that is merely
/// missing an entry looks exactly like a menu that was never supposed to have one.
///
/// These tests make that drift fail the build instead.
/// </summary>
public sealed class TransformMenuParityHeadlessTests
{
    private static IReadOnlyList<MenuItem> ContextMenuTransformItems(MainWindowTestHarness harness)
    {
        var menu = harness.ClipList.ContextMenu;
        Assert.NotNull(menu);

        menu!.Open(harness.ClipList);
        Dispatcher.UIThread.RunJobs();

        var items = menu.GetLogicalDescendants()
            .OfType<MenuItem>()
            .Where(m => m.CommandParameter is TextTransformation)
            .ToList();

        menu.Close();
        Dispatcher.UIThread.RunJobs();
        return items;
    }

    private static MainWindowTestHarness CreateHarnessWithSelection()
    {
        var harness = MainWindowTestHarness.Create();
        harness.SeedClips(1);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    [AvaloniaFact]
    public void TheContextMenuOffersExactlyTheTransformsTheOtherMenusDo()
    {
        using var harness = CreateHarnessWithSelection();

        var inContextMenu = ContextMenuTransformItems(harness)
            .Select(m => (TextTransformation)m.CommandParameter!)
            .ToHashSet();

        // Guards the walk itself: if the context menu never realised, both sides
        // would be compared as empty and agree perfectly.
        Assert.NotEmpty(inContextMenu);

        var inCodeMenus = TransformMenuCatalog.Entries.Select(e => e.Kind).ToHashSet();

        var missingFromContextMenu = inCodeMenus.Except(inContextMenu).OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
        var missingFromCodeMenus = inContextMenu.Except(inCodeMenus).OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();

        Assert.Empty(missingFromContextMenu);
        Assert.Empty(missingFromCodeMenus);
    }

    /// <summary>
    /// Same transform, same wording. A transform relabelled in one menu and not
    /// the other reads as two different features to anyone who meets both.
    /// </summary>
    [AvaloniaFact]
    public void TheTwoMenusLabelEachTransformIdentically()
    {
        using var harness = CreateHarnessWithSelection();

        var contextHeaders = ContextMenuTransformItems(harness)
            .ToDictionary(m => (TextTransformation)m.CommandParameter!, m => m.Header as string);

        Assert.NotEmpty(contextHeaders);

        var mismatched = TransformMenuCatalog.Entries
            .Where(e => contextHeaders.TryGetValue(e.Kind, out var header)
                && !string.Equals(header, e.Header, StringComparison.Ordinal))
            .Select(e => $"{e.Kind}: code menus say '{e.Header}', context menu says '{contextHeaders[e.Kind]}'")
            .ToList();

        Assert.Empty(mismatched);
    }

    /// <summary>
    /// Every transform the enum declares is offered somewhere, except the ones
    /// deliberately withheld. <c>None</c> is the no-op the click handler rejects,
    /// and <c>JoinWithDelimiter</c> needs a delimiter that a menu item cannot ask
    /// for. Naming them here means adding a transform and forgetting to surface
    /// it fails, while withholding one on purpose stays a one-line decision that
    /// has to be written down.
    /// </summary>
    [Fact]
    public void EveryTransformIsOfferedUnlessDeliberatelyWithheld()
    {
        var withheld = new HashSet<TextTransformation>
        {
            TextTransformation.None,
            TextTransformation.JoinWithDelimiter,
        };

        var offered = TransformMenuCatalog.Entries.Select(e => e.Kind).ToHashSet();

        var unreachable = Enum.GetValues<TextTransformation>()
            .Where(t => !withheld.Contains(t) && !offered.Contains(t))
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unreachable);

        // And the reverse, so a transform removed from the enum's menus without
        // being removed from `withheld` does not leave a stale exemption behind.
        var withheldButOffered = withheld.Where(offered.Contains).ToList();
        Assert.Empty(withheldButOffered);
    }
}
