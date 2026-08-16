using Avalonia;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>The view of a horizontal rule.</summary>
/// <remarks>
/// A rule is a leaf: it has no inside, so it has no position a caret can occupy and
/// <see cref="ContentSize"/> is zero. Everything asked of it in local positions therefore has
/// exactly one answer, and the interesting question — what happens when a selection covers it —
/// belongs to the host, which is the only thing that knows a selection reaches past this block
/// on both sides.
/// </remarks>
public sealed class RuleView : BlockView
{
    private readonly IBrush _brush;
    private Size _size;

    /// <summary>Creates the view of a rule.</summary>
    /// <param name="brush">The line's colour.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    public RuleView(IBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        _brush = brush;
    }

    /// <summary>The height a rule takes, line and the air around it together.</summary>
    public const double BlockHeight = 13;

    /// <summary>The thickness of the line itself.</summary>
    public const double LineThickness = 1;

    /// <inheritdoc/>
    public override int ContentSize => 0;

    /// <inheritdoc/>
    public override Size Size => _size;

    /// <inheritdoc/>
    public override Size Measure(double availableWidth)
    {
        if (double.IsNaN(availableWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableWidth), availableWidth, "A width to lay out to must be a number.");
        }

        // A rule spans whatever it is given rather than asking for room, so an unconstrained
        // measure would otherwise ask for an infinite width and take the layout with it.
        var width = double.IsInfinity(availableWidth) ? 0 : Math.Max(0, availableWidth);

        return _size = new Size(width, BlockHeight);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.FillRectangle(
            _brush,
            new Rect(
                origin.X,
                origin.Y + ((BlockHeight - LineThickness) / 2),
                _size.Width,
                LineThickness));
    }

    /// <inheritdoc/>
    public override int HitTest(Point local) => 0;

    /// <inheritdoc/>
    public override Rect GetCaretRect(int localPosition) =>
        new(0, 0, 0, BlockHeight);

    /// <inheritdoc/>
    public override IReadOnlyList<Rect> GetSelectionRects(int from, int to) => [];
}
