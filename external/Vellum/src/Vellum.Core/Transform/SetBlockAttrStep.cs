namespace Vellum;

/// <summary>
/// A property of a block that a step can change without touching its content.
/// </summary>
/// <remarks>
/// Every one of these is an enum or a small non-negative integer, which is why a step can carry
/// its value as an <see cref="int"/>. That keeps the step a plain value: comparable, hashable and
/// trivially invertible, with none of the boxing or type-testing an <c>object</c> payload would
/// need. Anything richer than an integer is not a block attribute; it is content, and content
/// changes go through a replace step.
/// </remarks>
public enum BlockAttr
{
    /// <summary>A paragraph's <see cref="ParagraphKind"/>: body text, a heading, a quote.</summary>
    ParagraphKind,

    /// <summary>A paragraph's or block image's <see cref="TextAlign"/>.</summary>
    Align,

    /// <summary>A paragraph's indent level.</summary>
    IndentLevel,

    /// <summary>A list's <see cref="Vellum.ListKind"/>.</summary>
    ListKind,

    /// <summary>The number an ordered list starts at.</summary>
    ListStart,
}

/// <summary>
/// Changes one attribute of one block.
/// </summary>
/// <param name="Pos">The position directly before the block.</param>
/// <param name="Attr">Which attribute to change.</param>
/// <param name="Value">Its new value.</param>
/// <remarks>
/// <para>
/// Turning a paragraph into a heading moves nothing, so this reports the identity map. Doing it
/// as a replace step would work and would be wrong: the map would collapse the paragraph's
/// interior onto its edges and throw away the cursor, for a change the reader would describe as
/// "the text stayed exactly where it was".
/// </para>
/// <para>
/// The position is the one <em>before</em> the block rather than an index into its parent, so
/// that it maps through other edits like any other position.
/// </para>
/// </remarks>
public sealed record SetBlockAttrStep(int Pos, BlockAttr Attr, int Value) : Step
{
    /// <inheritdoc/>
    public override StepResult Apply(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (Pos < 0 || Pos > doc.ContentSize)
        {
            return StepResult.Failed($"position {Pos} is outside the document");
        }

        var at = doc.Resolve(Pos);

        if (at.NodeAfter is not { } target)
        {
            return StepResult.Failed($"no block starts at position {Pos}");
        }

        var changed = WithAttr(target, Attr, Value);

        return changed is null
            ? StepResult.Failed(
                $"a {target.TypeName} has no {Attr}, or {Value} is not a legal value for it")
            : StepResult.Ok(TreeSurgery.ReplaceNodeAfter(at, changed));
    }

    /// <inheritdoc/>
    public override Step Invert(DocumentNode docBefore)
    {
        ArgumentNullException.ThrowIfNull(docBefore);

        var target = docBefore.Resolve(Pos).NodeAfter;
        var current = target is null ? Value : ReadAttr(target, Attr) ?? Value;

        // If the step could not apply, its inverse is a no-op that will also fail to apply, which
        // is the honest answer: nothing happened, so nothing needs undoing.
        return new SetBlockAttrStep(Pos, Attr, current);
    }

    /// <inheritdoc/>
    public override StepMap GetMap() => StepMap.Identity;

    /// <inheritdoc/>
    public override Step? Map(Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var mapped = mapping.MapWithResult(Pos, Assoc.After);

        // A block that no longer exists has no attributes to set.
        return mapped.Deleted ? null : this with { Pos = mapped.Pos };
    }

    private static int? ReadAttr(Node node, BlockAttr attr) => (node, attr) switch
    {
        (ParagraphNode p, BlockAttr.ParagraphKind) => (int)p.Kind,
        (ParagraphNode p, BlockAttr.Align) => (int)p.Align,
        (ParagraphNode p, BlockAttr.IndentLevel) => p.IndentLevel,
        (BlockImageNode i, BlockAttr.Align) => (int)i.Align,
        (ListNode l, BlockAttr.ListKind) => (int)l.Kind,
        (ListNode l, BlockAttr.ListStart) => l.Start,
        _ => null,
    };

    private static Node? WithAttr(Node node, BlockAttr attr, int value) => (node, attr) switch
    {
        (ParagraphNode p, BlockAttr.ParagraphKind) when IsDefined<ParagraphKind>(value) =>
            p.WithKind((ParagraphKind)value),
        (ParagraphNode p, BlockAttr.Align) when IsDefined<TextAlign>(value) =>
            p.WithAlign((TextAlign)value),
        (ParagraphNode p, BlockAttr.IndentLevel) when value >= 0 =>
            p.WithIndentLevel(value),
        (BlockImageNode i, BlockAttr.Align) when IsDefined<TextAlign>(value) =>
            new BlockImageNode(i.Image, (TextAlign)value),
        (ListNode l, BlockAttr.ListKind) when IsDefined<ListKind>(value) =>
            new ListNode(l.Items, (ListKind)value, l.Start),
        (ListNode l, BlockAttr.ListStart) when value >= 0 =>
            new ListNode(l.Items, l.Kind, value),
        _ => null,
    };

    private static bool IsDefined<T>(int value)
        where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value);
}
