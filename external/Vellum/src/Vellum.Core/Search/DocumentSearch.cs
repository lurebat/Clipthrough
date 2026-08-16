using System.Collections.Immutable;

namespace Vellum;

/// <summary>A range of the document matching a search.</summary>
/// <param name="From">The position the match starts at.</param>
/// <param name="To">The position the match ends at.</param>
public readonly record struct SearchMatch(int From, int To)
{
    /// <summary>The number of positions the match covers.</summary>
    public int Length => To - From;
}

/// <summary>How a search compares.</summary>
public sealed record SearchOptions
{
    /// <summary>The default: case-insensitive, anywhere in a word.</summary>
    public static SearchOptions Default { get; } = new();

    /// <summary>Whether an upper-case letter in the query has to match an upper-case letter.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Whether a match has to be bounded by non-word characters on both sides.</summary>
    public bool WholeWord { get; init; }
}

/// <summary>
/// Finding text in a document.
/// </summary>
/// <remarks>
/// <para>
/// Searches each paragraph's raw text, not <see cref="DocumentText"/>. That looks like the wrong
/// choice and is the only correct one: the raw text has exactly one code unit per document
/// position, so an offset into it <em>is</em> a position, whereas the plain-text projection
/// expands an embed into its alt text and every position after it would be wrong by the
/// difference. The cost is that an embed is one <see cref="InlineContent.Placeholder"/> to a
/// search rather than its description, which is right anyway — nobody expects to find an image by
/// searching for its alt text.
/// </para>
/// <para>
/// A match never spans two blocks. Positions between blocks are boundary tokens, not text, so a
/// range covering them is not a range a replacement could be applied to.
/// </para>
/// <para>
/// Comparison is ordinal. Culture-sensitive comparison can match a run of a <em>different length</em>
/// than the query — a soft hyphen has no collation weight, so <c>"co\u00ADoperate"</c> matches the
/// nine-code-unit query <c>"cooperate"</c> — and this search hands its results straight to a
/// replacement, so a match whose length is not its own length corrupts the document. Ordinal
/// ignore-case folds per code unit and cannot change a length.
/// </para>
/// </remarks>
public static class DocumentSearch
{
    /// <summary>Every match in the document, in document order.</summary>
    /// <param name="doc">The document to search.</param>
    /// <param name="query">The text to find. Empty matches nothing.</param>
    /// <param name="options">How to compare, or null for <see cref="SearchOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> or <paramref name="query"/> is null.</exception>
    public static ImmutableArray<SearchMatch> Find(
        DocumentNode doc, string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Length == 0)
        {
            return [];
        }

        var settings = options ?? SearchOptions.Default;
        var matches = ImmutableArray.CreateBuilder<SearchMatch>();

        Walk(doc.Blocks, 0, query, settings, matches);

        return matches.ToImmutable();
    }

    /// <summary>The first match at or after <paramref name="from"/>, wrapping if asked.</summary>
    /// <param name="matches">The matches, in document order.</param>
    /// <param name="from">The position to search from.</param>
    /// <param name="wrap">Whether to return the first match when none follows.</param>
    /// <returns>The index into <paramref name="matches"/>, or -1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="matches"/> is null.</exception>
    public static int NextFrom(IReadOnlyList<SearchMatch> matches, int from, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(matches);

        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].From >= from)
            {
                return i;
            }
        }

        return wrap && matches.Count > 0 ? 0 : -1;
    }

    /// <summary>The last match ending at or before <paramref name="from"/>, wrapping if asked.</summary>
    /// <param name="matches">The matches, in document order.</param>
    /// <param name="from">The position to search back from.</param>
    /// <param name="wrap">Whether to return the last match when none precedes.</param>
    /// <returns>The index into <paramref name="matches"/>, or -1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="matches"/> is null.</exception>
    public static int PreviousFrom(IReadOnlyList<SearchMatch> matches, int from, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(matches);

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i].To <= from)
            {
                return i;
            }
        }

        return wrap && matches.Count > 0 ? matches.Count - 1 : -1;
    }

    private static void Walk(
        IReadOnlyList<Node> nodes,
        int start,
        string query,
        SearchOptions options,
        ImmutableArray<SearchMatch>.Builder matches)
    {
        var pos = start;

        foreach (var node in nodes)
        {
            if (node is ParagraphNode paragraph)
            {
                // pos is the paragraph's open token; its text begins one past it.
                FindIn(paragraph.Content.Text, pos + 1, query, options, matches);
            }
            else if (node.Children.Count > 0)
            {
                Walk(node.Children, pos + 1, query, options, matches);
            }

            pos += node.NodeSize;
        }
    }

    private static void FindIn(
        string text,
        int start,
        string query,
        SearchOptions options,
        ImmutableArray<SearchMatch>.Builder matches)
    {
        var comparison = options.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var at = 0;

        while (at <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, at, comparison);

            if (found < 0)
            {
                return;
            }

            if (!options.WholeWord || IsWholeWord(text, found, query.Length))
            {
                matches.Add(new SearchMatch(start + found, start + found + query.Length));

                // Non-overlapping: "aa" in "aaa" is one match, not two. Overlapping matches cannot
                // both be replaced, and a replace-all that skipped every second match would be
                // stranger than one that found fewer.
                at = found + query.Length;
            }
            else
            {
                // Only past the rejected start, or a whole-word search for "the" in "the the"
                // would lose the second word to the rejection of the first.
                at = found + 1;
            }
        }
    }

    /// <remarks>
    /// A word character is a letter, a digit or an underscore, which is what every editor's
    /// "whole word" means and is deliberately wider than <see cref="char.IsLetterOrDigit(char)"/>.
    /// </remarks>
    private static bool IsWholeWord(string text, int at, int length) =>
        (at == 0 || !IsWordCharacter(text[at - 1])) &&
        (at + length == text.Length || !IsWordCharacter(text[at + length]));

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
