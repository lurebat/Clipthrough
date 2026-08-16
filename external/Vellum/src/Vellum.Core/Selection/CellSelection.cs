using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// Where one cell sits in a table once merges are taken into account.
/// </summary>
/// <param name="Pos">The position directly before the cell.</param>
/// <param name="Top">The first row it occupies.</param>
/// <param name="Left">The first column it occupies.</param>
/// <param name="Bottom">One past the last row it occupies.</param>
/// <param name="Right">One past the last column it occupies.</param>
public readonly record struct TableSlot(int Pos, int Top, int Left, int Bottom, int Right);

/// <summary>
/// A table laid out as a grid, so that merged cells can be reasoned about by row and column
/// rather than by index within a row.
/// </summary>
/// <remarks>
/// The document stores a table as rows of cells, which is the right shape for editing and the
/// wrong shape for every geometric question. A cell spanning two rows appears in one row's child
/// list and simply is not present in the next, so the third cell of row two may be the fourth
/// column. Selecting a rectangle, or asking what is above a cell, needs the grid, and building it
/// on demand is cheaper than maintaining it in the tree, where every edit would have to keep it
/// consistent.
/// </remarks>
public sealed class TableGrid
{
    private readonly int[,] _slots;
    private readonly ImmutableDictionary<int, TableSlot> _cells;

    private TableGrid(
        int tablePos,
        int rows,
        int columns,
        int[,] slots,
        ImmutableDictionary<int, TableSlot> cells)
    {
        TablePos = tablePos;
        Rows = rows;
        Columns = columns;
        _slots = slots;
        _cells = cells;
    }

    /// <summary>The position directly before the table.</summary>
    public int TablePos { get; }

    /// <summary>How many rows the table has.</summary>
    public int Rows { get; }

    /// <summary>How many columns it takes to hold every cell.</summary>
    public int Columns { get; }

    /// <summary>Every cell, by the position directly before it.</summary>
    public ImmutableDictionary<int, TableSlot> Cells => _cells;

    /// <summary>
    /// Lays out the table that contains a given cell position.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="cellPos">A position directly before a table cell.</param>
    /// <returns>The grid, or null if that position is not directly before a cell.</returns>
    public static TableGrid? ForCell(DocumentNode doc, int cellPos)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (cellPos < 0 || cellPos > doc.ContentSize)
        {
            return null;
        }

        var at = doc.Resolve(cellPos);

        if (at.NodeAfter is not TableCellNode
            || at.Depth < 2
            || at.NodeAt(at.Depth) is not TableRowNode
            || at.NodeAt(at.Depth - 1) is not TableNode table)
        {
            return null;
        }

