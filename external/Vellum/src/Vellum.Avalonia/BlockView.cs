using Avalonia;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>
/// The view of one block, per architecture 4.6.
/// </summary>
/// <remarks>
/// <para>
/// A block view owns its own layout and reports geometry, but it draws neither the caret nor
/// the selection highlight. Those belong to the view that hosts the blocks, because a selection
/// can span several of them and the caret is one concern with one blink timer. Blocks answer
/// <see cref="GetCaretRect"/> and <see cref="GetSelectionRects"/> and stop there.
/// </para>
/// <para>
/// Every position in this API is <em>local</em> to the block's content: 0 is the first position
/// inside the block, and <see cref="ContentSize"/> is the last. The host adds the block's
/// document offset. Keeping the conversion in one place is what stops position arithmetic from
/// leaking into every block type.
/// </para>
/// </remarks>
public abstract class BlockView
{
    /// <summary>The number of positions inside this block.</summary>
    public abstract int ContentSize { get; }

    /// <summary>The size the block last measured to, or a default size if never measured.</summary>
    public abstract Size Size { get; }

    /// <summary>
    /// Lays the block out for a given width and returns the space it needs.
    /// </summary>
    /// <param name="availableWidth">The width to wrap to.</param>
    public abstract Size Measure(double availableWidth);

    /// <summary>Draws the block with its top-left corner at <paramref name="origin"/>.</summary>
    /// <param name="context">The drawing context.</param>
    /// <param name="origin">Where the block's top-left corner sits.</param>
    public abstract void Render(DrawingContext context, Point origin);

    /// <summary>
    /// Finds the position nearest a point in the block's own coordinates.
    /// </summary>
    /// <param name="local">The point, relative to the block's top-left corner.</param>
    /// <returns>A local position in <c>[0, <see cref="ContentSize"/>]</c>.</returns>
    public abstract int HitTest(Point local);

    /// <summary>The caret rectangle at a local position, in the block's own coordinates.</summary>
    /// <param name="localPosition">A local position in <c>[0, <see cref="ContentSize"/>]</c>.</param>
    public abstract Rect GetCaretRect(int localPosition);

    /// <summary>
    /// The highlight rectangles for a local range, in the block's own coordinates.
    /// </summary>
    /// <remarks>
    /// More than one rectangle is normal and not an edge case: a range that wraps produces one
    /// per line, and a range crossing a bidi boundary produces disjoint rectangles on a single
    /// line. Increment 0 measured both.
    /// </remarks>
    /// <param name="from">The local start position.</param>
    /// <param name="to">The local end position.</param>
    public abstract IReadOnlyList<Rect> GetSelectionRects(int from, int to);

    /// <summary>
    /// The paragraph at a local position, or null if there is no editable text there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam between the editing layer and block nesting. Almost every editing command is
    /// written against one paragraph — insert here, walk a cluster, split, find the word around
    /// the caret — and it needs the paragraph the caret is actually in, which for a table is
    /// several levels down inside a block the flat run treats as one. Asking the block rather
    /// than assuming it <em>is</em> the paragraph is what lets a table be one slot without every
    /// command having to learn about cells.
    /// </para>
    /// <para>
    /// The default is null, which is the right answer for a rule and for an image: the caret can
    /// sit on them but there is no text to edit.
    /// </para>
    /// </remarks>
    /// <param name="localPosition">A local position in <c>[0, <see cref="ContentSize"/>]</c>.</param>
    public virtual TextAt? ParagraphAt(int localPosition) => null;

    /// <summary>
    /// The box an image at a local position is drawn in, or null if there is no image there.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="GetSelectionRects"/>, which for an inline image returns the
    /// <em>line</em> box: as tall as the line and reaching below the picture by the descent of the
    /// text beside it. Resize handles drawn on that rectangle float visibly off the image, so the
    /// block is asked where the picture itself is rather than where its position is.
    /// </remarks>
    /// <param name="localPosition">A local position in <c>[0, <see cref="ContentSize"/>]</c>.</param>
    public virtual Rect? GetImageRect(int localPosition) => null;
}

/// <summary>Where a paragraph found inside a block lives.</summary>
/// <param name="View">The paragraph's view.</param>
/// <param name="Start">
/// The paragraph's first inner position, relative to the containing block's content.
/// </param>
/// <param name="Origin">
/// The paragraph's top-left corner, relative to the containing block's top-left corner.
/// </param>
public readonly record struct TextAt(ParagraphView View, int Start, Point Origin);
