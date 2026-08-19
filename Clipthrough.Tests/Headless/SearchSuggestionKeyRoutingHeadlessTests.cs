using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Up and Down belong to the suggestion dropdown while it is showing, and
/// Ctrl+Up/Down is the explicit jump between the search box and the clip list.
/// </summary>
/// <remarks>
/// This reverses an earlier deliberate decision, so these tests carry the
/// reason. Down used to skip the dropdown for the clip list, because the
/// dropdown opens whenever the query substring-matches a past search - most of
/// the time - and sharing Down would have made the results unreachable exactly
/// when the query resembled an old one. Ctrl+Down removes that objection by
/// making the list reachable from anywhere, which is what let the plain keys go
/// back to behaving like an ordinary autocomplete.
///
/// Asaf reported the old behaviour as focus-stealing, which is the same fact
/// described from the user's side.
/// </remarks>
public sealed class SearchSuggestionKeyRoutingHeadlessTests
{
    private static ListBox Suggestions(MainWindowTestHarness harness)
        => harness.Window.GetVisualDescendants().OfType<ListBox>()
            .First(list => list.Name == "SearchSuggestionsList");

    /// <summary>
    /// Types a query that matches a stored recent search, so the dropdown opens.
    /// </summary>
    private static void OpenTheDropdown(MainWindowTestHarness harness)
    {
        harness.ViewModel.RecentSearches.Clear();
        harness.ViewModel.RecentSearches.Add("alpha one");
        harness.ViewModel.RecentSearches.Add("alpha two");
        harness.FocusSearchBox();
        harness.ViewModel.SearchText = "alpha";
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            harness.ViewModel.IsSearchSuggestionsOpen,
            "the dropdown did not open, so nothing below is about the dropdown");
    }

    private static void Press(MainWindowTestHarness harness, Key key, RawInputModifiers modifiers)
    {
        harness.Window.KeyPress(
            key,
            modifiers,
            key == Key.Down ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp,
            null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DownWithTheDropdownOpen_EntersTheDropdownRatherThanTheClipList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        OpenTheDropdown(harness);

        Press(harness, Key.Down, RawInputModifiers.None);

        Assert.True(Suggestions(harness).IsKeyboardFocusWithin, "Down did not reach the suggestion dropdown");
        Assert.False(harness.ClipList.IsKeyboardFocusWithin, "Down jumped to the clip list, which is the reported bug");
    }

    /// <summary>
    /// With no dropdown there is nothing for Down to navigate, so the one-key
    /// path into the results is kept rather than made Ctrl-only.
    /// </summary>
    [AvaloniaFact]
    public void DownWithNoDropdown_StillReachesTheClipList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        harness.FocusSearchBox();
        Assert.False(harness.ViewModel.IsSearchSuggestionsOpen);

        Press(harness, Key.Down, RawInputModifiers.None);

        Assert.True(harness.ClipList.IsKeyboardFocusWithin, "the one-key path into the results was lost");
    }

    /// <summary>
    /// The whole point of the new chord: the list stays reachable even when the
    /// dropdown has taken the plain keys.
    /// </summary>
    [AvaloniaFact]
    public void CtrlDownWithTheDropdownOpen_ReachesTheClipListAnyway()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        OpenTheDropdown(harness);

        Press(harness, Key.Down, RawInputModifiers.Control);

        Assert.True(harness.ClipList.IsKeyboardFocusWithin, "Ctrl+Down did not reach the clip list");
        Assert.False(Suggestions(harness).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void CtrlUpFromTheClipList_ReturnsToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        harness.FocusSearchBox();
        Press(harness, Key.Down, RawInputModifiers.None);
        Press(harness, Key.Down, RawInputModifiers.None);
        Assert.True(harness.ClipList.IsKeyboardFocusWithin, "setup failed: never got into the list");

        Press(harness, Key.Up, RawInputModifiers.Control);

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin, "Ctrl+Up did not return to the search box");
    }

    /// <summary>
    /// Ctrl+Up has to work from deep in the list, not only from the top row
    /// where plain Up already escapes.
    /// </summary>
    [AvaloniaFact]
    public void CtrlUpFromDeepInTheClipList_ReturnsToTheSearchBoxInOnePress()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        harness.FocusSearchBox();
        for (var i = 0; i < 6; i++)
        {
            Press(harness, Key.Down, RawInputModifiers.None);
        }

        Assert.True(harness.ClipList.SelectedIndex > 1, "setup failed: not deep enough for this to differ from plain Up");

        Press(harness, Key.Up, RawInputModifiers.Control);

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin, "Ctrl+Up did not return to the search box");
    }

    /// <summary>
    /// Up with the dropdown closed still cycles recent searches into the box,
    /// which is what Up meant before the dropdown existed.
    /// </summary>
    [AvaloniaFact]
    public void UpWithNoDropdown_StillReachesSearchHistory()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(10);
        harness.ViewModel.RecentSearches.Clear();
        harness.ViewModel.RecentSearches.Add("newest search");
        harness.FocusSearchBox();
        Assert.False(harness.ViewModel.IsSearchSuggestionsOpen);

        Press(harness, Key.Up, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("newest search", harness.ViewModel.SearchText);
    }
}
