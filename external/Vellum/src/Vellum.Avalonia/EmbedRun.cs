using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Vellum.Avalonia;

/// <summary>
/// One <see cref="InlineEmbed"/> as a run the text formatter can lay out.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Length"/> is 1 and the model reserves exactly one
/// <see cref="InlineContent.Placeholder"/> per embed, so a position in the formatter's index
/// space and a position in the model differ by a single additive block offset. There is no
/// index translation table, and that is the whole reason the placeholder exists.
/// </para>
/// <para>
/// <b>Properties must be overridden.</b> <c>TextRun.Properties</c> is virtual and defaults to
/// null. A run that leaves it null formats successfully and only throws when drawn, as
/// <c>ArgumentOutOfRangeException (Parameter 'baselineAlignment')</c> from deep inside the
/// text stack - a failure that surfaces at paint time, far from its cause. Measured in
/// Increment 0; see docs/increment-0-findings.md section 1.
/// </para>
/// </remarks>
public sealed class EmbedRun : DrawableTextRun
{
    /// <summary>Size used for an embed that does not say how big it is.</summary>
    private const double IntrinsicFallback = 16;

    private readonly IEmbedRenderer _renderer;

    /// <summary>Creates a run for an embed.</summary>
    /// <param name="embed">The embed to lay out.</param>
    /// <param name="properties">
    /// The properties of the surrounding text. Must not be null; see the type remarks.
    /// </param>
    /// <param name="renderer">Draws the embed when the line is painted.</param>
    public EmbedRun(InlineEmbed embed, TextRunProperties properties, IEmbedRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(embed);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(renderer);

        Embed = embed;
        Properties = properties;
        _renderer = renderer;
        Size = renderer.Measure(embed, properties) is { } size && IsUsable(size)
            ? size
            : new Size(IntrinsicFallback, IntrinsicFallback);

        // Sitting the box on the baseline is what Increment 0 measured as the alignment that
        // behaves; Top and TextTop are displaced by Avalonia's own baseline formula.
        Baseline = Size.Height;
    }

    /// <summary>The embed this run draws.</summary>
    public InlineEmbed Embed { get; }

    /// <inheritdoc/>
    public override TextRunProperties Properties { get; }

    /// <inheritdoc/>
    public override Size Size { get; }

    /// <inheritdoc/>
    public override double Baseline { get; }

    /// <summary>
    /// One, matching the single <see cref="InlineContent.Placeholder"/> the model reserves.
    /// </summary>
    public override int Length => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="origin"/> is the top-left of the run's box, with <see cref="Baseline"/>
    /// measured downward from it. Measured in Increment 0.
    /// </remarks>
    public override void Draw(DrawingContext drawingContext, Point origin) =>
        _renderer.Draw(drawingContext, new Rect(origin, Size), Embed);

    private static bool IsUsable(Size size) =>
        size.Width > 0 && size.Height > 0 &&
        double.IsFinite(size.Width) && double.IsFinite(size.Height);
}

/// <summary>
/// Measures and draws inline embeds on behalf of <see cref="EmbedRun"/>.
/// </summary>
/// <remarks>
/// Kept behind an interface because resolving an <see cref="ImageEmbed"/> to actual pixels
/// means asset loading, caching and invalidation, which is Increment 4's problem rather than
/// the text source's.
/// </remarks>
public interface IEmbedRenderer
{
    /// <summary>
    /// Measures an embed, or returns null to accept a default box.
    /// </summary>
    /// <param name="embed">The embed to measure.</param>
    /// <param name="properties">The properties of the surrounding text.</param>
    Size? Measure(InlineEmbed embed, TextRunProperties properties);

    /// <summary>Draws an embed into the box the formatter placed it in.</summary>
    /// <param name="context">The drawing context.</param>
    /// <param name="bounds">Where to draw.</param>
    /// <param name="embed">The embed to draw.</param>
    void Draw(DrawingContext context, Rect bounds, InlineEmbed embed);
}

/// <summary>
/// The stand-in renderer: a box the size the embed asks for, outlined so it is visible.
/// </summary>
/// <remarks>
/// Deliberately does not load images. Increment 2 is about editing a paragraph, and an embed's
/// only job here is to occupy one position and take part in line breaking correctly. Real
/// image rendering arrives with Increment 4.
/// </remarks>
public sealed class PlaceholderEmbedRenderer : IEmbedRenderer
{
    private static readonly IPen Outline = new ImmutablePen(Brushes.Gray, 1);

    /// <inheritdoc/>
    public Size? Measure(InlineEmbed embed, TextRunProperties properties)
    {
        ArgumentNullException.ThrowIfNull(embed);
        ArgumentNullException.ThrowIfNull(properties);

        if (embed is not ImageEmbed image)
        {
            return null;
        }

        // A dimension the embed does not give is squared off against the one it does, so an
        // image with only a width still occupies a sensible box.
        var side = properties.FontRenderingEmSize;

        return new Size(image.Width ?? image.Height ?? side, image.Height ?? image.Width ?? side);
    }

    /// <inheritdoc/>
    public void Draw(DrawingContext context, Rect bounds, InlineEmbed embed)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.DrawRectangle(null, Outline, bounds);
    }
}