        return Build(at.Before(at.Depth - 1), table);
    }

    /// <summary>
    /// Lays out a table.
    /// </summary>
    /// <param name="tablePos">The position directly before the table.</param>
    /// <param name="table">The table.</param>
    public static TableGrid Build(int tablePos, TableNode table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var cells = ImmutableDictionary.CreateBuilder<int, TableSlot>();
        var placed = new List<TableSlot>();

        // The geometry is worked out once, in one place, so the grid a selection sees and the
        // grid the schema validates can never disagree about where a cell sits.
        var placements = TableGeometry.Place(table, out var width);
        var rowPos = tablePos + 1;
        var next = 0;

        for (var row = 0; row < table.Rows.Length; row++)
        {
            var cellPos = rowPos + 1;

            foreach (var cell in table.Rows[row].Cells)
            {
                var placement = placements[next++];
                var slot = new TableSlot(
                    cellPos,
                    placement.Top,
                    placement.Left,
                    placement.Bottom,
                    placement.Right);

                placed.Add(slot);
                cells.Add(cellPos, slot);
                cellPos += cell.NodeSize;
            }

            rowPos += table.Rows[row].NodeSize;
        }

        var height = table.Rows.Length;
        var slots = new int[height, Math.Max(width, 1)];

        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < slots.GetLength(1); c++)
            {
                slots[r, c] = -1;
            }
        }

        foreach (var slot in placed)
        {
            for (var r = slot.Top; r < Math.Min(slot.Bottom, height); r++)
            {
                for (var c = slot.Left; c < slot.Right; c++)
                {
                    slots[r, c] = slot.Pos;
                }
            }
        }

        return new TableGrid(tablePos, height, width, slots, cells.ToImmutable());
    }

    /// <summary>The cell occupying a slot, or null if the table is ragged and it is empty.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    public TableSlot? At(int row, int column)
    {
        if (row < 0 || column < 0 || row >= Rows || column >= _slots.GetLength(1))
        {
            return null;
        }

        var pos = _slots[row, column];

        return pos < 0 ? null : _cells[pos];
    }

    /// <summary>
    /// Every cell in the smallest rectangle containing two cells, in document order.
    /// </summary>
    /// <param name="anchorCell">The position before one corner cell.</param>
    /// <param name="headCell">The position before the other.</param>
    /// <remarks>
    /// The rectangle grows until it is closed under the cells it touches. Dragging from a plain
    /// cell to one merged across two columns has to take the whole merged cell, and taking it
    /// widens the rectangle, which may then take another - so this iterates rather than computing
    /// a bounding box once.
    /// </remarks>
    public ImmutableArray<int> Rectangle(int anchorCell, int headCell)
    {
        if (!_cells.TryGetValue(anchorCell, out var anchor)
            || !_cells.TryGetValue(headCell, out var head))
        {
            return [];
        }

        var top = Math.Min(anchor.Top, head.Top);
        var left = Math.Min(anchor.Left, head.Left);
        var bottom = Math.Max(anchor.Bottom, head.Bottom);
        var right = Math.Max(anchor.Right, head.Right);

        bool grew;

        do
        {
            grew = false;

            for (var r = top; r < bottom; r++)
            {
                for (var c = left; c < right; c++)
                {
                    if (At(r, c) is not { } slot)
                    {
                        continue;
                    }

                    if (slot.Top < top || slot.Left < left || slot.Bottom > bottom
                        || slot.Right > right)
                    {
                        top = Math.Min(top, slot.Top);
                        left = Math.Min(left, slot.Left);
                        bottom = Math.Max(bottom, slot.Bottom);
                        right = Math.Max(right, slot.Right);
                        grew = true;
                    }
                }
            }
        }
        while (grew);

        var found = new SortedSet<int>();

        for (var r = top; r < bottom; r++)
        {
            for (var c = left; c < right; c++)
            {
                if (At(r, c) is { } slot)
                {
                    found.Add(slot.Pos);
                }
            }
        }

        return [.. found];
    }
}

