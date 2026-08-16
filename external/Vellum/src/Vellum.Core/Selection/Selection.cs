namespace Vellum;

/// <summary>
/// What is currently selected.
/// </summary>
/// <param name="Anchor">Where the selection was started from.</param>
/// <param name="Head">Where it currently ends - the end that moves.</param>
/// <remarks>
/// <para>
/// Anchor and head are kept apart from from and to because dragging backwards is a different
/// thing from dragging forwards even though it covers the same text: Shift+Right must extend a
/// backwards selection by shrinking it.
/// </para>
/// <para>
/// A selection carries no reference to the document it was measured against, so it cannot check
/// its own validity. Validity is established when one is created and preserved by
/// <see cref="Map"/>; nothing else may fabricate one.
/// </para>
/// </remarks>
public abstract record Selection(int Anchor, int Head)
{
    /// <summary>The lower of the two ends.</summary>
    public int From => Math.Min(Anchor, Head);

    /// <summary>The higher of the two ends.</summary>
    public int To => Math.Max(Anchor, Head);

    /// <summary>Whether this is a caret rather than a range.</summary>
    public bool IsEmpty => Anchor == Head;

    /// <summary>
    /// This selection rewritten for a document that the given changes have already been applied to.
    /// </summary>
    /// <param name="doc">The document after the changes.</param>
    /// <param name="mapping">The changes.</param>
    /// <remarks>
    /// Returns some valid selection, always. A selection whose subject was deleted becomes a caret
    /// near where it used to be rather than nothing, because there is no such thing as an editor
    /// with no selection and every caller would otherwise have to invent that fallback itself.
    /// </remarks>
    public abstract Selection Map(DocumentNode doc, Mapping mapping);

    /// <summary>
    /// The most sensible selection at or near a position.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="pos">Where to look.</param>
    /// <param name="forward">Which direction to prefer when both are equally close.</param>
    /// <remarks>
    /// Searches outward for somewhere a caret can legally sit. Failing that - a document of
    /// nothing but images and rules - it selects the nearest node instead, which is the behaviour
    /// a reader expects when the paragraph they were in is deleted and only a picture remains.
    /// </remarks>
    public static Selection Near(DocumentNode doc, int pos, bool forward = true)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var start = Math.Clamp(pos, 0, doc.ContentSize);

        for (var distance = 0; distance <= doc.ContentSize; distance++)
        {
            var first = forward ? start + distance : start - distance;
            var second = forward ? start - distance : start + distance;

            if (Candidate(doc, first) is { } a)
            {
                return a;
            }

            if (distance > 0 && Candidate(doc, second) is { } b)
            {
                return b;
            }
        }

        // Only reachable for a document with no blocks at all.
        return new TextSelection(0, 0);
    }

    /// <summary>A caret at the first place in the document one can sit.</summary>
    /// <param name="doc">The document.</param>
    public static Selection AtStart(DocumentNode doc) => Near(doc, 0);

    /// <summary>A caret at the last place in the document one can sit.</summary>
    /// <param name="doc">The document.</param>
    public static Selection AtEnd(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        return Near(doc, doc.ContentSize, forward: false);
    }

    /// <summary>
    /// Whether a node is the kind that can be selected whole rather than typed into.
    /// </summary>
    /// <param name="node">The node to test.</param>
    /// <remarks>
    /// Leaves and only leaves. Selecting a list or a table as a unit is expressible - the position
    /// before it is a perfectly good position - but it is not what any gesture in this editor
    /// produces, and admitting it would mean every command had to decide what Backspace means
    /// with a whole table selected.
    /// </remarks>
    internal static bool IsSelectable(Node node) => node.IsLeaf;

    private static Selection? Candidate(DocumentNode doc, int pos)
    {
        if (pos < 0 || pos > doc.ContentSize)
        {
            return null;
        }

        var at = doc.Resolve(pos);

        if (at.IsInText)
        {
            return new TextSelection(pos, pos);
        }

        return at.NodeAfter is { } node && IsSelectable(node)
            ? new NodeSelection(pos, node.NodeSize)
            : null;
    }
}

/// <summary>
/// A caret or a run of text.
/// </summary>
/// <param name="Anchor">Where the selection was started from.</param>
/// <param name="Head">Where it currently ends.</param>
public sealed record TextSelection(int Anchor, int Head) : Selection(Anchor, Head)
{
    /// <summary>A caret.</summary>
    /// <param name="pos">Where it sits.</param>
    public static TextSelection Cursor(int pos) => new(pos, pos);

    /// <summary>
    /// A text selection between two positions, repaired if either is not somewhere text lives.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="anchor">Where to start.</param>
    /// <param name="head">Where to end.</param>
    public static Selection Create(DocumentNode doc, int anchor, int head)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (anchor < 0 || head < 0 || anchor > doc.ContentSize || head > doc.ContentSize)
        {
            return Near(doc, Math.Clamp(head, 0, doc.ContentSize));
        }

        if (!doc.Resolve(head).IsInText)
        {
            return Near(doc, head, forward: head >= anchor);
        }

        return new TextSelection(doc.Resolve(anchor).IsInText ? anchor : head, head);
    }

    /// <inheritdoc/>
    public override Selection Map(DocumentNode doc, Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);

        // The head wins if only one end can be honoured. It is the end the reader was moving.
        return Create(doc, mapping.Map(Anchor), mapping.Map(Head));
    }
}

/// <summary>
/// One node selected whole - an image or a rule clicked as a unit.
/// </summary>
/// <param name="Pos">The position directly before the node.</param>
/// <param name="NodeSize">How many positions the node occupies.</param>
/// <remarks>
/// The size is carried rather than looked up so that <c>From</c> and <c>To</c> actually cover the
/// node, which is what makes "select the image and press Delete" the same code path as deleting a
/// range of text. It is a deliberate departure from the one-argument form sketched in the
/// architecture, which could not have filled in the base record's two ends honestly.
/// </remarks>
public sealed record NodeSelection(int Pos, int NodeSize) : Selection(Pos, Pos + NodeSize)
{
    /// <summary>
    /// Selects the node directly after a position, or nothing if there is no selectable node there.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="pos">The position before the node.</param>
    public static NodeSelection? At(DocumentNode doc, int pos)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (pos < 0 || pos > doc.ContentSize)
        {
            return null;
        }

        return doc.Resolve(pos).NodeAfter is { } node && IsSelectable(node)
            ? new NodeSelection(pos, node.NodeSize)
            : null;
    }

    /// <inheritdoc/>
    public override Selection Map(DocumentNode doc, Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);

        var mapped = mapping.MapWithResult(Pos, Assoc.After);

        return mapped.Deleted
            ? Near(doc, mapped.Pos)
            : At(doc, mapped.Pos) ?? Near(doc, mapped.Pos);
    }
}
