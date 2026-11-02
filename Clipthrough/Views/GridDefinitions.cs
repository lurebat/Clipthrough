using Avalonia.Controls;

namespace Clipthrough.Views;

/// <summary>
/// Replaces a Grid's row and column definitions at runtime without crashing
/// its next layout pass.
/// </summary>
/// <remarks>
/// Avalonia's Grid caches each child's cell - its row and column, clamped to
/// the definition counts of the time - and only rebuilds that cache when a
/// child's Grid.Row/Column/Span changes, a child is added or removed, a spacing
/// changes, or an existing definition changes unit type. Assigning a new
/// RowDefinitions or ColumnDefinitions collection does none of those: the
/// setter only invalidates measure (Avalonia 12.1.1, Grid.cs). When the
/// children keep their cells, the next measure indexes the new, shorter
/// collection with the old indices and throws ArgumentOutOfRangeException from
/// Grid.MeasureCellsGroup. On Windows that pass runs inside the window
/// procedure, so it killed Clipthrough when a display-mode change moved the
/// window across the Short breakpoint.
/// </remarks>
internal static class GridDefinitions
{
    /// <summary>
    /// Sets <paramref name="grid"/>'s definitions and makes it rebuild its cell
    /// cache before it next measures. Does nothing when they already match.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public static bool Set(Grid grid, string columns, string rows)
    {
        var changed = false;
        if (!Matches(grid.ColumnDefinitions, columns))
        {
            grid.ColumnDefinitions = new ColumnDefinitions(columns);
            changed = true;
        }

        if (!Matches(grid.RowDefinitions, rows))
        {
            grid.RowDefinitions = new RowDefinitions(rows);
            changed = true;
        }

        if (changed)
        {
            InvalidateCells(grid);
        }

        return changed;
    }

    /// <summary>
    /// Clears the Grid's cell cache through the one public route that does
    /// so unconditionally: a definition changing its unit type, which calls
    /// the Grid's internal Invalidate(). The width is restored at once, so
    /// nothing is laid out at the temporary value.
    /// </summary>
    private static void InvalidateCells(Grid grid)
    {
        if (grid.ColumnDefinitions.Count > 0)
        {
            var column = grid.ColumnDefinitions[0];
            var original = column.Width;
            column.Width = Other(original);
            column.Width = original;
        }
        else if (grid.RowDefinitions.Count > 0)
        {
            var row = grid.RowDefinitions[0];
            var original = row.Height;
            row.Height = Other(original);
            row.Height = original;
        }

        // With no definitions at all the Grid lays every child in one cell and
        // there is nothing stale to index.
    }

    private static GridLength Other(GridLength length)
        => length.IsAbsolute ? GridLength.Auto : new GridLength(1, GridUnitType.Pixel);

    private static bool Matches(ColumnDefinitions current, string wanted)
        => Describe(current) == Describe(new ColumnDefinitions(wanted));

    private static bool Matches(RowDefinitions current, string wanted)
        => Describe(current) == Describe(new RowDefinitions(wanted));

    private static string Describe(ColumnDefinitions definitions)
        => string.Join(",", System.Linq.Enumerable.Select(definitions, d => d.Width.ToString()));

    private static string Describe(RowDefinitions definitions)
        => string.Join(",", System.Linq.Enumerable.Select(definitions, d => d.Height.ToString()));
}