/// <summary>
/// A rectangular region of table cells.
/// </summary>
/// <param name="AnchorCell">The position directly before the cell the drag started in.</param>
/// <param name="HeadCell">The position directly before the cell it is currently over.</param>
/// <remarks>
/// <para>
/// The two ends are cell positions rather than the corners of a range, because a rectangle in a
/// table is not a contiguous range of the document: selecting the left column of a two-column
/// table skips over the right one in every row. Commands that act on the selection must therefore
/// ask <see cref="Cells"/> what it covers rather than reading <c>From</c> and <c>To</c>, which
/// only bound it.
/// </para>
/// <para>
/// It exists this early because retro-fitting a third selection kind means revisiting every
/// command that ever assumed two, and there will be more commands later than there are now.
/// </para>
/// </remarks>
public sealed record CellSelection(int AnchorCell, int HeadCell)
    : Selection(AnchorCell, HeadCell)
{
    /// <summary>
    /// A cell selection between two cells, or null if they are not two cells of one table.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="anchorCell">The position before one cell.</param>
    /// <param name="headCell">The position before the other.</param>
    public static CellSelection? Create(DocumentNode doc, int anchorCell, int headCell)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var grid = TableGrid.ForCell(doc, anchorCell);

        return grid is not null
            && grid.Cells.ContainsKey(headCell)
            && TableGrid.ForCell(doc, headCell)?.TablePos == grid.TablePos
            ? new CellSelection(anchorCell, headCell)
            : null;
    }

    /// <summary>
    /// A cell selection spanning the two cells containing two arbitrary positions, or null if
    /// they do not sit in two different cells of one table.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="anchorPos">Any position inside the cell the drag started in.</param>
    /// <param name="headPos">Any position inside the cell it is currently over.</param>
    /// <remarks>
    /// <para>
    /// This is what a pointer drag needs: it knows two positions, not two cells. Both ends are
    /// resolved to the chain of cells that contain them, innermost first, and the deepest table
    /// they have in common decides the level the rectangle is drawn at. Dragging from inside a
    /// nested table out to a sibling cell of the outer one therefore selects two cells of the
    /// <em>outer</em> table, which is the only rectangle that can contain both ends — matching at
    /// the innermost level instead would compare cells of two unrelated grids.
    /// </para>
    /// <para>
    /// Two positions in the same cell return null rather than a one-cell rectangle, because a
    /// drag within a cell is ordinary text selection and promoting it would make it impossible
    /// to select a word.
    /// </para>
    /// </remarks>
    public static CellSelection? Across(DocumentNode doc, int anchorPos, int headPos)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (anchorPos < 0 || anchorPos > doc.ContentSize
            || headPos < 0 || headPos > doc.ContentSize)
        {
            return null;
        }

        var anchors = CellChain(doc, anchorPos);

        if (anchors.Count == 0)
        {
            return null;
        }

        var heads = CellChain(doc, headPos);

        foreach (var (cell, table) in anchors)
        {
            foreach (var (otherCell, otherTable) in heads)
            {
                if (otherTable != table)
                {
                    continue;
                }

                return cell == otherCell ? null : Create(doc, cell, otherCell);
            }
        }

        return null;
    }

    /// <summary>
    /// Every table cell containing a position, as the position before the cell paired with the
    /// position before its table, innermost first.
    /// </summary>
    private static List<(int Cell, int Table)> CellChain(DocumentNode doc, int pos)
    {
        var at = doc.Resolve(pos);
        var chain = new List<(int, int)>();

        // A cell is always a row's child and a row always a table's, so a cell at depth d puts
        // its table at d - 2. Stopping at 3 keeps that lookup at depth 1 or deeper, which is
        // what Before requires: there is nothing outside the document to address.
        for (var depth = at.Depth; depth >= 3; depth--)
        {
            if (at.NodeAt(depth) is TableCellNode
                && at.NodeAt(depth - 1) is TableRowNode
                && at.NodeAt(depth - 2) is TableNode)
            {
                chain.Add((at.Before(depth), at.Before(depth - 2)));
            }
        }

        return chain;
    }

    /// <summary>
    /// The positions before each selected cell, in document order.
    /// </summary>
    /// <param name="doc">The document this selection was measured against.</param>
    public ImmutableArray<int> Cells(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        return TableGrid.ForCell(doc, AnchorCell)?.Rectangle(AnchorCell, HeadCell) ?? [];
    }

    /// <inheritdoc/>
    public override Selection Map(DocumentNode doc, Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);

        var anchor = mapping.MapWithResult(AnchorCell, Assoc.After);
        var head = mapping.MapWithResult(HeadCell, Assoc.After);

        if (!anchor.Deleted && !head.Deleted
            && Create(doc, anchor.Pos, head.Pos) is { } survived)
        {
            return survived;
        }

        // The table, or one of the two cells, is gone. Degrading to a caret is the only honest
        // answer: there is no smaller rectangle to fall back to that the reader asked for. The
        // caret goes where the head ended up, which is a position in the document that now
        // exists - the original head is a position in the document that no longer does.
        return Near(doc, head.Pos);
    }
}
