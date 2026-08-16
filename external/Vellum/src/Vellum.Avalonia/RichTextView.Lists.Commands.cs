using System.Collections.Immutable;

namespace Vellum.Avalonia;

/// <summary>
/// Commands that make, unmake and re-shape lists.
/// </summary>
/// <remarks>
/// <para>
/// Lists are the one structure the editor changes by rebuilding rather than by attribute. A
/// bullet is not a property of a paragraph; it is a paragraph wrapped in an item wrapped in a
/// list, and turning bullets on is genuine tree surgery. Every command here therefore replaces a
/// whole run of sibling blocks with a rebuilt run, in one step, so one press is one undo.
/// </para>
/// <para>
/// Because a replace collapses every position inside the range it replaced, none of these can
/// leave the selection to the position map — it would drag the caret to the edge of the list.
/// Instead each rebuild keeps the caret's paragraph <em>by reference</em>, and
/// <see cref="Relocate"/> finds where that same paragraph ended up. That is exact regardless of
/// how much nesting the rebuild added or removed, which hand-computed offsets were not.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>The kind of list the selection sits in, or null when it sits in none.</summary>
    public ListKind? ListKindAt => EnclosingList()?.List.Kind;

    /// <summary>Whether the selection could be nested one level deeper into its list.</summary>
    public bool CanNestListItem => EnclosingList() is { First: > 0 };

    /// <summary>Turns a list of the given kind on or off over the selection.</summary>
    /// <param name="kind">The kind of list to toggle.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// In a list of the same kind this removes it; in a list of the other kind it re-types it;
    /// otherwise it wraps. Only the items the selection touches are affected, so toggling in the
    /// middle of a five-item list splits it into three, which is what the user asked for even
    /// though it is not what they said.
    /// </remarks>
    public bool ToggleList(ListKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of list.");
        }

        return EnclosingList() is { } list ? Retype(list, kind) : Wrap(kind);
    }

    /// <summary>Nests the selected list items one level deeper.</summary>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// An item becomes a child of the item before it, which is why the first item of a list
    /// cannot be nested: there is nothing for it to become a child of. That is a real constraint
    /// of the shape rather than a rule imposed on top of it — an editor that let you indent the
    /// first bullet would have to invent an empty item to hold it.
    /// </remarks>
    public bool NestListItem()
    {
        if (EnclosingList() is not { First: > 0 } context)
        {
            return false;
        }

        var list = context.List;
        var moving = list.Items[context.First..(context.Last + 1)];
        var host = list.Items[context.First - 1];

        // Joining the previous item's own trailing list, when it has one, is what stops a
        // column of separately-nested items from appearing where one nested list belongs.
        var nested = host.Blocks is [.., ListNode trailing] && trailing.Kind == list.Kind
            ? new ListItemNode(
                [.. host.Blocks[..^1], new ListNode([.. trailing.Items, .. moving], trailing.Kind, trailing.Start)])
            : new ListItemNode([.. host.Blocks, new ListNode(moving, list.Kind)]);

        return ReplaceList(
            context,
            [.. list.Items[..(context.First - 1)], nested, .. list.Items[(context.Last + 1)..]]);
    }

    /// <summary>Lifts the selected list items one level out.</summary>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// From a nested list the items become items of the list outside it. From a top-level list
    /// they stop being list items at all, which is the same thing <see cref="ToggleList"/> does
    /// and deliberately so — there is only one place left to go.
    /// </remarks>
    public bool LiftListItem()
    {
        if (EnclosingList() is not { } context)
        {
            return false;
        }

        return context.Outer is null ? Retype(context, context.List.Kind) : LiftIntoOuter(context);
    }

    /// <summary>Lifts items out of a nested list into the list that contains it.</summary>
    /// <remarks>
    /// Whatever followed the lifted items stays nested, now under the last of them. Dropping it
    /// where it was would put content the user never selected above content they did.
    /// </remarks>
    private bool LiftIntoOuter(ListContext context)
    {
        var (list, outer, item, index) = (context.List, context.Outer!, context.Item!, context.ItemIndex);
        var head = list.Items[..context.First];
        var moving = list.Items[(context.First)..(context.Last + 1)].ToList();
        var tail = list.Items[(context.Last + 1)..];

        if (!tail.IsEmpty)
        {
            moving[^1] = new ListItemNode(
                [.. moving[^1].Blocks, new ListNode(tail, list.Kind, list.Start + context.Last + 1)]);
        }

        // The host keeps everything except the items that left. If that empties it — an item
        // holding nothing but the nested list — it goes, rather than lingering as a bullet with
        // no content, which the schema forbids anyway.
        var kept = head.IsEmpty
            ? [.. item.Blocks[..context.ListIndex], .. item.Blocks[(context.ListIndex + 1)..]]
            : item.Blocks.SetItem(context.ListIndex, new ListNode(head, list.Kind, list.Start));

        var items = kept.IsEmpty
            ? [.. outer.Items[..index], .. moving, .. outer.Items[(index + 1)..]]
            : (ImmutableArray<ListItemNode>)
                [.. outer.Items[..index], new ListItemNode(kept), .. moving, .. outer.Items[(index + 1)..]];

        return Rebuild(context.OuterBefore, context.OuterAfter, [new ListNode(items, outer.Kind, outer.Start)]);
    }

    /// <summary>Re-types the touched items, or unwraps them when they are already that kind.</summary>
    private bool Retype(ListContext context, ListKind kind)
    {
        var list = context.List;
        var head = list.Items[..context.First];
        var tail = list.Items[(context.Last + 1)..];
        var blocks = ImmutableArray.CreateBuilder<Node>();

        if (!head.IsEmpty)
        {
            blocks.Add(new ListNode(head, list.Kind, list.Start));
        }

        if (list.Kind == kind)
        {
            foreach (var item in list.Items[context.First..(context.Last + 1)])
            {
                blocks.AddRange(item.Blocks);
            }
        }
        else
        {
            blocks.Add(new ListNode(list.Items[context.First..(context.Last + 1)], kind));
        }

        if (!tail.IsEmpty)
        {
            // A number picks up where the head left off. Restarting at one would renumber items
            // the user did not touch.
            blocks.Add(new ListNode(tail, list.Kind, list.Start + context.Last + 1));
        }

        return Rebuild(context.Before, context.After, blocks.ToImmutable());
    }

    /// <summary>Wraps the blocks the selection touches in a list.</summary>
    private bool Wrap(ListKind kind)
    {
        if (SelectedRange() is not { } range)
        {
            return false;
        }

        var (from, to) = (range.From, range.To);
        var items = ImmutableArray.CreateBuilder<ListItemNode>();
        var start = 1;

        // A list that grew out of the one above it is one list, not two stacked. Left alone the
        // second would restart its numbering, which is visibly wrong rather than merely untidy.
        if (range.First > 0 && range.Parent.Children[range.First - 1] is ListNode above
            && above.Kind == kind)
        {
            items.AddRange(above.Items);
            start = above.Start;
            from -= above.NodeSize;
        }

        for (var i = range.First; i <= range.Last; i++)
        {
            switch (range.Parent.Children[i])
            {
                // A list of the other kind inside the range is absorbed rather than nested, so
                // selecting a mixed run and pressing bullets gives one bulleted list.
                case ListNode inner:
                    items.AddRange(inner.Items);
                    break;

                case BlockNode block:
                    items.Add(new ListItemNode([block]));
                    break;

                default:
                    return false;
            }
        }

        if (range.Last + 1 < range.Parent.Children.Count
            && range.Parent.Children[range.Last + 1] is ListNode below && below.Kind == kind)
        {
            items.AddRange(below.Items);
            to += below.NodeSize;
        }

        return items.Count > 0 && Rebuild(from, to, [new ListNode(items.ToImmutable(), kind, start)]);
    }

    /// <summary>Replaces a whole list with one rebuilt from the given items.</summary>
    private bool ReplaceList(ListContext context, ImmutableArray<ListItemNode> items) =>
        Rebuild(
            context.Before,
            context.After,
            [new ListNode(items, context.List.Kind, context.List.Start)]);

    /// <summary>
    /// Swaps a run of blocks for a rebuilt one and puts the selection back where it belongs.
    /// </summary>
    private bool Rebuild(int from, int to, ImmutableArray<Node> blocks)
    {
        var selection = _state.Selection;
        var transaction = _state.Transaction()
            .As(TransactionKind.Structure)
            .Replace(from, to, new Slice(blocks, 0, 0));

        var anchor = Relocate(blocks, from, selection.Anchor);
        var head = Relocate(blocks, from, selection.Head);

        if (anchor is not null && head is not null)
        {
            transaction.SetSelection(new TextSelection(anchor.Value, head.Value));
        }

        _goalX = null;

        return Apply(transaction);
    }

    /// <summary>
    /// Where a position ends up once its paragraph has been moved into <paramref name="blocks"/>.
    /// </summary>
    /// <param name="blocks">The rebuilt content.</param>
    /// <param name="at">Where the rebuilt content starts in the document.</param>
    /// <param name="pos">The position to relocate.</param>
    /// <returns>The new position, or null if the position was not in text that survived.</returns>
    private int? Relocate(ImmutableArray<Node> blocks, int at, int pos)
    {
        var resolved = _state.Doc.Resolve(pos);

        if (resolved.Paragraph is not { } paragraph)
        {
            return null;
        }

        var offset = ContentStart(blocks, paragraph);

        return offset < 0 ? null : at + offset + resolved.ParentOffset;
    }

    /// <summary>
    /// Where a node's content begins, relative to the start of a run of siblings, or -1.
    /// </summary>
    /// <remarks>
    /// By reference, not by value: two paragraphs reading the same words are equal, and finding
    /// the wrong one would put the caret in the wrong bullet.
    /// </remarks>
    private static int ContentStart(IReadOnlyList<Node> siblings, Node target)
    {
        var offset = 0;

        foreach (var node in siblings)
        {
            if (ReferenceEquals(node, target))
            {
                return offset + 1;
            }

            var inner = ContentStart(node.Children, target);

            if (inner >= 0)
            {
                return offset + 1 + inner;
            }

            offset += node.NodeSize;
        }

        return -1;
    }

    /// <summary>Whether the caret is alone in a list item that holds one empty paragraph.</summary>
    private bool InEmptyListItem()
    {
        if (!_state.Selection.IsEmpty)
        {
            return false;
        }

        var at = _state.Doc.Resolve(_state.Selection.From);

        return at.Depth >= 2
            && at.Parent is ParagraphNode { ContentSize: 0 }
            && at.NodeAt(at.Depth - 1) is ListItemNode { Blocks.Length: 1 };
    }

    /// <summary>The innermost list the whole selection sits in, with the items it touches.</summary>
    private ListContext? EnclosingList()
    {
        var doc = _state.Doc;
        var selection = _state.Selection;

        if (selection is CellSelection)
        {
            return null;
        }

        var from = doc.Resolve(selection.From);
        var to = doc.Resolve(selection.To);

        for (var depth = from.SharedDepth(to); depth >= 1; depth--)
        {
            if (from.NodeAt(depth) is not ListNode list)
            {
                continue;
            }

            var first = Math.Min(from.IndexAt(depth), list.Items.Length - 1);
            var last = Math.Clamp(
                to.Depth > depth ? to.IndexAt(depth) : to.IndexAt(depth) - 1,
                first,
                list.Items.Length - 1);

            // A list two levels inside another is a nested list; one inside a cell or the
            // document is not, however deep the surrounding structure happens to be.
            var nested = depth >= 3
                && from.NodeAt(depth - 1) is ListItemNode
                && from.NodeAt(depth - 2) is ListNode;

            return new ListContext(
                list,
                first,
                last,
                from.Before(depth),
                from.After(depth),
                nested ? (ListNode)from.NodeAt(depth - 2) : null,
                nested ? (ListItemNode)from.NodeAt(depth - 1) : null,
                nested ? from.IndexAt(depth - 2) : 0,
                nested ? from.IndexAt(depth - 1) : 0,
                nested ? from.Before(depth - 2) : 0,
                nested ? from.After(depth - 2) : 0);
        }

        return null;
    }

    /// <summary>The run of sibling blocks the selection touches, with its position range.</summary>
    private BlockRange? SelectedRange()
    {
        var doc = _state.Doc;
        var selection = _state.Selection;

        // A rectangle's bounds are the row positions either side of it, so the "run of sibling
        // blocks" it touches is a run of cells. Wrapping cells in a list is not what was asked
        // for, and it is only the schema that stops it happening.
        if (selection is CellSelection)
        {
            return null;
        }

        var from = doc.Resolve(selection.From);
        var to = doc.Resolve(selection.To);
        var depth = from.SharedDepth(to);

        // Two positions in one paragraph share the paragraph, which holds text rather than
        // blocks. The blocks in question are its own, one level out.
        if (from.NodeAt(depth) is ParagraphNode)
        {
            depth--;
        }

        var first = from.IndexAt(depth);
        var last = to.Depth > depth ? to.IndexAt(depth) : to.IndexAt(depth) - 1;

        if (last < first || depth < 0)
        {
            return null;
        }

        return new BlockRange(
            from.NodeAt(depth),
            first,
            last,
            from.Depth > depth ? from.Before(depth + 1) : from.Pos,
            to.Depth > depth ? to.After(depth + 1) : to.Pos);
    }

    /// <summary>A run of sibling blocks, and the positions that bracket it.</summary>
    private readonly record struct BlockRange(Node Parent, int First, int Last, int From, int To);

    /// <summary>
    /// The list a selection is in, the items it touches, and the list outside it if there is one.
    /// </summary>
    private readonly record struct ListContext(
        ListNode List,
        int First,
        int Last,
        int Before,
        int After,
        ListNode? Outer,
        ListItemNode? Item,
        int ItemIndex,
        int ListIndex,
        int OuterBefore,
        int OuterAfter);
}
