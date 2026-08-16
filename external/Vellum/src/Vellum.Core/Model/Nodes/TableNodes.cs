using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// A table.
/// </summary>
/// <remarks>
/// Tables exist in the model from the first increment even though no UI can reach them yet.
/// They are the only genuinely deep structure in the schema, and positions, steps and
/// mapping need to be exercised against nesting from the start rather than retrofitted to
/// cope with it later (architecture §12.3).
/// </remarks>
public sealed class TableNode : BlockNode
{
    private readonly ImmutableArray<TableRowNode> _rows;
    private readonly int _contentSize;

    /// <summary>Creates a table.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="columnWidths">
    /// Explicit column widths in device-independent pixels. Empty means size every column
    /// automatically; otherwise every entry must be a positive finite number.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A column width is not positive and finite.</exception>
    public TableNode(IEnumerable<TableRowNode> rows, IEnumerable<double>? columnWidths = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _rows = rows.ToImmutableArray();

        if (_rows.Contains(null!))
        {
            throw new ArgumentException("Table rows must not be null.", nameof(rows));
        }

        ColumnWidths = columnWidths?.ToImmutableArray() ?? ImmutableArray<double>.Empty;

        foreach (var width in ColumnWidths)
        {
            if (width <= 0 || !double.IsFinite(width))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columnWidths), width,
                    "A column width must be a positive finite number.");
            }
        }

        _contentSize = SumOfNodeSizes(_rows);
    }

    /// <summary>The rows.</summary>
    public ImmutableArray<TableRowNode> Rows => _rows;

    /// <summary>Explicit column widths, or empty for automatic sizing.</summary>
    public ImmutableArray<double> ColumnWidths { get; }

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _rows;

    /// <inheritdoc/>
    public override string TypeName => "table";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new TableNode(RequireAll<TableRowNode>(children, nameof(children)), ColumnWidths);

    /// <summary>Returns this table with different column widths.</summary>
    /// <param name="columnWidths">The replacement widths.</param>
    public TableNode WithColumnWidths(IEnumerable<double>? columnWidths) =>
        new(_rows, columnWidths);

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other)
    {
        var table = (TableNode)other;

        return ColumnWidths.SequenceEqual(table.ColumnWidths)
            && ChildrenEqual(_rows, table._rows);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var width in ColumnWidths)
        {
            hash.Add(width);
        }

        HashChildren(ref hash, _rows);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"table[{_rows.Length} rows]";
}

/// <summary>One row of a table.</summary>
public sealed class TableRowNode : BlockNode
{
    private readonly ImmutableArray<TableCellNode> _cells;
    private readonly int _contentSize;

    /// <summary>Creates a row.</summary>
    /// <param name="cells">The cells.</param>
    public TableRowNode(IEnumerable<TableCellNode> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        _cells = cells.ToImmutableArray();

        if (_cells.Contains(null!))
        {
            throw new ArgumentException("Table cells must not be null.", nameof(cells));
        }

        _contentSize = SumOfNodeSizes(_cells);
    }

    /// <summary>The cells.</summary>
    public ImmutableArray<TableCellNode> Cells => _cells;

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _cells;

    /// <inheritdoc/>
    public override string TypeName => "table-row";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new TableRowNode(RequireAll<TableCellNode>(children, nameof(children)));

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other) =>
        ChildrenEqual(_cells, ((TableRowNode)other)._cells);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        HashChildren(ref hash, _cells);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"table-row[{_cells.Length} cells]";
}

/// <summary>One cell of a table, holding block content.</summary>
public sealed class TableCellNode : BlockNode
{
    private readonly ImmutableArray<BlockNode> _blocks;
    private readonly int _contentSize;

