using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Vellum;

/// <summary>
/// The inline level of the document: flat UTF-16 text with sorted mark spans and embeds.
/// </summary>
/// <remarks>
/// <para>
/// Flat rather than a run tree because typing is the hot path. A tree of run objects would
/// allocate and re-splice on every keystroke, and shaping caches key off contiguous text.
/// </para>
/// <para>
/// <b>Mark spans are canonical.</b> For a given text and a given offset-to-mark function
/// there is exactly one legal <see cref="Marks"/> array: sorted, non-overlapping, no empty
/// ranges, no spans carrying <see cref="MarkSet.Empty"/>, and no two adjacent spans with
/// equal values. This is not tidiness. The undo property test compares documents exactly, so
/// two documents that render identically must also compare equal, and that only holds if the
/// representation is unique.
/// </para>
/// </remarks>
public sealed class InlineContent : IEquatable<InlineContent>
{
    /// <summary>
    /// U+FFFC OBJECT REPLACEMENT CHARACTER, standing in for one embed.
    /// </summary>
    /// <remarks>
    /// One placeholder character per embed means an embed occupies exactly one position, so
    /// every offset calculation in the document stays uniform.
    /// </remarks>
    public const char Placeholder = '\uFFFC';

    private readonly ImmutableArray<int> _placeholderOffsets;

    private InlineContent(
        string text,
        ImmutableArray<ValueSpan<MarkSet>> marks,
        ImmutableArray<InlineEmbed> embeds,
        ImmutableArray<int> placeholderOffsets)
    {
        Text = text;
        Marks = marks;
        Embeds = embeds;
        _placeholderOffsets = placeholderOffsets;
    }

    /// <summary>Content with no text, no marks and no embeds.</summary>
    public static InlineContent Empty { get; } = new(
        string.Empty,
        ImmutableArray<ValueSpan<MarkSet>>.Empty,
        ImmutableArray<InlineEmbed>.Empty,
        ImmutableArray<int>.Empty);

    /// <summary>The text, with one <see cref="Placeholder"/> per embed.</summary>
    public string Text { get; }

    /// <summary>Formatting spans, in canonical form.</summary>
    public ImmutableArray<ValueSpan<MarkSet>> Marks { get; }

    /// <summary>The embeds, in the order their placeholders appear in <see cref="Text"/>.</summary>
    public ImmutableArray<InlineEmbed> Embeds { get; }

    /// <summary>The length of <see cref="Text"/> in UTF-16 code units.</summary>
    public int Length => Text.Length;

    /// <summary>Whether there is no text at all.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>Creates unformatted content.</summary>
    /// <param name="text">The text. Must contain no <see cref="Placeholder"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> contains a placeholder.</exception>
    public static InlineContent FromText(string text) => FromText(text, MarkSet.Empty);

    /// <summary>Creates content with one mark across all of it.</summary>
    /// <param name="text">The text. Must contain no <see cref="Placeholder"/>.</param>
    /// <param name="mark">The formatting to apply to the whole of it.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> contains a placeholder.</exception>
    public static InlineContent FromText(string text, MarkSet mark)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Contains(Placeholder, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Text containing a placeholder needs matching embeds; use Create.", nameof(text));
        }

