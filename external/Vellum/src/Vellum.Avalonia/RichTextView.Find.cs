using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>
/// Find and replace, per architecture 10.3 P7.
/// </summary>
/// <remarks>
/// <para>
/// The search itself lives in <see cref="DocumentSearch"/> and knows nothing about a view. This
/// file is the part that needs one: keeping the match list in step with an edited document,
/// painting the matches, and stepping the selection through them.
/// </para>
/// <para>
/// Matches are re-found whenever the document changes rather than mapped through the transaction.
/// Mapping is what the position machinery is for and it would be faster, but it is also how a
/// find bar goes subtly wrong — a replacement that changes a length shifts every later match, a
/// paste can create new matches out of nothing, and an undo can resurrect them. Re-finding cannot
/// drift. It is linear in the document per edit, which is the same order as the layout work an
/// edit already causes, and it only happens while a search is actually open.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>Defines the <see cref="FindHighlightBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> FindHighlightBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(FindHighlightBrush), new SolidColorBrush(Color.FromArgb(90, 255, 196, 0)));

    /// <summary>Defines the <see cref="CurrentFindHighlightBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> CurrentFindHighlightBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(CurrentFindHighlightBrush), new SolidColorBrush(Color.FromArgb(160, 255, 150, 0)));

    /// <summary>Defines the <see cref="MatchCount"/> property.</summary>
    public static readonly DirectProperty<RichTextView, int> MatchCountProperty =
        AvaloniaProperty.RegisterDirect<RichTextView, int>(nameof(MatchCount), o => o.MatchCount);

    /// <summary>Defines the <see cref="CurrentMatch"/> property.</summary>
    public static readonly DirectProperty<RichTextView, int> CurrentMatchProperty =
        AvaloniaProperty.RegisterDirect<RichTextView, int>(nameof(CurrentMatch), o => o.CurrentMatch);

    private ImmutableArray<SearchMatch> _matches = [];
    private DocumentNode? _matchesFor;
    private string _query = string.Empty;
    private SearchOptions _searchOptions = SearchOptions.Default;
    private int _current = -1;

    /// <summary>The fill behind every match of the current search.</summary>
    public IBrush? FindHighlightBrush
    {
        get => GetValue(FindHighlightBrushProperty);
        set => SetValue(FindHighlightBrushProperty, value);
    }

    /// <summary>The fill behind the match the user is on.</summary>
    public IBrush? CurrentFindHighlightBrush
    {
        get => GetValue(CurrentFindHighlightBrushProperty);
        set => SetValue(CurrentFindHighlightBrushProperty, value);
    }

    /// <summary>Every match of the current search, in document order.</summary>
    public ImmutableArray<SearchMatch> Matches => _matches;

    /// <summary>How many matches the current search has.</summary>
    /// <remarks>
    /// Observable, unlike <see cref="Matches"/>, because a find bar's "3 of 7" has to follow an
    /// edit without anything asking it to.
    /// </remarks>
    public int MatchCount => _matches.Length;

    /// <summary>The index into <see cref="Matches"/> the user is on, or -1.</summary>
    public int CurrentMatch => _current;

    /// <summary>The query the highlights belong to, empty when no search is open.</summary>
    public string Query => _query;

    /// <summary>Runs a search, replacing any previous one.</summary>
    /// <param name="query">The text to find. Empty ends the search.</param>
    /// <param name="options">How to compare, or null for <see cref="SearchOptions.Default"/>.</param>
    /// <returns>The number of matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <remarks>
    /// Deliberately does not move the selection. A user typing into a find box wants to see where
    /// the matches are; scrolling away on every keystroke, before they have finished the word,
    /// is the behaviour people turn off.
    /// </remarks>
    public int Find(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        _query = query;
        _searchOptions = options ?? SearchOptions.Default;
        _current = -1;

        // Forces the research: the document has not changed, but the question has.
        _matchesFor = null;

        Research();

        return _matches.Length;
    }

    /// <summary>Ends the search and takes the highlights away.</summary>
    public void ClearFind()
    {
        if (_query.Length == 0 && _matches.IsEmpty)
        {
            return;
        }

        _query = string.Empty;
        _matchesFor = null;

        Publish([], -1);
    }

    /// <summary>Selects the next match after the caret, wrapping at the end.</summary>
    /// <returns>Whether there was one.</returns>
    public bool FindNext() => GoTo(DocumentSearch.NextFrom(_matches, SearchAnchor(forward: true)));

    /// <summary>Selects the previous match before the caret, wrapping at the start.</summary>
    /// <returns>Whether there was one.</returns>
    public bool FindPrevious() =>
        GoTo(DocumentSearch.PreviousFrom(_matches, SearchAnchor(forward: false)));

    /// <summary>Replaces the match the user is on, then moves to the next.</summary>
    /// <param name="replacement">The text to put there.</param>
    /// <returns>Whether anything was replaced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is null.</exception>
    /// <remarks>
    /// Replaces the current match rather than the selection, and steps forward afterwards, so
    /// that holding Replace walks the document exactly as pressing Find Next would.
    /// </remarks>
    public bool ReplaceCurrent(string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (_current < 0 || _current >= _matches.Length)
        {
            return false;
        }

        var match = _matches[_current];
        var transaction = _state.Transaction().As(TransactionKind.Structure);

        Put(transaction, match, replacement);

        transaction
            .SetSelection(TextSelection.Cursor(match.From + replacement.Length))
            .SetStoredMarks(null);

        if (!transaction.Failures.IsEmpty || !Apply(transaction))
        {
            return false;
        }

        // Research has already run off the state change; the caret sits just past what was
        // written, so this lands on the first match after it.
        GoTo(DocumentSearch.NextFrom(_matches, _state.Selection.To));

        return true;
    }

    /// <summary>Replaces every match.</summary>
    /// <param name="replacement">The text to put in each one.</param>
    /// <returns>How many were replaced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is null.</exception>
    /// <remarks>
    /// One transaction, so one undo takes the whole thing back, and applied back to front so that
    /// an earlier replacement of a different length cannot move a later match.
    /// </remarks>
    public int ReplaceAll(string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (_matches.IsEmpty)
        {
            return 0;
        }

        var count = _matches.Length;
        var transaction = _state.Transaction().As(TransactionKind.Structure);

        for (var i = count - 1; i >= 0; i--)
        {
            Put(transaction, _matches[i], replacement);
        }

        if (!transaction.Failures.IsEmpty || !Apply(transaction))
        {
            return 0;
        }

        return count;
    }

    /// <summary>Writes <paramref name="replacement"/> over <paramref name="match"/>.</summary>
    /// <remarks>
    /// An empty replacement needs no special case: a slice of empty inline content is a valid
    /// replacement and deletes the range, which was verified rather than assumed.
    /// </remarks>
    private static void Put(Transaction transaction, SearchMatch match, string replacement) =>
        transaction.Replace(
            match.From, match.To, Slice.OfInline(InlineContent.FromText(replacement)));

    /// <summary>Brings the match list back into step with the document.</summary>
    /// <remarks>
    /// Called from <c>OnStateChanged</c>, so an edit, an undo and a redo all keep the highlights
    /// honest without any of them having to know a search is open. Guarded on the document's
    /// identity, so moving the caret — by far the commonest state change, and the one this is
    /// reached through when stepping between matches — costs a reference comparison. An empty
    /// query needs no guard of its own: <see cref="DocumentSearch.Find"/> answers it without
    /// walking anything.
    /// </remarks>
    private void Research()
    {
        if (ReferenceEquals(_matchesFor, _state.Doc))
        {
            return;
        }

        _matchesFor = _state.Doc;

        // The match the user was on cannot be identified in the new document. Its positions have
        // moved by an amount only the transaction's mapping knows, and comparing by value silently
        // works for an edit after it and silently fails for one before it — which is worse than
        // not working at all, because it looks right in testing. Find Next re-establishes it, and
        // the paths that care (ReplaceCurrent, ReplaceAll) step explicitly rather than relying on
        // it surviving.
        Publish(DocumentSearch.Find(_state.Doc, _query, _searchOptions), -1);
    }

    /// <summary>The single place the match state changes.</summary>
    /// <remarks>
    /// Every notification and the repaint hang off this, so a new caller cannot leave a find bar
    /// showing a stale count by forgetting one of them.
    /// </remarks>
    private void Publish(ImmutableArray<SearchMatch> matches, int current)
    {
        var wasCount = _matches.Length;
        var wasCurrent = _current;

        _matches = matches;
        _current = current;

        RaisePropertyChanged(MatchCountProperty, wasCount, matches.Length);
        RaisePropertyChanged(CurrentMatchProperty, wasCurrent, current);

        InvalidateVisual();
    }

    /// <summary>Where stepping should start from.</summary>
    /// <remarks>
    /// The far end of the selection in the direction of travel, so that Find Next on a selected
    /// match moves off it rather than finding it again, and Find Previous does the mirror.
    /// </remarks>
    private int SearchAnchor(bool forward) =>
        forward ? _state.Selection.To : _state.Selection.From;

    private bool GoTo(int index)
    {
        if (index < 0 || index >= _matches.Length)
        {
            Publish(_matches, -1);

            return false;
        }

        var match = _matches[index];

        Publish(_matches, index);

        // Through the selection, so the match is scrolled into view, the caret lands on it and
        // the formatting toolbar sees it, all by the paths that already exist. The document does
        // not change, so Research leaves the current match alone.
        MoveTo(match.From, extend: false);
        MoveTo(match.To, extend: true);

        return true;
    }

    private void RenderFindHighlights(DrawingContext context)
    {
        if (_matches.IsEmpty || FindHighlightBrush is not { } brush)
        {
            return;
        }

        for (var i = 0; i < _matches.Length; i++)
        {
            if (i == _current)
            {
                continue;
            }

            var match = _matches[i];

            EnumerateRange(match.From, match.To, rect => context.FillRectangle(brush, rect));
        }
    }

    /// <summary>Paints the match the user is on, over the selection rather than under it.</summary>
    /// <remarks>
    /// Stepping to a match selects it, so the current match is always selected. Painting it
    /// underneath, as the other matches are painted, leaves the selection tint mixed into it and
    /// the result is a muddy colour that reads as neither — measured by rendering it. Being the
    /// current match is the more specific fact, so it wins.
    /// </remarks>
    private void RenderCurrentFindHighlight(DrawingContext context)
    {
        if (_current < 0
            || _current >= _matches.Length
            || CurrentFindHighlightBrush is not { } brush)
        {
            return;
        }

        var match = _matches[_current];

        EnumerateRange(match.From, match.To, rect => context.FillRectangle(brush, rect));
    }
}
