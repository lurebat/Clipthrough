namespace Vellum;

/// <summary>
/// An object anchored at a single inline position, represented in the text by
/// <see cref="InlineContent.Placeholder"/>.
/// </summary>
public abstract record InlineEmbed
{
    /// <summary>Text used when the embed is exported to a plain-text format.</summary>
    public abstract string PlainTextFallback { get; }
}

/// <summary>An image placed in the flow of text.</summary>
public sealed record ImageEmbed : InlineEmbed
{
    private readonly double? _width;
    private readonly double? _height;

    /// <summary>Creates an inline image.</summary>
    /// <param name="source">Where the image comes from. Must not be blank.</param>
    /// <param name="width">Display width in device-independent pixels, or null for intrinsic.</param>
    /// <param name="height">Display height in device-independent pixels, or null for intrinsic.</param>
    /// <param name="altText">Alternative text for accessibility and plain-text export.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not a positive finite number.</exception>
    public ImageEmbed(string source, double? width = null, double? height = null, string? altText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Source = source;
        Width = width;
        Height = height;
        AltText = string.IsNullOrWhiteSpace(altText) ? null : altText;
    }

    /// <summary>Where the image comes from.</summary>
    public string Source { get; }

    /// <summary>Display width in device-independent pixels, or null for the intrinsic width.</summary>
    public double? Width
    {
        get => _width;
        init => _width = Checked(value);
    }

    /// <summary>Display height in device-independent pixels, or null for the intrinsic height.</summary>
    public double? Height
    {
        get => _height;
        init => _height = Checked(value);
    }

    /// <summary>Alternative text, or null.</summary>
    public string? AltText { get; init; }

    /// <inheritdoc/>
    public override string PlainTextFallback => AltText ?? string.Empty;

    private static double? Checked(double? value) =>
        value is { } v && (v <= 0 || !double.IsFinite(v))
            ? throw new ArgumentOutOfRangeException(
                nameof(value), value, "An image dimension must be a positive finite number.")
            : value;
}
