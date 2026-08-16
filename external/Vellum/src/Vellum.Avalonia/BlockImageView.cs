using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Vellum.Avalonia;

/// <summary>The view of an image that stands on its own rather than sitting in a line of text.</summary>
/// <remarks>
/// <para>
/// A leaf, like <see cref="RuleView"/>: the image occupies one position and has no inside, so
/// there is nothing in it for a caret to be in.
/// </para>
/// <para>
/// It draws through the same <see cref="IEmbedRenderer"/> an inline image goes through, so
/// whatever a host does about loading and caching pixels applies to both without being written
/// twice. Against the default renderer that means an outlined box of the right size rather than
/// the picture.
/// </para>
/// </remarks>
public sealed class BlockImageView : BlockView
{
    private readonly BlockImageNode _node;
    private readonly IEmbedRenderer _renderer;
    private readonly TextRunProperties _properties;
    private Size _size;

    /// <summary>Creates the view of a block image.</summary>
    /// <param name="node">The block.</param>
    /// <param name="renderer">The renderer that measures and draws the image.</param>
    /// <param name="properties">The surrounding text properties, for a default size.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public BlockImageView(BlockImageNode node, IEmbedRenderer renderer, TextRunProperties properties)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(properties);

        _node = node;
        _renderer = renderer;
        _properties = properties;
    }

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

        var asked = _renderer.Measure(_node.Image, _properties)
            ?? new Size(_properties.FontRenderingEmSize, _properties.FontRenderingEmSize);

        // An image wider than the column is boxed into it rather than overflowing, and the height
        // goes with it so the picture keeps its shape.
        var limit = double.IsInfinity(availableWidth) ? asked.Width : Math.Max(0, availableWidth);
        var scale = asked.Width > limit && asked.Width > 0 ? limit / asked.Width : 1;

        return _size = new Size(asked.Width * scale, asked.Height * scale);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);

        _renderer.Draw(context, new Rect(origin, _size), _node.Image);
    }

    /// <inheritdoc/>
    public override int HitTest(Point local) => 0;

    /// <inheritdoc/>
    public override Rect GetCaretRect(int localPosition) => new(0, 0, 0, _size.Height);

    /// <inheritdoc/>
    /// <remarks>
    /// The whole block: a block image is the picture and nothing else. Note that
    /// <see cref="Measure"/> boxes an over-wide image into the column, so this is the size the
    /// image is <em>drawn</em> at, which is what a resize handle has to be placed on.
    /// </remarks>
    public override Rect? GetImageRect(int localPosition) =>
        localPosition == 0 ? new Rect(default, _size) : null;

    /// <inheritdoc/>
    public override IReadOnlyList<Rect> GetSelectionRects(int from, int to) => [];
}
