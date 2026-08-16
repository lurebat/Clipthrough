using System.Collections.Immutable;

namespace Vellum;

/// <summary>One way in which a document breaks the schema.</summary>
/// <param name="Path">Where the problem is, as a slash-separated node path.</param>
/// <param name="Message">What is wrong.</param>
public readonly record struct SchemaViolation(string Path, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// The structural rules a document must satisfy.
/// </summary>
/// <remarks>
/// <para>
/// Child <em>types</em> are enforced by the node constructors — a
/// <see cref="TableRowNode"/> simply cannot hold a paragraph. What is left for the schema is
/// cardinality and the rules that span several nodes, which a constructor cannot see.
/// </para>
/// <para>
/// Validation reports rather than throws, because the caller that needs it most is
/// <c>Step.Apply</c>, whose contract is to return a failure instead of producing an invalid
/// document (architecture §7). A partially-valid tree must never reach the editor.
/// </para>
/// </remarks>
public static class DocumentSchema
{
    /// <summary>Whether <paramref name="node"/> and everything under it is valid.</summary>
    /// <param name="node">The node to check.</param>
    public static bool IsValid(Node node) => Validate(node).IsEmpty;

    /// <summary>Finds everything wrong with <paramref name="node"/> and its descendants.</summary>
    /// <param name="node">The node to check.</param>
    /// <returns>The violations, empty when the node is valid.</returns>
    public static ImmutableArray<SchemaViolation> Validate(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var violations = ImmutableArray.CreateBuilder<SchemaViolation>();
        Visit(node, node.TypeName, violations);

        return violations.ToImmutable();
    }

    private static void Visit(
        Node node, string path, ImmutableArray<SchemaViolation>.Builder violations)
    {
        switch (node)
        {
            case DocumentNode doc:
                // Without a block there is nowhere for the caret to be, and an editor with
                // no valid caret position has no coherent behaviour to fall back on.
                RequireNonEmpty(doc.Blocks.Length, path, "block", violations);
                break;

            case ListNode list:
                RequireNonEmpty(list.Items.Length, path, "item", violations);
                break;

            case ListItemNode item:
                RequireNonEmpty(item.Blocks.Length, path, "block", violations);
                break;

            case TableNode table:
                RequireNonEmpty(table.Rows.Length, path, "row", violations);
                ValidateTable(table, path, violations);
                break;

            case TableRowNode row:
                RequireNonEmpty(row.Cells.Length, path, "cell", violations);
                break;

            case TableCellNode cell:
                RequireNonEmpty(cell.Blocks.Length, path, "block", violations);
                break;

            default:
                break;
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            Visit(child, $"{path}/{i}:{child.TypeName}", violations);
        }
    }

    private static void ValidateTable(
        TableNode table, string path, ImmutableArray<SchemaViolation>.Builder violations)
    {
        if (table.Rows.IsEmpty)
        {
            return;
        }

        // Occupancy, not cell counts: a cell spanning rows fills slots in the rows below it,
        // so those rows legitimately carry fewer cells. What must hold is that the placements
        // tile the grid exactly - no hole a renderer would have to invent a cell for, and no
        // span hanging off the bottom.
        var placements = TableGeometry.Place(table, out var width);
        var height = table.Rows.Length;
        var covered = new bool[height, Math.Max(width, 1)];

        foreach (var placement in placements)
        {
            if (placement.Bottom > height)
            {
                violations.Add(new SchemaViolation(
                    $"{path}/{placement.Top}:table-row",
                    $"A cell spans rows {placement.Top} to {placement.Bottom - 1} but the "
                    + $"table has only {height} row(s)."));
            }

            for (var r = placement.Top; r < Math.Min(placement.Bottom, height); r++)
            {
                for (var c = placement.Left; c < placement.Right; c++)
                {
                    covered[r, c] = true;
                }
            }
        }

        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < width; c++)
            {
                if (!covered[r, c])
                {
                    violations.Add(new SchemaViolation(
                        $"{path}/{r}:table-row",
                        $"No cell covers column {c}, but the table is {width} column(s) wide."));
                }
            }
        }

        if (!table.ColumnWidths.IsEmpty && table.ColumnWidths.Length != width)
        {
            violations.Add(new SchemaViolation(
                path,
                $"{table.ColumnWidths.Length} column width(s) supplied for {width} column(s)."));
        }
    }

    private static void RequireNonEmpty(
        int count, string path, string childName, ImmutableArray<SchemaViolation>.Builder violations)
    {
        if (count == 0)
        {
            violations.Add(new SchemaViolation(path, $"Must contain at least one {childName}."));
        }
    }
}
