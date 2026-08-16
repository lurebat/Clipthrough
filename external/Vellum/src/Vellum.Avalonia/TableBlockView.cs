using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>
/// A table, drawn as a grid.
/// </summary>
/// <remarks>
/// <para>
/// The first block view that hosts other block views. Everything else in the document is a
/// vertical run, which is why the presenter can flatten a document into an array and answer
/// every geometry question with a search. A table is the one structure that cannot be
/// flattened — its cells sit side by side — so it owns its children and answers for them.
/// </para>
/// <para>
/// The position arithmetic here mirrors <see cref="DocumentBlocks"/> exactly, because it has
/// to: the document offsets the presenter hands out are computed by that walk, and a table that
/// disagreed by one would put the caret in the wrong cell. Local position 0 is the first
/// position inside the table; each row occupies <c>NodeSize</c> positions starting one past the
/// row's opening boundary, and so on down to the blocks in a cell. The mapping is built once in
/// the constructor as a flat list of child views with their local starts, so hit-testing and
/// caret geometry are a search over that list rather than a second walk that could drift.
/// </para>
/// <para>
/// Column widths follow the table when it states them and are divided evenly when it does not.
/// Even division is not what a browser does — a browser sizes columns to their content — but it
/// is predictable, it never collapses a column to nothing, and content-based sizing needs a
/// min/max width pass over every cell that the block views do not currently expose. Explicit
/// widths, which is what the HTML and RTF importers produce whenever the source stated any, are
/// honoured.
/// </para>
/// </remarks>
public sealed class TableBlockView : BlockView
{
    private const double CellPadding = 6;
    private const double BorderThickness = 1;

    private readonly Func<BlockNode, BlockView> _create;
    private readonly IBrush? _header;
    private readonly IPen _border;

    private TableNode _table;
    private ImmutableArray<Cell> _cells;
    private int _columns;

    private double[] _columnEdges = [];
    private double[] _rowEdges = [];
    private Size _size;

    /// <summary>Creates a view of a table.</summary>
    /// <param name="table">The table.</param>
    /// <param name="create">Makes a view for a block inside a cell.</param>
    /// <param name="border">The brush the grid lines are drawn in.</param>
    /// <param name="header">The brush a header cell is filled with, or null to leave it plain.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="header"/> is null.</exception>
    public TableBlockView(
        TableNode table,
        Func<BlockNode, BlockView> create,
        IBrush border,
        IBrush? header = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(border);

        _table = table;
        _create = create;
        _header = header;
        _border = new Pen(border, BorderThickness);
        _cells = Build(table, create, header, previous: default, out _columns);
    }

    /// <summary>
    /// Brings the view up to date with an edited table, keeping the views of every cell the edit
    /// did not touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilding the view instead costs a fresh <c>TextLayout</c> for every block in every cell,
    /// on every keystroke. Measured on a 50×5 table, that was 18.5 ms per typed character against
    /// a 16.7 ms frame — the table was the only structure in the document that got slower the
    /// bigger it was, because it was the only one <c>Reconcile</c> could not update in place.
    /// </para>
    /// <para>
    /// The document is a persistent tree, so an edit inside one cell leaves every other cell node
    /// reference-identical to the one already on screen. That is what makes the reuse safe and
    /// what makes it cheap: the walk is the same walk, and all it adds is a reference comparison
    /// per block.
    /// </para>
    /// </remarks>
    /// <param name="table">The table as it is now.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public bool Update(TableNode table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (ReferenceEquals(table, _table))
        {
            return false;
        }

        _cells = Build(table, _create, _header, _cells, out _columns);
        _table = table;