        return Create(text, [new ValueSpan<MarkSet>(0, text.Length, mark)], []);
    }

    /// <summary>Creates content from a single embed.</summary>
    /// <param name="embed">The embed.</param>
    /// <param name="mark">Formatting applied to the embed's placeholder.</param>
    public static InlineContent FromEmbed(InlineEmbed embed, MarkSet mark = default)
    {
        ArgumentNullException.ThrowIfNull(embed);

        return Create(Placeholder.ToString(), [new ValueSpan<MarkSet>(0, 1, mark)], [embed]);
    }

    /// <summary>
    /// Creates content, normalizing <paramref name="marks"/> into canonical form.
    /// </summary>
    /// <param name="text">The text, with one <see cref="Placeholder"/> per embed.</param>
    /// <param name="marks">
    /// Formatting spans. May arrive in any order, but must not overlap each other.
    /// </param>
    /// <param name="embeds">
    /// The embeds, ordered to match the placeholders in <paramref name="text"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A span falls outside the text.</exception>
    /// <exception cref="ArgumentException">
    /// Two spans overlap, or the embed count does not match the placeholder count.
    /// </exception>
    public static InlineContent Create(
        string text,
        IEnumerable<ValueSpan<MarkSet>> marks,
        IEnumerable<InlineEmbed> embeds)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(embeds);

        var embedArray = embeds.ToImmutableArray();

        if (embedArray.Contains(null!))
        {
            throw new ArgumentException("Embeds must not be null.", nameof(embeds));
        }

        var placeholders = PlaceholderOffsetsOf(text);

        if (placeholders.Length != embedArray.Length)
        {
            throw new ArgumentException(
                $"Text has {placeholders.Length} placeholder(s) but {embedArray.Length} embed(s) " +
                "were supplied; the two must correspond one-to-one.",
                nameof(embeds));
        }

        RequireWellFormed(text);

        // Normalize before the empty-text shortcut, so spans supplied for text that does not
        // exist are still rejected rather than silently discarded.
        var normalized = Normalize(marks, text.Length);

        RequireWholeCharacters(text, normalized);

        return text.Length == 0
            ? Empty
            : new InlineContent(text, normalized, embedArray, placeholders);
    }

    /// <summary>
    /// Rejects text that is not well-formed UTF-16.
    /// </summary>
    /// <remarks>
    /// An unpaired surrogate is not a character. Admitting one would make
    /// <see cref="IsValidBoundary"/> a promise the type cannot keep, and every consumer that
    /// trusts it - cutting, measuring, shaping - would be working from a false premise. The
    /// model's contract is to fail rather than hold something invalid (architecture §7).
    /// </remarks>
    private static void RequireWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    throw new ArgumentException(
                        $"The text is not well-formed UTF-16: the high surrogate at {i} is not "
                        + "followed by a low surrogate.",
                        nameof(text));
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                throw new ArgumentException(
                    $"The text is not well-formed UTF-16: the low surrogate at {i} is not "
                    + "preceded by a high surrogate.",
                    nameof(text));
            }
        }
    }

    /// <summary>Rejects formatting spans whose edges fall inside a surrogate pair.</summary>
    /// <remarks>
    /// Half a character cannot be bold. Such a span has no meaning to give a renderer, and
    /// silently rounding it would move formatting the caller did not ask to move.
    /// </remarks>
    private static void RequireWholeCharacters(
        string text, ImmutableArray<ValueSpan<MarkSet>> spans)
    {
        foreach (var span in spans)
        {
            foreach (var edge in (int[])[span.Start, span.End])
            {
                if (edge > 0 && edge < text.Length && char.IsLowSurrogate(text[edge]))
                {
                    throw new ArgumentException(
                        $"A formatting span edge at {edge} falls inside a surrogate pair.",
                        nameof(spans));
                }
            }
        }
    }

    /// <summary>The formatting at <paramref name="offset"/>.</summary>
    /// <param name="offset">An offset in <c>[0, Length)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside the text.</exception>
    public MarkSet MarkAt(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, Length);

        var index = IndexOfSpanContaining(offset);

        return index < 0 ? MarkSet.Empty : Marks[index].Value;
    }

    /// <summary>
    /// The formatting a character typed at <paramref name="offset"/> should inherit.
    /// </summary>
    /// <remarks>
    /// Takes the formatting of the character before the caret, which is what every editor
    /// does: typing at the end of a bold word continues bold. At the very start there is no
    /// preceding character, so the following one is used instead.
    /// </remarks>
    /// <param name="offset">An offset in <c>[0, Length]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside the text.</exception>
    public MarkSet MarkForInsertionAt(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);

        if (Length == 0)
        {
            return MarkSet.Empty;
        }

        return offset == 0 ? MarkAt(0) : MarkAt(offset - 1);
    }

    /// <summary>Gets the embed whose placeholder sits at <paramref name="offset"/>.</summary>
    /// <param name="offset">The offset to look at.</param>
    /// <param name="embed">Receives the embed, if there is one.</param>
    /// <returns>Whether an embed was found.</returns>
    public bool TryGetEmbedAt(int offset, [NotNullWhen(true)] out InlineEmbed? embed)
    {
        var index = _placeholderOffsets.BinarySearch(offset);

        if (index < 0)
        {
            embed = null;
            return false;
        }

        embed = Embeds[index];
        return true;
    }

    /// <summary>
    /// Whether <paramref name="offset"/> is a position an edit may start or end at.
    /// </summary>
    /// <remarks>
    /// False in the middle of a surrogate pair. Steps consult this and fail cleanly rather
    /// than producing invalid UTF-16, which is an explicit requirement of the verification
    /// scenarios (architecture §8).
    /// </remarks>
    /// <param name="offset">The offset to test.</param>
    public bool IsValidBoundary(int offset)
    {
        if (offset < 0 || offset > Length)
        {
            return false;
        }

        return offset == 0 || offset == Length || !char.IsLowSurrogate(Text[offset]);
    }

    /// <summary>Extracts a range as new content.</summary>
    /// <param name="start">Where to start.</param>
    /// <param name="length">How many code units to take.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the text.</exception>
    /// <exception cref="ArgumentException">An endpoint splits a surrogate pair.</exception>
    public InlineContent Substring(int start, int length)
    {
        ValidateRange(start, length);

        if (length == 0)
        {
            return Empty;
        }

        if (start == 0 && length == Length)
        {
            return this;
        }

        var end = start + length;

        var marks = ImmutableArray.CreateBuilder<ValueSpan<MarkSet>>();

        foreach (var span in Marks)
        {
            var from = Math.Max(span.Start, start);
            var to = Math.Min(span.End, end);

            if (from < to)
            {
                marks.Add(new ValueSpan<MarkSet>(from - start, to - from, span.Value));
            }
        }

        var embeds = ImmutableArray.CreateBuilder<InlineEmbed>();

        for (var i = 0; i < _placeholderOffsets.Length; i++)
        {
            if (_placeholderOffsets[i] >= start && _placeholderOffsets[i] < end)
            {
                embeds.Add(Embeds[i]);
            }
        }

        return Create(Text.Substring(start, length), marks.ToImmutable(), embeds.ToImmutable());
    }

    /// <summary>Replaces a range with other content.</summary>
    /// <param name="start">Where the replaced range starts.</param>
    /// <param name="length">How many code units it covers.</param>
    /// <param name="replacement">What to put there.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the text.</exception>
    /// <exception cref="ArgumentException">An endpoint splits a surrogate pair.</exception>
    public InlineContent Replace(int start, int length, InlineContent replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRange(start, length);

        var end = start + length;
        var shift = replacement.Length - length;

        var text = string.Concat(
            Text.AsSpan(0, start),
            replacement.Text,
            Text.AsSpan(end));

        var marks = ImmutableArray.CreateBuilder<ValueSpan<MarkSet>>();

        foreach (var span in Marks)
        {
            if (span.Start < start)
            {
                marks.Add(span.WithRange(span.Start, Math.Min(span.End, start) - span.Start));
            }

            if (span.End > end)
            {
                var from = Math.Max(span.Start, end);
                marks.Add(span.WithRange(from + shift, span.End - from));
            }
        }

        foreach (var span in replacement.Marks)
        {
            marks.Add(span.WithRange(span.Start + start, span.Length));
        }

        var embeds = ImmutableArray.CreateBuilder<InlineEmbed>();

        for (var i = 0; i < _placeholderOffsets.Length; i++)
        {
            if (_placeholderOffsets[i] < start)
            {
                embeds.Add(Embeds[i]);
            }
        }

        embeds.AddRange(replacement.Embeds);

        for (var i = 0; i < _placeholderOffsets.Length; i++)
        {
            if (_placeholderOffsets[i] >= end)
            {
                embeds.Add(Embeds[i]);
            }
        }

        return Create(text, marks.ToImmutable(), embeds.ToImmutable());
    }

    /// <summary>Inserts content at an offset.</summary>
    /// <param name="offset">Where to insert.</param>
    /// <param name="content">What to insert.</param>
    public InlineContent Insert(int offset, InlineContent content) => Replace(offset, 0, content);

    /// <summary>Removes a range.</summary>
    /// <param name="start">Where to start.</param>
    /// <param name="length">How many code units to remove.</param>
    public InlineContent Remove(int start, int length) => Replace(start, length, Empty);

    /// <summary>Appends other content.</summary>
    /// <param name="other">What to append.</param>
    public InlineContent Concat(InlineContent other) => Insert(Length, other);

    /// <summary>
    /// Changes the selected fields of the formatting across a range, leaving the rest alone.
    /// </summary>
    /// <param name="start">Where the range starts.</param>
    /// <param name="length">How many code units it covers.</param>
    /// <param name="value">Supplies the new values for the selected fields.</param>
    /// <param name="fields">Which fields to change.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the text.</exception>
    /// <exception cref="ArgumentException">An endpoint splits a surrogate pair.</exception>
    public InlineContent ApplyMarks(int start, int length, MarkSet value, MarkFields fields)
    {
        ValidateRange(start, length);

        if (length == 0 || fields == MarkFields.None)
        {
            return this;
        }

        var end = start + length;
        var marks = ImmutableArray.CreateBuilder<ValueSpan<MarkSet>>();

        // Existing spans outside the range survive untouched; the parts inside get rebuilt
        // below, together with any previously unmarked stretches the range covers.
        foreach (var span in Marks)
        {
            if (span.Start < start)
            {
                marks.Add(span.WithRange(span.Start, Math.Min(span.End, start) - span.Start));
            }

            if (span.End > end)
            {
                var from = Math.Max(span.Start, end);
                marks.Add(span.WithRange(from, span.End - from));
            }
        }

        var cursor = start;

        while (cursor < end)
        {
            var index = IndexOfSpanContaining(cursor);
            var current = index < 0 ? MarkSet.Empty : Marks[index].Value;
            var segmentEnd = index < 0 ? NextSpanStartAfter(cursor, end) : Math.Min(Marks[index].End, end);

            marks.Add(new ValueSpan<MarkSet>(
                cursor, segmentEnd - cursor, current.Apply(value, fields)));

            cursor = segmentEnd;
        }

        var normalized = Normalize(marks, Length);

        // Formatting text that already has that formatting has to give back the very same
        // object. A transaction drops a step whose document came back reference-identical, and
        // that is the only thing standing between "make this bold" on already-bold text and an
        // undo entry that undoes nothing the user can see.
        return normalized.SequenceEqual(Marks)
            ? this
            : new InlineContent(Text, normalized, Embeds, _placeholderOffsets);
    }

    /// <summary>
    /// The text with each embed replaced by its plain-text fallback.
    /// </summary>
    public string ToPlainText()
    {
        if (Embeds.IsEmpty)
        {
            return Text;
        }

        var result = new System.Text.StringBuilder(Text.Length);
        var cursor = 0;

        for (var i = 0; i < _placeholderOffsets.Length; i++)
        {
            result.Append(Text, cursor, _placeholderOffsets[i] - cursor);
            result.Append(Embeds[i].PlainTextFallback);
            cursor = _placeholderOffsets[i] + 1;
        }

        return result.Append(Text, cursor, Text.Length - cursor).ToString();
    }

    /// <inheritdoc/>
    public bool Equals(InlineContent? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Text == other.Text
            && Marks.SequenceEqual(other.Marks)
            && Embeds.SequenceEqual(other.Embeds);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as InlineContent);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Text);
        hash.Add(Marks.Length);

        foreach (var span in Marks)
        {
            hash.Add(span);
        }

        foreach (var embed in Embeds)
        {
            hash.Add(embed);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Marks.IsEmpty ? Text : $"{Text} [{string.Join(", ", Marks)}]";

    private static ImmutableArray<int> PlaceholderOffsetsOf(string text)
    {
        var offsets = ImmutableArray.CreateBuilder<int>();

        for (var i = text.IndexOf(Placeholder);
             i >= 0;
             i = text.IndexOf(Placeholder, i + 1))
        {
            offsets.Add(i);
        }

        return offsets.ToImmutable();
    }

    /// <summary>
    /// Reduces arbitrary non-overlapping spans to the one canonical representation: sorted,
    /// nothing empty, nothing inheriting-only, and no two adjacent equal spans left unmerged.
    /// </summary>
    private static ImmutableArray<ValueSpan<MarkSet>> Normalize(
        IEnumerable<ValueSpan<MarkSet>> marks, int textLength)
    {
        var sorted = new List<ValueSpan<MarkSet>>();

        foreach (var span in marks)
        {
            if (span.End > textLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(marks), span, $"Span extends past the end of {textLength} code units.");
            }

            // An empty span and a span that overrides nothing both describe "no formatting
            // here", and the canonical form spells that as the absence of a span.
            if (!span.IsEmpty && !span.Value.IsEmpty)
            {
                sorted.Add(span);
            }
        }

        if (sorted.Count == 0)
        {
            return ImmutableArray<ValueSpan<MarkSet>>.Empty;
        }

        sorted.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var result = ImmutableArray.CreateBuilder<ValueSpan<MarkSet>>(sorted.Count);
        var pending = sorted[0];

        for (var i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];

            if (next.Start < pending.End)
            {
                throw new ArgumentException(
                    $"Mark spans overlap: {pending} and {next}.", nameof(marks));
            }

            if (next.Start == pending.End && EqualityComparer<MarkSet>.Default.Equals(
                    next.Value, pending.Value))
            {
                pending = pending.WithRange(pending.Start, next.End - pending.Start);
                continue;
            }

            result.Add(pending);
            pending = next;
        }

        result.Add(pending);

        return result.ToImmutable();
    }

    private void ValidateRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, Length);

        if (!IsValidBoundary(start) || !IsValidBoundary(start + length))
        {
            throw new ArgumentException(
                $"Range [{start}..{start + length}) splits a surrogate pair.", nameof(start));
        }
    }

    /// <summary>The index of the span covering <paramref name="offset"/>, or a negative value.</summary>
    private int IndexOfSpanContaining(int offset)
    {
        var low = 0;
        var high = Marks.Length - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var span = Marks[mid];

            if (offset < span.Start)
            {
                high = mid - 1;
            }
            else if (offset >= span.End)
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Where the unmarked stretch starting at <paramref name="offset"/> ends: the next span's
    /// start, or <paramref name="limit"/>.
    /// </summary>
    private int NextSpanStartAfter(int offset, int limit)
    {
        foreach (var span in Marks)
        {
            if (span.Start > offset)
            {
                return Math.Min(span.Start, limit);
            }
        }

        return limit;
    }
}
