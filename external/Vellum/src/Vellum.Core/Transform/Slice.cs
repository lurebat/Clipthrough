using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// A piece of document, with its edges marked as open or closed.
/// </summary>
/// <remarks>
/// <para>
/// A slice is what a replacement inserts and what a copy produces. The difficulty it exists to
/// solve is that a cut through a document rarely lands on node boundaries: selecting from the
/// middle of one paragraph to the middle of the next yields two paragraph fragments that, when
/// pasted back, must merge into their neighbours rather than arrive as two new paragraphs.
/// </para>
/// <para>
/// <see cref="OpenStart"/> and <see cref="OpenEnd"/> record how many levels of nesting at each
/// edge are fragments rather than whole nodes. Text alone is a single paragraph open at both
/// ends; two complete paragraphs are closed at both. A paragraph split is the same slice as
/// typing, only with two empty paragraphs instead of one non-empty one.
/// </para>
/// </remarks>
public sealed record Slice
{
    /// <summary>Creates a slice.</summary>
    /// <param name="content">The nodes, outermost level only.</param>
    /// <param name="openStart">How many levels of the leading edge are fragments.</param>
    /// <param name="openEnd">How many levels of the trailing edge are fragments.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An open depth is negative, or the open depths claim more of the content than it has.
    /// </exception>
    public Slice(IEnumerable<Node> content, int openStart, int openEnd)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(openStart);
        ArgumentOutOfRangeException.ThrowIfNegative(openEnd);

        Content = content.ToImmutableArray();

        if (Content.Contains(null!))
        {
            throw new ArgumentException("Slice content must not contain nulls.", nameof(content));
        }

        var raw = 0;

        foreach (var node in Content)
        {
            raw += node.NodeSize;
        }

        // An open depth is a claim about the shape of the content: openStart of two says the
        // first node is a fragment and so is its first child. If that child is not there, the
        // claim is unmeetable, and the damage surfaces far away - the size computed here no
        // longer matches what joining the slice into the document actually produces, so a step's
        // map disagrees with the document it just made and every position after it is wrong.
        if (openStart > AvailableDepth(Content, first: true))
        {
            throw new ArgumentOutOfRangeException(
                nameof(openStart),
                "The content is not nested deeply enough to be open that far at its start.");
        }

        if (openEnd > AvailableDepth(Content, first: false))
        {
            throw new ArgumentOutOfRangeException(
                nameof(openEnd),
                "The content is not nested deeply enough to be open that far at its end.");
        }

        Size = raw - openStart - openEnd;

        ArgumentOutOfRangeException.ThrowIfNegative(Size, nameof(openStart));