        return true;
    }

    /// <summary>
    /// Walks the table into the flat cell list everything else here searches, reusing the views
    /// in <paramref name="previous"/> wherever the edit left a block untouched.
    /// </summary>
    /// <remarks>
    /// The one place the local position arithmetic lives. It has to mirror
    /// <see cref="DocumentBlocks"/> exactly, and a second copy of it for the update path is the
    /// obvious way for the two to drift apart by one and put the caret in the wrong cell.
    /// </remarks>
    private static ImmutableArray<Cell> Build(
        TableNode table,
        Func<BlockNode, BlockView> create,
        IBrush? header,
        ImmutableArray<Cell> previous,
        out int columns)
    {
        var placements = TableGeometry.Place(table, out columns);
        var cells = ImmutableArray.CreateBuilder<Cell>(placements.Length);

        // Walked in exactly the order TableGeometry placed them, which is the order the rows and
        // their cells appear in, so placement i belongs to the i'th cell reached here.
        var index = 0;
        var rowStart = 0;

        foreach (var row in table.Rows)
        {
            // One past the row's opening boundary.
            var cellStart = rowStart + 1;

            foreach (var cell in row.Cells)
            {
                // Cells are matched by position rather than by identity. An edit inside one cell
                // leaves the others alone, so the i'th cell of the new table is the i'th of the
                // old one; a structural change that makes that false only costs a rebuild, which
                // is what would have happened anyway.
                var was = !previous.IsDefault && index < previous.Length ? previous[index] : null;
                var blocks = ImmutableArray.CreateBuilder<Child>(cell.Blocks.Length);

                // Flattened, because a list has no view of its own: it is drawn as its paragraphs
                // wearing a depth and a marker. A cell that handed its blocks straight to create
                // would throw the moment one of them was a list.
                var slots = DocumentBlocks.Flatten(cell.Blocks, cellStart + 1);
                var at = 0;

                foreach (var slot in slots)
                {
                    var before = was is not null && at < was.Blocks.Length ? was.Blocks[at] : null;
                    var view = Reuse(before, slot.Node) ?? create(slot.Node);

                    // A marker is not part of the paragraph node, so a reused view can be drawn
                    // for the same paragraph under a different bullet and must be told each time.
                    if (view is ParagraphView paragraph)
                    {
                        paragraph.SetLead(slot.Depth, slot.Marker);
                    }

                    blocks.Add(new Child(slot.Node, view, slot.Start));
                    at++;
                }

                cells.Add(new Cell(
                    cell,
                    placements[index],
                    cellStart + 1,
                    blocks.ToImmutable(),
                    header));

                cellStart += cell.NodeSize;
                index++;
            }

            rowStart += row.NodeSize;
        }

        return cells.ToImmutable();
    }

    /// <summary>The view already on screen for a block, if it can be kept, or null.</summary>
    /// <remarks>
    /// An unchanged block keeps its view outright. A changed one keeps it only if the view can be
    /// told to update itself, which a paragraph and a nested table can and an image and a rule
    /// cannot — and an image especially must not be rebuilt for nothing, because building one
    /// resolves its source.
    /// </remarks>
    private static BlockView? Reuse(Child? before, BlockNode block) => before switch
    {
        null => null,
        _ when ReferenceEquals(before.Node, block) => before.View,
        { View: ParagraphView view } when block is ParagraphNode paragraph => Updated(view, paragraph),
        { View: TableBlockView view } when block is TableNode nested => Updated(view, nested),
        _ => null,
    };

    private static BlockView Updated(ParagraphView view, ParagraphNode paragraph)
    {
        view.Update(paragraph);

        return view;
    }

    private static BlockView Updated(TableBlockView view, TableNode table)
    {
        view.Update(table);

        return view;
    }

    /// <inheritdoc/>
    public override int ContentSize => _table.ContentSize;

    /// <inheritdoc/>
    public override Size Size => _size;

    /// <summary>How many columns the table turned out to be.</summary>
    /// <remarks>
    /// Not <c>Rows[0].Cells.Length</c>: a row-spanning cell means a row can carry fewer cells
    /// than the table is wide.
    /// </remarks>
    public int Columns => _columns;

    /// <inheritdoc/>
    public override Size Measure(double availableWidth)
    {
        var width = double.IsFinite(availableWidth) && availableWidth > 0
            ? availableWidth
            : 0;

        _columnEdges = ColumnEdges(width);

        var heights = new double[_table.Rows.Length];

        foreach (var cell in _cells)
        {
            var inner = CellWidth(cell.Placement) - (2 * CellPadding);
            var content = 0.0;

            foreach (var child in cell.Blocks)
            {
                // Top is an offset within the cell's content box; the padding is added once, by
                // ChildOrigin, so that measuring and drawing cannot disagree about it.
                child.Top = content;
                content += child.View.Measure(Math.Max(0, inner)).Height;
            }

            var height = content + (2 * CellPadding);

            cell.Height = height;

            // A cell spanning rows is not the height of the row it starts in; it only has to fit
            // across all of them. Charging its whole height to the first row would make that row
            // as tall as the span, which is the classic way to get a table with one enormous row.
            if (cell.Placement.Bottom - cell.Placement.Top == 1)
            {
                heights[cell.Placement.Top] = Math.Max(heights[cell.Placement.Top], height);
            }
        }

        // Now the spanning cells, against the row heights the single-row cells settled. Only a
        // cell that still does not fit pushes, and it pushes the last row it covers.
        foreach (var cell in _cells)
        {
            var span = cell.Placement.Bottom - cell.Placement.Top;

            if (span == 1)
            {
                continue;
            }

            var covered = 0.0;

            for (var r = cell.Placement.Top; r < cell.Placement.Bottom && r < heights.Length; r++)
            {
                covered += heights[r];
            }

            var last = Math.Min(cell.Placement.Bottom, heights.Length) - 1;

            if (last >= 0 && covered < cell.Height)
            {
                heights[last] += cell.Height - covered;
            }
        }

        _rowEdges = Edges(heights);
        _size = new Size(width, _rowEdges[^1]);

        return _size;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var cell in _cells)
        {
            var bounds = CellBounds(cell.Placement).Translate(new Vector(origin.X, origin.Y));

            if (cell.Fill is { } fill)
            {
                context.FillRectangle(fill, bounds);
            }

            context.DrawRectangle(null, _border, bounds);

            var y = bounds.Y + CellPadding;

            foreach (var child in cell.Blocks)
            {
                var at = ChildOrigin(cell, child);

                child.View.Render(context, new Point(origin.X + at.X, origin.Y + at.Y));
            }
        }
    }

    /// <inheritdoc/>
    public override int HitTest(Point local)
    {
        var cell = NearestCell(local);

        if (cell is null)
        {
            return 0;
        }

        var bounds = CellBounds(cell.Placement);
        var inner = new Point(local.X - bounds.X - CellPadding, local.Y - bounds.Y - CellPadding);
        var child = NearestChild(cell, inner.Y);

        // An empty cell is not schema-valid, but the schema reports rather than throws and
        // nothing stops a caller building one, so there may be nothing to hand the point to.
        return child is null
            ? cell.Start
            : child.Start + child.View.HitTest(new Point(inner.X, inner.Y - child.Top));
    }

    /// <inheritdoc/>
    public override Rect GetCaretRect(int localPosition)
    {
        if (Find(localPosition) is not { } found)
        {
            // A boundary position — between two rows, say — has no glyph to sit beside. The top
            // left of the table is wrong but drawable, which beats throwing on a position the
            // caret can legitimately hold.
            return new Rect(0, 0, 0, _size.Height);
        }

        var (cell, child) = found;
        var origin = ChildOrigin(cell, child);

        return child.View
            .GetCaretRect(Math.Clamp(localPosition - child.Start, 0, child.View.ContentSize))
            .Translate(new Vector(origin.X, origin.Y));
    }

    /// <summary>
    /// The boxes of the given cells, for a rectangular selection.
    /// </summary>
    /// <param name="cells">
    /// The document positions directly before each selected cell, as
    /// <see cref="CellSelection.Cells"/> reports them.
    /// </param>
    /// <param name="start">This table's own first inner position, in document coordinates.</param>
    /// <remarks>
    /// Distinct from <see cref="GetSelectionRects"/>, which highlights the text a range covers.
    /// A rectangle selects cells rather than text, so an empty cell must still be filled and a
    /// half-empty one filled to its edges — anything else draws a ragged rectangle that does not
    /// look like a selection at all.
    /// </remarks>
    public IReadOnlyList<Rect> GetCellRects(IReadOnlyCollection<int> cells, int start)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var rects = new List<Rect>();

        Collect(cells, rects, start, default);

        return rects;
    }

    private void Collect(IReadOnlyCollection<int> cells, List<Rect> into, int start, Vector offset)
    {
        foreach (var cell in _cells)
        {
            // A cell's local first inner position is one past the position before it, which is
            // what a cell selection names.
            if (cells.Contains(start + cell.Start - 1))
            {
                into.Add(CellBounds(cell.Placement).Translate(offset));

                // A selected cell is covered whole, so there is nothing to find inside it. A
                // rectangle cannot select a cell and one of its nested cells at once: they
                // belong to different grids, and Across resolves to exactly one of them.
                continue;
            }

            foreach (var child in cell.Blocks)
            {
                if (child.View is not TableBlockView nested)
                {
                    continue;
                }

                var origin = ChildOrigin(cell, child);

                nested.Collect(
                    cells,
                    into,
                    start + child.Start,
                    offset + new Vector(origin.X, origin.Y));
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No guard against an empty or inverted range: each cell clamps the range to its own
    /// content and skips anything that comes out empty, so those cases already produce no
    /// rectangles. A guard here would be an early return that no test could distinguish.
    /// </remarks>
    public override IReadOnlyList<Rect> GetSelectionRects(int from, int to)
    {
        var rects = new List<Rect>();

        foreach (var cell in _cells)
        {
            foreach (var child in cell.Blocks)
            {
                var start = Math.Max(from - child.Start, 0);
                var end = Math.Min(to - child.Start, child.View.ContentSize);

                if (end <= start)
                {
                    continue;
                }

                var origin = ChildOrigin(cell, child);

                foreach (var rect in child.View.GetSelectionRects(start, end))
                {
                    rects.Add(rect.Translate(new Vector(origin.X, origin.Y)));
                }
            }
        }

        return rects;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Recursive rather than a lookup, so a table inside a cell answers for its own cells and
    /// the editing layer never has to know how deep it went.
    /// </remarks>
    public override TextAt? ParagraphAt(int localPosition)
    {
        if (Find(localPosition) is not ({ } cell, { } child))
        {
            return null;
        }

        if (child.View.ParagraphAt(localPosition - child.Start) is not { } inner)
        {
            return null;
        }

        var origin = ChildOrigin(cell, child);

        return inner with
        {
            Start = child.Start + inner.Start,
            Origin = new Point(origin.X + inner.Origin.X, origin.Y + inner.Origin.Y),
        };
    }

    /// <inheritdoc/>
    /// <remarks>Recursive, for the same reason <see cref="ParagraphAt"/> is.</remarks>
    public override Rect? GetImageRect(int localPosition)
    {
        if (Find(localPosition) is not ({ } cell, { } child))
        {
            return null;
        }

        if (child.View.GetImageRect(localPosition - child.Start) is not { } inner)
        {
            return null;
        }

        var origin = ChildOrigin(cell, child);

        return inner.Translate(new Vector(origin.X, origin.Y));
    }

    private (Cell Cell, Child Child)? Find(int localPosition)
    {
        foreach (var cell in _cells)
        {
            foreach (var child in cell.Blocks)
            {
                if (localPosition >= child.Start
                    && localPosition <= child.Start + child.View.ContentSize)
                {
                    return (cell, child);
                }
            }
        }

        return null;
    }

    private Point ChildOrigin(Cell cell, Child child)
    {
        var bounds = CellBounds(cell.Placement);

        return new Point(bounds.X + CellPadding, bounds.Y + CellPadding + child.Top);
    }

    private Cell? NearestCell(Point local)
    {
        Cell? nearest = null;
        var best = double.PositiveInfinity;

        foreach (var cell in _cells)
        {
            var bounds = CellBounds(cell.Placement);

            if (bounds.Contains(local))
            {
                return cell;
            }

            // Squared distance to the cell's box, which is zero along any axis the point already
            // lies within. A click in the gap between cells, or outside the table entirely,
            // therefore lands in the cell it is nearest rather than nowhere.
            var dx = Math.Max(0, Math.Max(bounds.X - local.X, local.X - bounds.Right));
            var dy = Math.Max(0, Math.Max(bounds.Y - local.Y, local.Y - bounds.Bottom));
            var distance = (dx * dx) + (dy * dy);

            if (distance < best)
            {
                best = distance;
                nearest = cell;
            }
        }

        return nearest;
    }

    private static Child? NearestChild(Cell cell, double y)
    {
        Child? last = null;

        foreach (var child in cell.Blocks)
        {
            if (y < child.Top + child.View.Size.Height)
            {
                return child;
            }

            last = child;
        }

        return last;
    }

    private double CellWidth(CellPlacement placement) =>
        _columnEdges[Math.Min(placement.Right, _columns)] - _columnEdges[placement.Left];

    private Rect CellBounds(CellPlacement placement)
    {
        var left = _columnEdges[placement.Left];
        var top = _rowEdges[placement.Top];

        return new Rect(
            left,
            top,
            CellWidth(placement),
            _rowEdges[Math.Min(placement.Bottom, _table.Rows.Length)] - top);
    }

    private double[] ColumnEdges(double available)
    {
        var widths = new double[_columns];

        if (_table.ColumnWidths.Length == 0)
        {
            var each = _columns == 0 ? 0 : available / _columns;

            Array.Fill(widths, each);
        }
        else
        {
            // Stated widths are a proportion, not a promise: a table that states more than the
            // available width is scaled down to fit rather than drawn off the edge, and columns
            // the table said nothing about fall back to an even share of what is stated.
            var fallback = _table.ColumnWidths.Length == 0
                ? 0
                : _table.ColumnWidths.Average();

            for (var i = 0; i < _columns; i++)
            {
                widths[i] = i < _table.ColumnWidths.Length ? _table.ColumnWidths[i] : fallback;
            }

            var total = widths.Sum();

            if (total > 0 && available > 0)
            {
                var scale = available / total;

                for (var i = 0; i < widths.Length; i++)
                {
                    widths[i] *= scale;
                }
            }
        }

        return Edges(widths);
    }

    private static double[] Edges(double[] sizes)
    {
        var edges = new double[sizes.Length + 1];

        for (var i = 0; i < sizes.Length; i++)
        {
            edges[i + 1] = edges[i] + sizes[i];
        }

        return edges;
    }

    /// <summary>One cell, its place in the grid, and the views of the blocks inside it.</summary>
    private sealed class Cell(
        TableCellNode node,
        CellPlacement placement,
        int start,
        ImmutableArray<Child> blocks,
        IBrush? header)
    {
        public CellPlacement Placement { get; } = placement;

        /// <summary>The cell's own background, or null to draw nothing behind it.</summary>
        /// <remarks>
        /// Built once: a brush per cell per frame is a lot of garbage for a fill. A background the
        /// document states wins over the header tint, because the document was explicit and the
        /// tint is only the control saying what a header row usually looks like.
        /// </remarks>
        public IBrush? Fill { get; } = node.Background is { } colour
            ? new SolidColorBrush(colour.ToAvalonia())
            : node.IsHeader ? header : null;

        /// <summary>The cell's own first inner position, for a cell with nothing in it.</summary>
        public int Start { get; } = start;

        /// <summary>The blocks inside the cell, with where each is drawn.</summary>
        public ImmutableArray<Child> Blocks { get; } = blocks;

        /// <summary>What the cell measured to, before its row was settled.</summary>
        public double Height { get; set; }
    }

    /// <summary>One block inside a cell, with where it starts in both senses.</summary>
    private sealed class Child(BlockNode node, BlockView view, int start)
    {
        /// <summary>The block itself, so an update can tell whether this one was edited.</summary>
        public BlockNode Node { get; } = node;

        public BlockView View { get; } = view;

        /// <summary>The local position of the block's first inner position.</summary>
        public int Start { get; } = start;

        /// <summary>How far down the cell's content the block is drawn.</summary>
        public double Top { get; set; }
    }
}
