using System.Collections.Immutable;
using Vellum;

namespace Vellum.Avalonia;

/// <summary>
/// What the editing commands do when the selection is a rectangle of table cells rather than a
/// range of text.
/// </summary>
/// <remarks>
/// <para>
/// A rectangle is <em>not</em> a range, and the difference is destructive rather than cosmetic.
/// <see cref="Selection.From"/> and <see cref="Selection.To"/> only bound a
/// <see cref="CellSelection"/>: selecting the left column of a two column table gives a range that
/// runs straight through the right one. Any command that deletes, cuts or formats that range
/// touches cells the user did not select — and deleting it outright removes whole cells from their
/// rows. So every command either asks <see cref="CellSelection.Cells"/> which cells it may touch,
/// or refuses.
/// </para>
/// <para>
/// The commands that refuse are the ones a rectangle cannot express: there is no sensible place to
/// put a typed character, a pasted slice or a paragraph split when the selection is four cells, and
/// no single block for a heading, a list or an indent to apply to. Refusing reads as a key that did
/// nothing, which is honest; the alternative is a table quietly losing a cell.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>The selection as a rectangle of cells, or null when it is a range.</summary>
    private CellSelection? Rectangle => _state.Selection as CellSelection;

    /// <summary>The text position extending a rectangle is measured from.</summary>
    /// <remarks>
    /// The position the drag began at — unless the rectangle did not come from a drag. An
    /// application can set one on <see cref="State"/> directly, and then the remembered position
    /// is whatever some earlier drag left behind, or nothing at all. Extending from that reaches
    /// out of the table and selects everything between it and the pointer, so a rectangle whose
    /// remembered origin is not inside its own anchor cell measures from that cell instead.
    /// </remarks>
    /// <param name="rectangle">The current selection.</param>
    private int CellOrigin(CellSelection rectangle) =>
        InCell(_state.Doc, _cellOrigin, rectangle.AnchorCell)
            ? _cellOrigin
            : NearestTextPosition(rectangle.AnchorCell + 1, forward: true);

    /// <summary>Whether a position is inside the cell that begins at <paramref name="cellPos"/>.</summary>
    private static bool InCell(DocumentNode doc, int pos, int cellPos)
    {
        if (pos < 0 || pos > doc.ContentSize)
        {
            return false;
        }

        var at = doc.Resolve(pos);

        for (var depth = at.Depth; depth >= 1; depth--)
        {
            if (at.NodeAt(depth) is TableCellNode && at.Before(depth) == cellPos)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The inner range of a cell — everything between its two boundary positions.</summary>
    /// <param name="doc">The document to read.</param>
    /// <param name="cellPos">The position directly before the cell.</param>
    private static (int From, int To)? CellContent(DocumentNode doc, int cellPos) =>
        doc.Resolve(cellPos).NodeAfter is TableCellNode cell
            ? (cellPos + 1, cellPos + 1 + cell.ContentSize)
            : null;

    /// <summary>Empties every cell of a rectangle, leaving the table itself alone.</summary>
    /// <remarks>
    /// <para>
    /// Each cell keeps one empty paragraph, because a cell with no blocks at all is not a legal
    /// cell and because a caret has to have somewhere to land afterwards.
    /// </para>
    /// <para>
    /// The cells are cleared from the last to the first. Emptying one shortens the document after
    /// it and leaves everything before it where it was, so working backwards keeps every position
    /// still to come valid; working forwards would need each one mapped through the steps already
    /// added, which is the same answer by a longer route with more to get wrong.
    /// </para>
    /// </remarks>
    /// <param name="rectangle">The selected cells.</param>
    /// <returns>Whether anything changed.</returns>
    private bool ClearCells(CellSelection rectangle)
    {
        var doc = _state.Doc;
        var cells = rectangle.Cells(doc);

        if (cells.IsEmpty)
        {
            return false;
        }

        var transaction = _state.Transaction().As(TransactionKind.Delete);
        var blank = ParagraphNode.FromText(string.Empty);
        var cleared = false;

        foreach (var pos in cells.OrderDescending())
        {
            if (doc.Resolve(pos).NodeAfter is not TableCellNode cell)
            {
                continue;
            }

            // A cell that is already one empty paragraph has nothing to clear. Skipping it is
            // what makes Delete on an empty rectangle report that it did nothing, rather than
            // pushing an undo entry that restores a document identical to the current one.
            if (cell.Blocks is [var only] && only.Equals(blank))
            {
                continue;
            }

            transaction.Replace(pos + 1, pos + 1 + cell.ContentSize, Slice.OfBlocks(blank));
            cleared = true;
        }

        if (!cleared)
        {
            return false;
        }

        // Into the first cell of the rectangle, one past its paragraph's own boundary. Nothing
        // before it was touched, so the position it had in the original document still holds.
        transaction
            .SetSelection(TextSelection.Cursor(cells.Min() + 2))
            .SetStoredMarks(null);

        _goalX = null;

        return Apply(transaction);
    }

    /// <summary>Applies formatting to the whole content of every cell of a rectangle.</summary>
    /// <param name="rectangle">The selected cells.</param>
    /// <param name="value">Supplies the new values for the named fields.</param>
    /// <param name="fields">Which fields to take from <paramref name="value"/>.</param>
    /// <returns>Whether anything changed.</returns>
    private bool MarkCells(CellSelection rectangle, MarkSet value, MarkFields fields)
    {
        var doc = _state.Doc;
        var cells = rectangle.Cells(doc);

        if (cells.IsEmpty)
        {
            return false;
        }

        var transaction = _state.Transaction().As(TransactionKind.Format);

        // No ordering care needed, unlike clearing: a mark step changes no node's size, so no
        // position moves and the rectangle is still the same rectangle afterwards.
        foreach (var pos in cells)
        {
            if (CellContent(doc, pos) is not { } range)
            {
                continue;
            }

            transaction.Step(new AddMarkStep(range.From, range.To, value, fields));
        }

        // Only the stored marks. The selection needs no restating: a mark step moves nothing, so
        // the rectangle maps through the transaction as the very same rectangle.
        transaction.SetStoredMarks(null);

        return Apply(transaction);
    }

    /// <summary>The formatting every character of every selected cell shares.</summary>
    /// <param name="rectangle">The selected cells.</param>
    private MarkSet? CellMarks(CellSelection rectangle)
    {
        var doc = _state.Doc;

        MarkSet? common = null;

        foreach (var pos in rectangle.Cells(doc))
        {
            if (CellContent(doc, pos) is not { } range
                || MarksAcross(range.From, range.To) is not { } marks)
            {
                continue;
            }

            common = common is { } far ? Intersect(far, marks) : marks;
        }

        return common;
    }

    /// <summary>The selected cells as a table in a document of their own.</summary>
    /// <remarks>
    /// Rebuilt rather than cut out of the document, because cutting the range between the first
    /// and last cell sweeps up every cell between them — for a column selection, the entire table.
    /// The rebuilt table is as wide and as tall as the rectangle, and since a rectangle is closed
    /// under the merged cells it touches, no cell's span can overhang it.
    /// </remarks>
    /// <param name="rectangle">The selected cells.</param>
    internal DocumentNode CellDocument(CellSelection rectangle)
    {
        var doc = _state.Doc;

        if (TableGrid.ForCell(doc, rectangle.AnchorCell) is not { } grid)
        {
            return new DocumentNode([]);
        }

        var chosen = rectangle.Cells(doc).ToHashSet();
        var rows = ImmutableArray.CreateBuilder<TableRowNode>();

        for (var r = 0; r < grid.Rows; r++)
        {
            var cells = ImmutableArray.CreateBuilder<TableCellNode>();

            for (var c = 0; c < grid.Columns; c++)
            {
                // A merged cell fills every slot it covers, so it is taken only from the slot it
                // starts in. Taking it from each would copy it once per row and column it spans.
                if (grid.At(r, c) is not { } slot
                    || slot.Top != r || slot.Left != c
                    || !chosen.Contains(slot.Pos)
                    || doc.Resolve(slot.Pos).NodeAfter is not TableCellNode cell)
                {
                    continue;
                }

                cells.Add(cell);
            }

            if (cells.Count > 0)
            {
                rows.Add(new TableRowNode(cells.ToImmutable()));
            }
        }

        return rows.Count > 0
            ? new DocumentNode([new TableNode(rows.ToImmutable())])
            : new DocumentNode([]);
    }
}
