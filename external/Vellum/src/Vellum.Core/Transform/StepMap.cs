namespace Vellum;

/// <summary>
/// Which side of a position content is considered to belong to when an edit lands exactly on it.
/// </summary>
/// <remarks>
/// A caret sitting between two characters has to pick a side when text is inserted at exactly
/// that spot: either it stays put and the new text appears after it, or it moves along and the
/// new text appears before it. Neither is universally right, so callers say which they mean.
/// </remarks>
public enum Assoc
{
    /// <summary>Bind to the content before the position; insertions at it push content to the right.</summary>
    Before = -1,

    /// <summary>Bind to the content after the position; insertions at it move the position along.</summary>
    After = 1,
}

/// <summary>The outcome of mapping a position through an edit.</summary>
/// <param name="Pos">The position in the new document.</param>
/// <param name="Deleted">
/// Whether the content this position was bound to no longer exists. The position is still
/// usable — it collapses to the edge of the replaced range — but a caller that cares about
/// identity rather than location should notice.
/// </param>
public readonly record struct MapResult(int Pos, bool Deleted);

/// <summary>
/// How one step moved positions: the record of a single replaced range.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole reason positions are flat integers. Rewriting a stale position after an
/// edit is arithmetic on one range rather than a walk of the tree, which is what makes it
/// affordable to route every externally-held position — selections, link anchors, later
/// collaborative cursors — through the mapping on every keystroke.
/// </para>
/// <para>
/// A step that does not move anything, such as applying a mark, has an
/// <see cref="IsIdentity"/> map. Recording it anyway keeps a mapping's map count equal to its
/// step count, so a step's map can always be found by index.
/// </para>
/// </remarks>
public readonly record struct StepMap
{
    /// <summary>Creates a map for a range replacement.</summary>
    /// <param name="start">Where the replaced range begins.</param>
    /// <param name="oldSize">How many positions the range covered before.</param>
    /// <param name="newSize">How many positions it covers after.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is negative, or the range described could not fit in a document.
    /// </exception>
    public StepMap(int start, int oldSize, int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(oldSize);
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);

        // Mapping is addition, and it runs on every keystroke for every held position, so it
        // cannot afford checked arithmetic. Refusing a map that could overflow costs nothing
        // here and is the difference between a position landing past the end of the document
        // and one landing at a large negative number, which reads as valid to everything.
        if (start > int.MaxValue - oldSize || start > int.MaxValue - newSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newSize),
                "A step map cannot describe a range that runs past the end of the position space.");
        }

        Start = start;
        OldSize = oldSize;
        NewSize = newSize;
    }

    /// <summary>A map for a step that moves nothing.</summary>
    public static StepMap Identity { get; }

    /// <summary>Where the replaced range begins. Unchanged by the edit, by definition.</summary>
    public int Start { get; }

    /// <summary>How many positions the replaced range covered before the edit.</summary>
    public int OldSize { get; }

    /// <summary>How many positions it covers after.</summary>
    public int NewSize { get; }

    /// <summary>Whether this map leaves every position where it was.</summary>
    public bool IsIdentity => OldSize == 0 && NewSize == 0;

    /// <summary>How much longer the document got. Negative for a deletion.</summary>
    public int SizeDelta => NewSize - OldSize;

    /// <summary>Rewrites a position for the document after the edit.</summary>
    /// <param name="pos">A position in the document before the edit.</param>
    /// <param name="assoc">Which side of the position to bind to.</param>
    public int Map(int pos, Assoc assoc = Assoc.After) => MapWithResult(pos, assoc).Pos;

    /// <summary>
    /// Rewrites a position, also reporting whether what it pointed at survived.
    /// </summary>
    /// <param name="pos">A position in the document before the edit.</param>
    /// <param name="assoc">Which side of the position to bind to.</param>
    public MapResult MapWithResult(int pos, Assoc assoc = Assoc.After)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pos);

        var end = Start + OldSize;

        if (pos < Start)
        {
            return new MapResult(pos, false);
        }

        if (pos > end)
        {
            return new MapResult(pos + SizeDelta, false);
        }

        // The position is inside the replaced range, so it has to collapse to one edge of the
        // replacement. At the range's own edges the answer is forced regardless of what the
        // caller asked for: a position before the range cannot be pushed past the new content,
        // and one after it cannot be pulled in front of it. Only a pure insertion, where both
        // edges coincide, is genuinely the caller's choice.
        var side = OldSize == 0
            ? assoc
            : pos == Start ? Assoc.Before
            : pos == end ? Assoc.After
            : assoc;

        var mapped = side == Assoc.Before ? Start : Start + NewSize;
        var deleted = assoc == Assoc.Before ? pos != Start : pos != end;

        return new MapResult(mapped, deleted);
    }

    /// <summary>The map that undoes this one.</summary>
    public StepMap Invert() => new(Start, NewSize, OldSize);

    /// <inheritdoc/>
    public override string ToString() =>
        IsIdentity ? "identity" : $"[{Start}, +{OldSize}) -> +{NewSize}";
}
