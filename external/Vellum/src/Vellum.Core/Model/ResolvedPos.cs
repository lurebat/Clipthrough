using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// A document position with its ancestor chain worked out.
/// </summary>
/// <remarks>
/// <para>
/// A raw position is a single integer, which is what makes selections two integers and makes
/// position mapping possible without walking the tree. Resolving one recovers the context
/// that the integer deliberately throws away: which node contains it, at what depth, and
/// where in that node's content it falls.
/// </para>
/// <para>
/// Depth 0 is the document itself. <see cref="Parent"/> is the node at <see cref="Depth"/> —
/// the innermost node that actually contains the position, which is a paragraph for a
/// position in text and a container for a position between blocks.
/// </para>
/// </remarks>
public sealed class ResolvedPos
{
    private readonly ImmutableArray<Level> _path;

    private ResolvedPos(int pos, ImmutableArray<Level> path)
    {
        Pos = pos;
        _path = path;
    }

    /// <summary>The position this resolves.</summary>
    public int Pos { get; }

    /// <summary>How many levels down the tree the position sits. 0 is the document.</summary>
    public int Depth => _path.Length - 1;

    /// <summary>The innermost node containing the position.</summary>
    public Node Parent => _path[Depth].Node;

    /// <summary>
    /// The offset of the position within <see cref="Parent"/>'s content — a text offset when
    /// the parent is a paragraph, otherwise a position between child nodes.
    /// </summary>
    public int ParentOffset => Pos - _path[Depth].Start;

    /// <summary>
    /// The index within <see cref="Parent"/>'s children at which the position falls. For a
    /// position between children this is where an insertion would go.
    /// </summary>
    public int ParentIndex => _path[Depth].Index;

    /// <summary>Whether the position is inside a paragraph's text.</summary>
    public bool IsInText => Parent is ParagraphNode;

    /// <summary>The paragraph containing the position, or null if it is between blocks.</summary>
    public ParagraphNode? Paragraph => Parent as ParagraphNode;

    /// <summary>
    /// The child immediately before the position, or null at the start of the parent or
    /// inside text.
    /// </summary>
    public Node? NodeBefore =>
        IsInText || ParentIndex == 0 ? null : Parent.Children[ParentIndex - 1];

    /// <summary>
    /// The child immediately after the position, or null at the end of the parent or inside
    /// text.
    /// </summary>
    public Node? NodeAfter =>
        IsInText || ParentIndex >= Parent.Children.Count ? null : Parent.Children[ParentIndex];

    /// <summary>Resolves a position in a document.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="pos">A position in <c>[0, doc.ContentSize]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the document.</exception>
    public static ResolvedPos Resolve(DocumentNode doc, int pos)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfNegative(pos);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pos, doc.ContentSize);

        var path = ImmutableArray.CreateBuilder<Level>();
        Node node = doc;
        var start = 0;

        while (true)
        {
            var offset = pos - start;

            if (node is ParagraphNode)
            {
                // A paragraph's interior is text, so there is nothing further to descend
                // into and the offset is already the text offset.
                path.Add(new Level(node, 0, start));
                break;
            }

            var index = 0;
            var consumed = 0;

            while (index < node.Children.Count)
            {
                var size = node.Children[index].NodeSize;

                if (consumed + size > offset)
                {
                    break;
                }

                consumed += size;
                index++;
            }

            // Landing exactly on a child boundary - or past the last child - means the
            // position belongs to this node, not to any child of it.
            if (index == node.Children.Count || consumed == offset)
            {
                path.Add(new Level(node, index, start));
                break;
            }

            path.Add(new Level(node, index, start));

            // The child's own content begins one position past its opening boundary.
            start += consumed + 1;
            node = node.Children[index];
        }

        return new ResolvedPos(pos, path.ToImmutable());
    }

    /// <summary>The node at a given depth.</summary>
    /// <param name="depth">A depth in <c>[0, Depth]</c>.</param>
    public Node NodeAt(int depth) => _path[CheckDepth(depth)].Node;

    /// <summary>
    /// The index at a given depth — which child of that node the position descends into.
    /// </summary>
    /// <param name="depth">A depth in <c>[0, Depth]</c>.</param>
    public int IndexAt(int depth) => _path[CheckDepth(depth)].Index;

    /// <summary>The position where the content of the node at a given depth starts.</summary>
    /// <param name="depth">A depth in <c>[0, Depth]</c>.</param>
    public int Start(int depth) => _path[CheckDepth(depth)].Start;

    /// <summary>The position where the content of the node at a given depth ends.</summary>
    /// <param name="depth">A depth in <c>[0, Depth]</c>.</param>
    public int End(int depth) => Start(depth) + NodeAt(depth).ContentSize;

    /// <summary>The position immediately before the node at a given depth.</summary>
    /// <param name="depth">A depth in <c>[1, Depth]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="depth"/> is 0 — there is nothing outside the document to address.
    /// </exception>
    public int Before(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        return Start(depth) - 1;
    }

    /// <summary>The position immediately after the node at a given depth.</summary>
    /// <param name="depth">A depth in <c>[1, Depth]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="depth"/> is 0 — there is nothing outside the document to address.
    /// </exception>
    public int After(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        return End(depth) + 1;
    }

    /// <summary>
    /// The deepest depth at which this position and <paramref name="other"/> share an
    /// ancestor.
    /// </summary>
    /// <param name="other">The position to compare against.</param>
    public int SharedDepth(ResolvedPos other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var depth = 0;

        while (depth < Depth
            && depth < other.Depth
            && ReferenceEquals(NodeAt(depth + 1), other.NodeAt(depth + 1))
            && Start(depth + 1) == other.Start(depth + 1))
        {
            depth++;
        }

        return depth;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var trail = string.Join("/", _path.Select(level => $"{level.Node.TypeName}:{level.Index}"));

        return $"{Pos} ({trail} @{ParentOffset})";
    }

    private int CheckDepth(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, Depth);

        return depth;
    }

    private readonly record struct Level(Node Node, int Index, int Start);
}
