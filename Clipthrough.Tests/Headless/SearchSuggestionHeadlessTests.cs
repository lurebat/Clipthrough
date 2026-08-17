using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The recent-search suggestion dropdown.
///
/// It was wired to <c>SelectionChanged</c>, which caused two faults at once.
///
/// Clicking an entry crashed: applying a suggestion rewrites <c>SearchText</c>, which
/// clears and refills <c>FilteredRecentSearches</c> - the list's own <c>ItemsSource</c> -
/// and <c>SelectionChanged</c> is raised from inside the selection model's commit, so the
/// model was left indexing a collection that had been emptied underneath it:
/// <c>ArgumentOutOfRangeException</c> out of <c>SelectedItems.GetEnumerator</c>.
///
/// And the dropdown was unreachable by keyboard, because <c>SelectionChanged</c> also
/// fires while arrowing through a list - moving the highlight would have applied every
/// entry it passed over. The two faults compounded: the only way to use the feature was
/// the mouse, and the mouse was the path that crashed.
/// </summary>
public sealed class SearchSuggestionHeadlessTests
{
    [AvaloniaFact]
    public void MovingTheHighlight_DoesNotApplyTheSuggestion()
    {
        using var harness = MainWindowTestHarness.Create();
        var suggestions = SeedSuggestions(harness, "alpha query", "beta query");

        // Exactly what arrowing through the list does. Under SelectionChanged this
        // applied immediately, which is both the crash and the reason keyboard
        // navigation could not exist.
        suggestions.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("query", harness.ViewModel.SearchText);

        suggestions.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("query", harness.ViewModel.SearchText);
    }

    [AvaloniaFact]
    public void PressingEnter_AppliesTheHighlightedSuggestion()
    {
        using var harness = MainWindowTestHarness.Create();
        var suggestions = SeedSuggestions(harness, "alpha query", "beta query");

        EnterSuggestions(harness);
        suggestions.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        harness.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("beta query", harness.ViewModel.SearchText);
    }

    /// <summary>
    /// Down from the search box is the only keyboard route into the dropdown.
    /// </summary>
    [AvaloniaFact]
    public void DownFromTheSearchBox_MovesIntoTheSuggestions()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        var suggestions = SeedSuggestions(harness, "alpha query", "beta query");

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(suggestions.IsKeyboardFocusWithin);
        Assert.Equal(0, suggestions.SelectedIndex);

        // Anti-vacuity: Down must still reach the clip list when no suggestions are
        // showing, or this would have been "Down stopped working".
        Assert.False(harness.ClipList.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void DownFromTheSearchBox_WithNoSuggestions_StillReachesTheClipList()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.FocusSearchBox();
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.ViewModel.IsSearchSuggestionsOpen);

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ClipList.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// A search matching no clips is exactly when reaching for a different recent search
    /// is most useful, and the clip-list guard used to swallow the key before the
    /// suggestion branch could run.
    /// </summary>
    [AvaloniaFact]
    public void DownFromTheSearchBox_ReachesSuggestionsEvenWhenNoClipsMatch()
    {
        using var harness = MainWindowTestHarness.Create();
        var suggestions = SeedSuggestions(harness, "alpha query");

        Assert.Empty(harness.ViewModel.Clips);

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(suggestions.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void EscapeFromTheSuggestions_ReturnsToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        var suggestions = SeedSuggestions(harness, "alpha query", "beta query");

        EnterSuggestions(harness);

        harness.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
        Assert.Equal("query", harness.ViewModel.SearchText);
    }

    /// <summary>
    /// Up off the top returns to the box rather than trapping focus in a list the user
    /// arrowed into from above.
    /// </summary>
    [AvaloniaFact]
    public void UpFromTheFirstSuggestion_ReturnsToTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        var suggestions = SeedSuggestions(harness, "alpha query", "beta query");

        EnterSuggestions(harness);
        Assert.Equal(0, suggestions.SelectedIndex);

        harness.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    // Down from the search box is the real route in, and the only one: an Avalonia
    // ListBox is not focusable, so calling Focus() on the list does nothing at all.
    private static void EnterSuggestions(MainWindowTestHarness harness)
    {
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
    }

    // Types a query that matches every seeded entry, so the dropdown is open and
    // populated, and returns the list.
    private static ListBox SeedSuggestions(MainWindowTestHarness harness, params string[] recent)
    {
        foreach (var entry in recent)
        {
            harness.ViewModel.RecentSearches.Add(entry);
        }

        harness.FocusSearchBox();
        harness.ViewModel.SearchText = "query";
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.IsSearchSuggestionsOpen);
        Assert.Equal(recent.Length, harness.ViewModel.FilteredRecentSearches.Count);

        var suggestions = harness.Window.FindControl<ListBox>("SearchSuggestionsList");
        Assert.NotNull(suggestions);
        return suggestions;
    }
}
