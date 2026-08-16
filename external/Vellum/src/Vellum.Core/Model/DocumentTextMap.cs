using System.Collections.Immutable;
using System.Text;

namespace Vellum;

/// <summary>
/// A two-way map between document positions and offsets into the document's text.
/// </summary>
/// <remarks>
/// <para>
/// For code that thinks in plain-text offsets — a transformation applied to a selected range, an
/// external tool handed some text and returning replacement text, an offset stored before an edit
/// and used after it. Such code needs to get back to the document, and the two coordinate systems
/// do not line up: a paragraph costs <c>length + 2</c> positions but contributes
/// <c>length + 1</c> characters, so the gap grows by one for every block and by more for every
/// list or table around them.
/// </para>
/// <para>
/// <b>This is not <see cref="DocumentText"/>, and the difference is the whole point.</b>
/// <see cref="DocumentText"/> expands an embed to its
/// <see cref="InlineEmbed.PlainTextFallback"/> — the right answer for a screen reader, and one
/// that cannot be inverted, since an image with alt text occupies one position and several
/// characters, and one without occupies one position and none at all. Here every embed is one
/// <see cref="InlineContent.Placeholder"/>, so characters and positions stay in step and the map
/// is total in both directions.
/// </para>
/// <para>
/// The map is a snapshot. Edit the document and build a new one; holding an old map against a new
/// document silently returns positions into the document it was built from.
/// </para>
/// </remarks>
public sealed class DocumentTextMap
{
    private readonly ImmutableArray<Span> _spans;

    private DocumentTextMap(string text, ImmutableArray<Span> spans)
    {
        Text = text;
        _spans = spans;
    }

    /// <summary>The document's text, with one <see cref="InlineContent.Placeholder"/> per embed.</summary>
    /// <remarks>
    /// Blocks are separated by a single newline, exactly as <see cref="DocumentText"/> separates
    /// them, so the two agree on everything except embeds.
    /// </remarks>
    public string Text { get; }

    /// <summary>Builds the map for a document.</summary>
    /// <param name="doc">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is <see langword="null"/>.</exception>
    public static DocumentTextMap Of(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var builder = new StringBuilder();
        var spans = ImmutableArray.CreateBuilder<Span>();

        // A paragraph's own position is one before its content, which is what makes the content
        // start `position + 1` rather than `position`.
        Walk(doc.Blocks, start: 0, builder, spans);

        return new DocumentTextMap(builder.ToString(), spans.ToImmutable());
    }

    private static void Walk(
        IEnumerable<Node> nodes, int start, StringBuilder text, ImmutableArray<Span>.Builder spans)
    {
        var position = start;

        foreach (var node in nodes)
        {
            if (node is ParagraphNode paragraph)
            {
                if (spans.Count > 0)
                {
                    text.Append('\n');
                }

                spans.Add(new Span(text.Length, paragraph.Content.Length, position + 1));
                text.Append(paragraph.Content.Text);
            }
            else if (!node.IsLeaf)
            {
                Walk(node.Children, position + 1, text, spans);
            }

            position += node.NodeSize;
        }
    }

    /// <summary>The text offset a document position falls at.</summary>
    /// <param name="position">A position in the document.</param>
    /// <returns>The offset into <see cref="Text"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the document.</exception>
    /// <remarks>
    /// A position between blocks — the gap a caret cannot occupy — resolves to the end of the text
    /// before it, because that is the offset a selection reaching that far has reached.
    /// </remarks>
    public int ToOffset(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        if (_spans.IsEmpty)
        {
            return 0;
        }

        var last = 0;

        foreach (var span in _spans)
        {
            if (position < span.Position)
            {
                return last;
            }

            if (position <= span.Position + span.Length)
            {
                return span.Offset + (position - span.Position);
            }

            last = span.Offset + span.Length;
        }

        return last;
    }

    /// <summary>The document position a text offset falls at.</summary>
    /// <param name="offset">An offset into <see cref="Text"/>.</param>
    /// <returns>The position in the document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside <see cref="Text"/>.</exception>
    public int ToPosition(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Text.Length);

