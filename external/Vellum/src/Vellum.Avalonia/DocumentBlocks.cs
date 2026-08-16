using System.Collections.Immutable;

namespace Vellum.Avalonia;

/// <summary>How a list item is marked.</summary>
/// <param name="Kind">Whether the item is numbered.</param>
/// <param name="Ordinal">The item's number, honouring <see cref="ListNode.Start"/>.</param>
public readonly record struct ListMarker(ListKind Kind, int Ordinal);

/// <summary>
/// One block as the view layer sees it: a leaf block together with everything the document
/// tree knew about it that its own node does not.
/// </summary>
/// <remarks>
/// A block view lays out and draws one block, and it should not have to walk the tree to find
/// out that it is the third item of a nested numbered list. The walk happens once, here, and
/// what it learned travels with the block.
/// </remarks>
/// <param name="Node">The block itself.</param>
/// <param name="Start">
/// The document position of the block's first inner position — the position a caret at the very
/// start of the block occupies. For a leaf block, which has no inside, this is the position
/// the block itself occupies.
/// </param>
/// <param name="Depth">
/// How deeply the block is nested in lists. Zero for a top-level block, one for a block in a
/// top-level list item, and so on.
/// </param>
/// <param name="Marker">
/// The marker to draw beside the block, or null. Only the first block of a list item carries
/// one, so a multi-paragraph item is marked once rather than once per paragraph.
/// </param>
public readonly record struct BlockSlot(BlockNode Node, int Start, int Depth, ListMarker? Marker)
{
    /// <summary>The number of positions inside the block.</summary>
    public int ContentSize => Node.ContentSize;

    /// <summary>The document position just past the block's content.</summary>
    public int End => Start + Node.ContentSize;
}

/// <summary>
/// Flattens a document tree into the sequence of blocks that actually get laid out.
/// </summary>
/// <remarks>
/// <para>
/// The document is a tree, but what is drawn is a vertical run of paragraphs and rules. Lists
/// and list items are structure, not layout: they contribute no box of their own, only an
/// indent and a marker on the blocks inside them. Flattening once and handing the view layer a
/// list is what keeps every geometry question — which block is at this pixel, which blocks does
/// this selection touch — a search over an array rather than a walk of a tree.
/// </para>
/// <para>
/// A table is the one block that is not a flat run: its cells hold blocks side by side. It is
/// emitted as a single slot and its own view lays its cells out, so this walk stops at the
/// table rather than descending into it. That keeps the flat array intact — the slot spans
/// every position inside the table, so a caret anywhere in a cell still resolves to exactly one
/// block — while the structure that cannot be flattened stays behind the one view that can
/// express it.
/// </para>
/// </remarks>
public static class DocumentBlocks
{
    /// <summary>The blocks of a document, in the order they are drawn.</summary>
    /// <param name="doc">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    public static ImmutableArray<BlockSlot> Flatten(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var slots = ImmutableArray.CreateBuilder<BlockSlot>(doc.Blocks.Length);

        // A document's content starts at position 0, so the first block's boundary is there.
        Walk(doc.Blocks, 0, 0, null, slots);

        return slots.ToImmutable();
    }

    /// <summary>
    /// Every block a caret can be inside, in document order, however deeply nested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the same question as <see cref="Flatten(DocumentNode)"/>, which asks what gets drawn. A table is
    /// drawn as one block but holds many paragraphs a caret can be in, and the two answers
    /// diverge exactly there. Backspace at the start of a block needs the block before it in
    /// <em>this</em> order — the paragraph above it inside the same cell, say — because that is
    /// the join the user is asking for.
    /// </para>
    /// <para>
    /// Whether the join is legal is not decided here. It is decided by the step, which refuses
    /// to splice two cells or two list items into one, so every way of asking for that
    /// corruption is refused in one place rather than in each caller.
    /// </para>
    /// </remarks>
    /// <param name="doc">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    public static ImmutableArray<BlockSlot> Leaves(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var slots = ImmutableArray.CreateBuilder<BlockSlot>(doc.Blocks.Length);

        Descend(doc.Blocks, 0, slots);

        return slots.ToImmutable();
    }

    /// <summary>The blocks of a container, in the order they are drawn.</summary>
    /// <remarks>
    /// Lists are flattened into their paragraphs, each carrying the depth and marker that make it
    /// look like a list item. Nothing else draws a list, so a container whose blocks reach a view
    /// unflattened has no way to draw one at all.
    /// </remarks>
    /// <param name="blocks">The container's block content.</param>
    /// <param name="start">The position its content starts at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public static ImmutableArray<BlockSlot> Flatten(IReadOnlyList<BlockNode> blocks, int start)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var slots = ImmutableArray.CreateBuilder<BlockSlot>(blocks.Count);

        Walk(blocks, start, 0, null, slots);

        return slots.ToImmutable();
    }

    private static void Descend(
        IReadOnlyList<Node> nodes, int start, ImmutableArray<BlockSlot>.Builder slots)
    {
        var pos = start;

        foreach (var node in nodes)
        {
            // A block with block children is structure, not content: it is descended through.
            // Anything else is somewhere a caret can be, including a paragraph, whose children
            // are empty because its content is inline rather than block.
            if (node.Children.Count > 0 && node.Children[0] is BlockNode)
            {
                Descend(node.Children, pos + 1, slots);
            }
            else if (node is BlockNode block)
            {
                slots.Add(new BlockSlot(block, block.IsLeaf ? pos : pos + 1, 0, null));
            }

            pos += node.NodeSize;
        }
    }

    private static void Walk(
        IEnumerable<BlockNode> blocks,
        int start,
        int depth,
        ListMarker? marker,
        ImmutableArray<BlockSlot>.Builder slots)
    {
        var pos = start;

        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListNode list:
                    WalkList(list, pos + 1, depth, slots);
                    break;

                case ListItemNode item:
                    // Reached only through WalkList, which supplies the marker.
                    Walk(item.Blocks, pos + 1, depth, marker, slots);
                    marker = null;
                    break;

                case TableNode:
                default:
                    // A leaf block has no inside, so its content start is the position it
                    // occupies rather than one past its opening boundary. Getting this wrong
                    // would put every later block one position out. A table comes through here
                    // whole — its own view lays its cells out.
                    slots.Add(new BlockSlot(block, block.IsLeaf ? pos : pos + 1, depth, marker));
                    marker = null;
                    break;
            }

            pos += block.NodeSize;
        }
    }

    private static void WalkList(
        ListNode list,
        int start,
        int depth,
        ImmutableArray<BlockSlot>.Builder slots)
    {
        var pos = start;
        var ordinal = list.Start;

        foreach (var item in list.Items)
        {
            var marker = new ListMarker(list.Kind, ordinal);

            // The marker belongs to the item's first block only. An item holding two paragraphs
            // is one bullet with two paragraphs under it, not two bullets.
            Walk(item.Blocks, pos + 1, depth + 1, marker, slots);

            pos += item.NodeSize;
            ordinal++;
        }
    }
}
