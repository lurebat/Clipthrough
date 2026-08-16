namespace Vellum;

/// <summary>
/// Replaces a range of the document with a slice.
/// </summary>
/// <remarks>
/// <para>
/// Almost every edit is this one step. Typing is a replacement of an empty range with one
/// character; Backspace is a replacement of one character with nothing; Enter is a replacement
/// of an empty range with a slice that happens to be open at both ends; paste is the general
/// case. Keeping them one step means undo, mapping and rebasing are written once.
/// </para>
/// </remarks>
/// <param name="From">Where the replaced range starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Content">What to put there.</param>
public sealed record ReplaceStep(int From, int To, Slice Content) : Step
{
    /// <summary>Deletes a range.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    public static ReplaceStep Delete(int from, int to) => new(from, to, Slice.Empty);

    /// <summary>Inserts a slice at a position.</summary>
    /// <param name="at">Where to insert.</param>
    /// <param name="content">What to insert.</param>
    public static ReplaceStep Insert(int at, Slice content) => new(at, at, content);

    /// <inheritdoc/>
    public override StepMap GetMap() => new(From, To - From, Content.Size);

    /// <inheritdoc/>
    public override StepResult Apply(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (From < 0 || To < From || To > doc.ContentSize)
        {
            return StepResult.Failed($"Range [{From}, {To}) is not inside the document.");
        }

        var start = doc.Resolve(From);
        var end = doc.Resolve(To);

        if (Content.OpenStart > start.Depth || Content.OpenEnd > end.Depth)
        {
            return StepResult.Failed("The slice is open deeper than the position it goes into.");
        }

        // The slice's two edges must sit at the same depth as each other once its open levels
        // are accounted for, or there is no single level at which the document closes back up
        // and the step's own size prediction stops being true.
        var extra = start.Depth - Content.OpenStart;

        if (extra != end.Depth - Content.OpenEnd)
        {
            return StepResult.Failed(
                $"Inconsistent open depths: {start.Depth}-{Content.OpenStart} against "
                + $"{end.Depth}-{Content.OpenEnd}.");
        }

        if (TakesATableApart(start, end, extra))
        {
            return StepResult.Failed("The range would join two parts of a table together.");
        }

        try
        {
            return Rebuild(doc, start, end, extra);
        }
        catch (ArgumentException ex)
        {
            // The model refuses invalid content at construction - a fragment cut through a
            // surrogate pair, a node type that cannot live where the slice would put it. Those
            // are step failures, not editor crashes.
            return StepResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public override Step Invert(DocumentNode docBefore)
    {
        ArgumentNullException.ThrowIfNull(docBefore);

        return new ReplaceStep(From, From + Content.Size, Slice.Cut(docBefore, From, To));
    }

    /// <inheritdoc/>
    public override Step? Map(Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        // The two ends bind outwards, so that concurrent edits inside the range shrink it
        // rather than pushing its edges around the surrounding content.
        var from = mapping.MapWithResult(From, Assoc.After);
        var to = mapping.MapWithResult(To, Assoc.Before);

        if (from.Deleted && to.Deleted)
        {
            return null;
        }

        return new ReplaceStep(from.Pos, Math.Max(from.Pos, to.Pos), Content);
    }

    /// <summary>Whether closing the range back up would merge two parts of a table.</summary>
    /// <remarks>
    /// <para>
    /// The levels the step has to merge are the ones the slice does not supply — the first
    /// <paramref name="extra"/> of them, which are rebuilt from the range's <em>start</em> and
    /// then joined to the tree the range's end left behind. Where the two ends share an ancestor
    /// that join puts a node back together with itself and is what editing inside one cell is
    /// made of. Where they do not, it merges two different nodes, and if either is a table, a row
    /// or a cell then a piece of the table disappears into another one.
    /// </para>
    /// <para>
    /// This cannot be left to <see cref="DocumentSchema"/>, which is only asked whether the
    /// result is a legal table and not whether it is the same one: two cells of a two-by-two
    /// table merge into a grid with a hole in it and are caught, while two cells of a table with
    /// one row merge into a table with one cell, which is perfectly rectangular and perfectly
    /// wrong. Nor can <see cref="TreeSurgery.Splice"/> decide it — at the seam it sees two cells
    /// and cannot tell two halves of one cell from two different ones. Only the two resolved
    /// positions know that, so this is where it is asked.
    /// </para>
    /// <para>
    /// A slice open all the way down is the case this must <em>not</em> refuse: cutting a range
    /// that spans two cells and putting the very same slice back is an identity, and it is an
    /// identity precisely because the slice carries the cells with it and nothing is merged.
    /// </para>
    /// </remarks>
    private static bool TakesATableApart(ResolvedPos start, ResolvedPos end, int extra)
    {
        for (var depth = start.SharedDepth(end) + 1; depth <= extra; depth++)
        {
            if (start.NodeAt(depth) is TableNode or TableRowNode or TableCellNode
                || end.NodeAt(depth) is TableNode or TableRowNode or TableCellNode)
            {
                return true;
            }
        }

        return false;
    }

    private StepResult Rebuild(DocumentNode doc, ResolvedPos start, ResolvedPos end, int extra)    {
        var left = TreeSurgery.CutBefore(start, 0);
        var right = TreeSurgery.CutAfter(end, 0);

        // The slice arrives at whatever depth it was cut from, so it has to be re-wrapped in
        // the ancestors of the position it is going into before the seams can be joined.
        IReadOnlyList<Node> middle = Content.Content;

        for (var depth = extra; depth >= 1; depth--)
        {
            if (start.NodeAt(depth) is ParagraphNode paragraph)
            {
                if (middle.Count > 0)
                {
                    return StepResult.Failed("Cannot put block nodes inside a paragraph.");
                }

                middle = [paragraph.WithContent(InlineContent.Empty)];
            }
            else
            {
                middle = [start.NodeAt(depth).WithChildren(middle)];
            }
        }

        var withMiddle = TreeSurgery.Splice(left, doc.WithChildren(middle), start.Depth);

        if (withMiddle is null)
        {
            return StepResult.Failed("The slice cannot be joined to the content before it.");
        }

        if (TreeSurgery.Splice(withMiddle, right, end.Depth) is not DocumentNode result)
        {
            return StepResult.Failed("The slice cannot be joined to the content after it.");
        }

        var violations = DocumentSchema.Validate(result);

        if (!violations.IsEmpty)
        {
            return StepResult.Failed($"The result is not a valid document: {violations[0]}.");
        }

        return StepResult.Ok(result);
    }
}