    /// <summary>Creates a cell.</summary>
    /// <param name="blocks">The block content.</param>
    /// <param name="rowSpan">How many rows the cell covers. At least 1.</param>
    /// <param name="columnSpan">How many columns the cell covers. At least 1.</param>
    /// <param name="background">A background colour, or null.</param>
    /// <param name="isHeader">Whether the cell is a header cell.</param>
    /// <exception cref="ArgumentOutOfRangeException">A span is less than 1.</exception>
    public TableCellNode(
        IEnumerable<BlockNode> blocks,
        int rowSpan = 1,
        int columnSpan = 1,
        Rgba? background = null,
        bool isHeader = false)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);

        _blocks = blocks.ToImmutableArray();

        if (_blocks.Contains(null!))
        {
            throw new ArgumentException("Table cell blocks must not be null.", nameof(blocks));
        }

        RowSpan = rowSpan;
        ColumnSpan = columnSpan;
        Background = background;
        IsHeader = isHeader;
        _contentSize = SumOfNodeSizes(_blocks);
    }

    /// <summary>Creates a cell holding a single paragraph.</summary>
    /// <param name="text">The paragraph text.</param>
    public static TableCellNode FromText(string text) => new([ParagraphNode.FromText(text)]);

    /// <summary>The block content.</summary>
    public ImmutableArray<BlockNode> Blocks => _blocks;

    /// <summary>How many rows the cell covers.</summary>
    public int RowSpan { get; }

    /// <summary>How many columns the cell covers.</summary>
    public int ColumnSpan { get; }

    /// <summary>A background colour, or null.</summary>
    public Rgba? Background { get; }

    /// <summary>Whether the cell is a header cell.</summary>
    public bool IsHeader { get; }

    /// <inheritdoc/>
    public override int ContentSize => _contentSize;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => _blocks;

    /// <inheritdoc/>
    public override string TypeName => "table-cell";

    /// <inheritdoc/>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        new TableCellNode(
            RequireAll<BlockNode>(children, nameof(children)),
            RowSpan,
            ColumnSpan,
            Background,
            IsHeader);

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other)
    {
        var cell = (TableCellNode)other;

        return RowSpan == cell.RowSpan
            && ColumnSpan == cell.ColumnSpan
            && Background == cell.Background
            && IsHeader == cell.IsHeader
            && ChildrenEqual(_blocks, cell._blocks);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RowSpan);
        hash.Add(ColumnSpan);
        hash.Add(Background);
        hash.Add(IsHeader);
        HashChildren(ref hash, _blocks);

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        RowSpan == 1 && ColumnSpan == 1 ? "table-cell" : $"table-cell({RowSpan}x{ColumnSpan})";
}

/// <summary>Where one cell sits in the grid its table implies.</summary>
/// <param name="Top">The first row it occupies.</param>
/// <param name="Left">The first column it occupies.</param>
/// <param name="Bottom">One past the last row it occupies.</param>
/// <param name="Right">One past the last column it occupies.</param>
public readonly record struct CellPlacement(int Top, int Left, int Bottom, int Right);

/// <summary>
/// Works out which grid slots each cell of a table occupies.
/// </summary>
/// <remarks>
/// <para>
/// Rows are not columns. A cell that spans rows occupies slots in the rows below it, so those
/// rows carry fewer cells than the table is wide, and counting cells - or even summing column
/// spans - misreads such a table as ragged. Only tracking occupancy gets it right.
/// </para>
/// <para>
/// This is shared rather than duplicated because everything that needs to know the shape of a
/// table needs exactly this answer, and copies of an algorithm this fiddly would drift apart.
/// The schema validates against it, cell selection uses it to work out a rectangle of cells,
/// and the view uses it to decide which column a cell is drawn in.
/// </para>
/// </remarks>
public static class TableGeometry
{
    /// <summary>
    /// Places every cell of a table, in row-major order, matching the order the rows and
    /// their cells appear in.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="width">How many columns the placements span.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    /// <remarks>
    /// Total by construction: a cell whose span will not fit beside what is already there is
    /// pushed right until it does, widening the table rather than overwriting a neighbour.
    /// A malformed table therefore comes back as a wider grid with holes in it, which the
    /// schema can report on, instead of as silently lost cells.
    /// </remarks>
    public static ImmutableArray<CellPlacement> Place(TableNode table, out int width)
    {
        ArgumentNullException.ThrowIfNull(table);

        var placements = ImmutableArray.CreateBuilder<CellPlacement>();
        var occupied = new HashSet<(int Row, int Column)>();

        width = 0;

        for (var row = 0; row < table.Rows.Length; row++)
        {
            var column = 0;

            foreach (var cell in table.Rows[row].Cells)
            {
                while (!IsFree(occupied, row, column, cell.ColumnSpan))
                {
                    column++;
                }

                var placement = new CellPlacement(
                    row, column, row + cell.RowSpan, column + cell.ColumnSpan);

                for (var r = placement.Top; r < placement.Bottom; r++)
                {
                    for (var c = placement.Left; c < placement.Right; c++)
                    {
                        occupied.Add((r, c));
                    }
                }

                placements.Add(placement);
                width = Math.Max(width, placement.Right);
                column = placement.Right;
            }
        }

        return placements.ToImmutable();
    }

    private static bool IsFree(
        HashSet<(int Row, int Column)> occupied, int row, int column, int span)
    {
        for (var c = column; c < column + span; c++)
        {
            if (occupied.Contains((row, c)))
            {
                return false;
            }
        }

        return true;
    }
}