        if (_spans.IsEmpty)
        {
            return 0;
        }

        var last = _spans[0].Position;

        foreach (var span in _spans)
        {
            if (offset < span.Offset)
            {
                return last;
            }

            if (offset <= span.Offset + span.Length)
            {
                return span.Position + (offset - span.Offset);
            }

            last = span.Position + span.Length;
        }

        return last;
    }

    /// <summary>The document range a run of text covers.</summary>
    /// <param name="offset">Where the run starts in <see cref="Text"/>.</param>
    /// <param name="length">How long it is.</param>
    /// <returns>The range, as positions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The run falls outside <see cref="Text"/>.</exception>
    /// <remarks>
    /// <para>
    /// Note the change of convention, which is deliberate and matches what each side expects: text
    /// runs are an offset and a <em>length</em>, document ranges are a <em>pair of positions</em>.
    /// The result therefore goes straight into a <see cref="ReplaceStep"/>, whose two arguments are
    /// also <c>from</c> and <c>to</c>:
    /// </para>
    /// <code>
    /// var (from, to) = map.ToPositions(offset, length);
    /// transaction.Step(new ReplaceStep(from, to, Slice.OfText(replacement)));
    /// </code>
    /// <para>
    /// Formatting outside the range survives, because a replace only touches what it spans. That
    /// is the reason to go through positions at all rather than splicing
    /// <see cref="Text"/> and rebuilding — a splice flattens the whole document to save one word.
    /// </para>
    /// <para>
    /// Two things the range can do that plain text cannot, both worth deciding about rather than
    /// discovering. A range containing an embed cannot be replaced with text that still carries
    /// its placeholder — see <see cref="ContainsEmbeds"/>. And a range spanning a block boundary
    /// replaced by one run of text leaves <em>one</em> block, since that is what replacing the
    /// boundary means; replacing across three paragraphs with <c>"X"</c> yields a single paragraph.
    /// </para>
    /// </remarks>
    public (int From, int To) ToPositions(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return (ToPosition(offset), ToPosition(offset + length));
    }

    /// <summary>The run of text a document range covers.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <returns>The offset and length into <see cref="Text"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="to"/> is before <paramref name="from"/>.</exception>
    public (int Offset, int Length) ToOffsets(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);

        var start = ToOffset(from);

        return (start, ToOffset(to) - start);
    }

    /// <summary>The text a document range covers.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="to"/> is before <paramref name="from"/>.</exception>
    /// <remarks>
    /// An embed inside the range appears as one <see cref="InlineContent.Placeholder"/>. If the
    /// text is going to be transformed and put back, check <see cref="ContainsEmbeds"/> first —
    /// see its remarks for why.
    /// </remarks>
    public string Slice(int from, int to)
    {
        var (offset, length) = ToOffsets(from, to);

        return Text.Substring(offset, length);
    }

    /// <summary>Whether a document range contains an embed.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <returns><see langword="true"/> if any embed falls inside it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="to"/> is before <paramref name="from"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Check this before round-tripping text through anything.</b> A transformation takes text
    /// and returns text, and an embed reaches it as a bare
    /// <see cref="InlineContent.Placeholder"/> standing for something the transformation knows
    /// nothing about — so it comes back still carrying that placeholder, with no embed attached to
    /// it. <see cref="Slice.OfText(string)"/> refuses such text, correctly and loudly, but it does
    /// so from well away from here.
    /// </para>
    /// <para>
    /// The two honest answers are to leave a range with an embed in it alone, or to accept that
    /// replacing it discards the embed. Which one is right belongs to the application, not to this
    /// type — so it is asked rather than assumed.
    /// </para>
    /// </remarks>
    public bool ContainsEmbeds(int from, int to)
    {
        var (offset, length) = ToOffsets(from, to);

        return Text.AsSpan(offset, length).Contains(InlineContent.Placeholder);
    }

    /// <summary>One paragraph's text, and where it sits in both coordinate systems.</summary>
    private readonly record struct Span(int Offset, int Length, int Position);
}
