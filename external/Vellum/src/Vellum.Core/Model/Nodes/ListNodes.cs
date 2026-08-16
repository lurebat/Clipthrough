using System.Collections.Immutable;

namespace Vellum;

/// <summary>Whether a list numbers its items.</summary>
public enum ListKind
{
    /// <summary>Bulleted.</summary>
    Unordered = 0,

    /// <summary>Numbered.</summary>
    Ordered,
}

/// <summary>A bulleted or numbered list.</summary>
public sealed class ListNode : BlockNode
{
    private readonly ImmutableArray<ListItemNode> _items;
    private readonly int _contentSize;

    /// <summary>Creates a list.</summary>
    /// <param name="items">The items.</param>
    /// <param name="kind">Whether the list is numbered.</param>
    /// <param name="start">The number the first item takes, for ordered lists.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative.</exception>
    public ListNode(
        IEnumerable<ListItemNode> items,
        ListKind kind = ListKind.Unordered,
        int start = 1)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        NodeAttr.Require(kind);

        _items = items.ToImmutableArray();

        if (_items.Contains(null!))
        {
            throw new ArgumentException("List items must not be null.", nameof(items));
        }

        Kind = kind;
        Start = start;
        _contentSize = SumOfNodeSizes(_items);
    }

    /// <summary>The items.</summary>
    public ImmutableArray<ListItemNode> Items => _items;

    /// <summary>Whether the list is numbered.</summary>
    public ListKind Kind { get; }

    /// <summary>The number the first item takes, for ordered lists.</summary>
    public int Start { get; }

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _items;

    /// <inheritdoc/>
    public override string TypeName => "list";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new ListNode(RequireAll<ListItemNode>(children, nameof(children)), Kind, Start);

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other)
    {
        var list = (ListNode)other;

        return Kind == list.Kind && Start == list.Start && ChildrenEqual(_items, list._items);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Start);
        HashChildren(ref hash, _items);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}-list[{_items.Length}]";
}

/// <summary>
/// One item in a list, holding block content.
/// </summary>
/// <remarks>
/// Items hold blocks rather than inline content, which is what makes nested lists fall out
/// for free: a nested list is simply another block inside an item.
/// </remarks>
public sealed class ListItemNode : BlockNode
{
    private readonly ImmutableArray<BlockNode> _blocks;
    private readonly int _contentSize;

    /// <summary>Creates a list item.</summary>
    /// <param name="blocks">The block content.</param>
    public ListItemNode(IEnumerable<BlockNode> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        _blocks = blocks.ToImmutableArray();

        if (_blocks.Contains(null!))
        {
            throw new ArgumentException("List item blocks must not be null.", nameof(blocks));
        }

        _contentSize = SumOfNodeSizes(_blocks);
    }

    /// <summary>Creates a list item holding a single paragraph.</summary>
    /// <param name="text">The paragraph text.</param>
    public static ListItemNode FromText(string text) => new([ParagraphNode.FromText(text)]);

    /// <summary>The block content.</summary>
    public ImmutableArray<BlockNode> Blocks => _blocks;

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _blocks;

    /// <inheritdoc/>
    public override string TypeName => "list-item";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new ListItemNode(RequireAll<BlockNode>(children, nameof(children)));

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other) =>
        ChildrenEqual(_blocks, ((ListItemNode)other)._blocks);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        HashChildren(ref hash, _blocks);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"list-item[{_blocks.Length}]";
}
