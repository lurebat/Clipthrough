using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// The root of the document tree.
/// </summary>
/// <remarks>
/// Positions in the document run from 0 to <see cref="ContentSize"/>. The document's own
/// boundary tokens are never addressable, because there is nothing outside it to address
/// them from.
/// </remarks>
public sealed class DocumentNode : Node
{
    private readonly ImmutableArray<BlockNode> _blocks;
    private readonly int _contentSize;

    /// <summary>Creates a document.</summary>
    /// <param name="blocks">The top-level blocks.</param>
    public DocumentNode(IEnumerable<BlockNode> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        _blocks = blocks.ToImmutableArray();

        if (_blocks.Contains(null!))
        {
            throw new ArgumentException("Document blocks must not be null.", nameof(blocks));
        }

        _contentSize = SumOfNodeSizes(_blocks);
    }

    /// <summary>
    /// A document holding one empty paragraph.
    /// </summary>
    /// <remarks>
    /// Not a document holding nothing: the schema requires at least one block, so that the
    /// caret always has somewhere to be.
    /// </remarks>
    public static DocumentNode Empty { get; } = new([ParagraphNode.Empty]);

    /// <summary>Creates a document of plain paragraphs.</summary>
    /// <param name="paragraphs">The paragraph texts.</param>
    public static DocumentNode FromParagraphs(params string[] paragraphs) =>
        new(paragraphs.Select(ParagraphNode.FromText));

    /// <summary>The top-level blocks.</summary>
    public ImmutableArray<BlockNode> Blocks => _blocks;

    /// <summary>Works out the ancestor chain for a position.</summary>
    /// <param name="pos">A position in <c>[0, ContentSize]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the document.</exception>
    public ResolvedPos Resolve(int pos) => ResolvedPos.Resolve(this, pos);

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _blocks;

    /// <inheritdoc/>
    public override string TypeName => "doc";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new DocumentNode(RequireAll<BlockNode>(children, nameof(children)));

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other) =>
        ChildrenEqual(_blocks, ((DocumentNode)other)._blocks);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        HashChildren(ref hash, _blocks);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"doc[{_blocks.Length} blocks, size {_contentSize}]";
}