        OpenStart = openStart;
        OpenEnd = openEnd;
    }

    /// <summary>How many levels of nesting an edge of some content actually has to give.</summary>
    /// <param name="content">The nodes at this level.</param>
    /// <param name="first">Whether to follow the leading edge rather than the trailing one.</param>
    private static int AvailableDepth(IReadOnlyList<Node> content, bool first)
    {
        var depth = 0;

        while (content.Count > 0)
        {
            depth++;
            content = (first ? content[0] : content[^1]).Children;
        }

        return depth;
    }

    /// <summary>A slice holding nothing, which is what a pure deletion inserts.</summary>
    public static Slice Empty { get; } = new([], 0, 0);

    /// <summary>
    /// A slice that splits whatever paragraph it is inserted into, which is what Enter does.
    /// </summary>
    public static Slice ParagraphSplit { get; } =
        new([ParagraphNode.Empty, ParagraphNode.Empty], 1, 1);

    /// <summary>The nodes, outermost level only.</summary>
    public ImmutableArray<Node> Content { get; }

    /// <summary>How many levels of the leading edge are fragments rather than whole nodes.</summary>
    public int OpenStart { get; }

    /// <summary>How many levels of the trailing edge are fragments rather than whole nodes.</summary>
    public int OpenEnd { get; }

    /// <summary>
    /// How many positions inserting this slice adds to a document.
    /// </summary>
    /// <remarks>
    /// Each open edge is a boundary token that gets merged away rather than inserted, so it
    /// does not count. This is what lets a step predict its own position map without applying
    /// itself first.
    /// </remarks>
    public int Size { get; }

    /// <summary>Whether this slice inserts nothing at all.</summary>
    public bool IsEmpty => Content.IsEmpty;

    /// <summary>A slice of inline content, to be merged into the paragraph it lands in.</summary>
    /// <param name="content">The inline content.</param>
    public static Slice OfInline(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new Slice([new ParagraphNode(content)], 1, 1);
    }

    /// <summary>A slice of plain text, to be merged into the paragraph it lands in.</summary>
    /// <param name="text">The text.</param>
    public static Slice OfText(string text) => OfInline(InlineContent.FromText(text));

    /// <summary>A slice of plain text whose newlines become block boundaries.</summary>
    /// <param name="text">The text. Each <c>\n</c> starts a new block.</param>
    /// <param name="kind">The kind to give the blocks this creates.</param>
    /// <returns>The slice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="DocumentTextMap"/>'s projection, which turns a block boundary
    /// into a newline on the way out. Without this the way back in is not symmetric:
    /// <see cref="OfText"/> puts the whole string in one block, so replacing a selection with text
    /// that has newlines in it — pretty-printed JSON, sorted lines, rewrapped prose — yields one
    /// run-on paragraph containing newline characters that nothing will ever render as breaks.
    /// </para>
    /// <para>
    /// <b>The first and last blocks merge with what they land between</b>, which is what the open
    /// ends mean and why <paramref name="kind"/> only reaches the blocks in between. So replacing
    /// part of a heading with three lines leaves the heading a heading and adds two blocks after
    /// it, rather than turning the heading into whatever this slice says.
    /// </para>
    /// <para>
    /// Only <c>\n</c> divides. A <c>\r</c> stays as text, exactly as it does when read back out, so
    /// a document holding CRLF still round-trips through the projection unchanged. Text arriving
    /// from somewhere that uses CRLF and not meaning it should be normalised by the caller, which
    /// is a decision this cannot make on its behalf.
    /// </para>
    /// </remarks>
    public static Slice OfLines(string text, ParagraphKind kind = ParagraphKind.Body)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n');

        return lines.Length == 1
            ? OfText(text)
            : new Slice(
                [.. lines.Select(line => new ParagraphNode(InlineContent.FromText(line), kind))],
                1,
                1);
    }

    /// <summary>A slice of whole blocks, to be inserted between existing ones.</summary>
    /// <param name="blocks">The blocks.</param>
    public static Slice OfBlocks(params BlockNode[] blocks) => new(blocks, 0, 0);

    /// <summary>
    /// Extracts the content between two positions, such that replacing that range with the
    /// result leaves the document unchanged.
    /// </summary>
    /// <param name="doc">The document to cut from.</param>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the document or inverted.</exception>
    public static Slice Cut(DocumentNode doc, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);

        if (from == to)
        {
            return Empty;
        }

        var start = doc.Resolve(from);
        var end = doc.Resolve(to);
        var shared = start.SharedDepth(end);

        // Both ends inside one paragraph is the common case - a word, a sentence - and it is
        // the one case where the shared node is not a container. Wrapping the fragment in its
        // own paragraph, open at both ends, keeps every slice a list of nodes.
        if (start.NodeAt(shared) is ParagraphNode paragraph)
        {
            var text = paragraph.Content.Substring(
                start.ParentOffset,
                end.ParentOffset - start.ParentOffset);

            return new Slice([paragraph.WithContent(text)], 1, 1);
        }

        var node = start.NodeAt(shared);
        var pieces = ImmutableArray.CreateBuilder<Node>();
        var first = start.IndexAt(shared);
        var last = end.IndexAt(shared);

        if (start.Depth > shared)
        {
            pieces.Add(TreeSurgery.CutAfter(start, shared + 1));
            first++;
        }

        for (var i = first; i < last; i++)
        {
            pieces.Add(node.Children[i]);
        }

        if (end.Depth > shared)
        {
            pieces.Add(TreeSurgery.CutBefore(end, shared + 1));
        }

        return new Slice(pieces.ToImmutable(), start.Depth - shared, end.Depth - shared);
    }

    /// <inheritdoc/>
    public bool Equals(Slice? other) =>
        other is not null
        && OpenStart == other.OpenStart
        && OpenEnd == other.OpenEnd
        && Content.SequenceEqual(other.Content);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(OpenStart);
        hash.Add(OpenEnd);

        foreach (var node in Content)
        {
            hash.Add(node);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"slice({string.Join(", ", Content)}, open {OpenStart}/{OpenEnd}, size {Size})";
}
