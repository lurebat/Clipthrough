namespace Vellum;

/// <summary>
/// A straight (non-premultiplied) 8-bit-per-channel sRGB colour with alpha.
/// </summary>
/// <remarks>
/// <para>
/// Vellum.Core takes no dependency on Avalonia, so it cannot use <c>Avalonia.Media.Color</c>.
/// The layouts are deliberately identical in channel order and range, which keeps the
/// conversion in Vellum.Avalonia a field-for-field copy.
/// </para>
/// <para>
/// This is a value type with structural equality, which is what lets a mark set be compared
/// cheaply when normalizing mark spans.
/// </para>
/// </remarks>
/// <param name="R">The red channel, 0-255.</param>
/// <param name="G">The green channel, 0-255.</param>
/// <param name="B">The blue channel, 0-255.</param>
/// <param name="A">The alpha channel, 0 (transparent) to 255 (opaque).</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>Fully transparent. This is also <c>default(Rgba)</c>.</summary>
    public static Rgba Transparent => default;

    /// <summary>Opaque black.</summary>
    public static Rgba Black => new(0, 0, 0, 255);

    /// <summary>Opaque white.</summary>
    public static Rgba White => new(255, 255, 255, 255);

    /// <summary>Creates an opaque colour.</summary>
    /// <param name="r">The red channel, 0-255.</param>
    /// <param name="g">The green channel, 0-255.</param>
    /// <param name="b">The blue channel, 0-255.</param>
    public static Rgba FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>Creates a colour from a packed <c>0xAARRGGBB</c> value.</summary>
    /// <param name="argb">The packed value, alpha in the most significant byte.</param>
    public static Rgba FromArgb(uint argb) => new(
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb,
        (byte)(argb >> 24));

    /// <summary>Packs this colour into <c>0xAARRGGBB</c>.</summary>
    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>Returns this colour with a different alpha channel.</summary>
    /// <param name="alpha">The replacement alpha, 0-255.</param>
    public Rgba WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>Whether the colour is fully transparent, regardless of its RGB channels.</summary>
    public bool IsTransparent => A == 0;

    /// <summary>
    /// Formats as <c>#AARRGGBB</c>. Documents are compared byte-exactly in tests, so a
    /// legible colour is worth the small cost when a comparison fails.
    /// </summary>
    public override string ToString() => $"#{ToArgb():X8}";
}
