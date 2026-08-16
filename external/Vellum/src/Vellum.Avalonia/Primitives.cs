using AvColor = Avalonia.Media.Color;

namespace Vellum.Avalonia;

/// <summary>
/// Converts the two primitives <c>Vellum.Core</c> owns because it takes no dependency on
/// Avalonia, per architecture 4.1.
/// </summary>
/// <remarks>
/// The layouts are deliberately identical on both sides, so these are field-for-field copies
/// rather than translations. This is the only place in the product that knows both shapes.
/// </remarks>
public static class Primitives
{
    /// <summary>Converts a Vellum colour to an Avalonia one.</summary>
    /// <param name="colour">The colour to convert.</param>
    public static AvColor ToAvalonia(this Rgba colour) =>
        AvColor.FromArgb(colour.A, colour.R, colour.G, colour.B);

    /// <summary>Converts an Avalonia colour to a Vellum one.</summary>
    /// <param name="colour">The colour to convert.</param>
    public static Rgba ToVellum(this AvColor colour) =>
        new(colour.R, colour.G, colour.B, colour.A);

    /// <summary>Converts a Vellum span to an Avalonia one.</summary>
    /// <remarks>
    /// The two shapes agree on <c>Start</c>, <c>Length</c> and the value, which is all the
    /// conversion needs. They are not identical: Avalonia's span has no <c>End</c>, so callers
    /// that want one keep working in Vellum's span until the last moment.
    /// </remarks>
    /// <typeparam name="T">The attached value.</typeparam>
    /// <param name="span">The span to convert.</param>
    public static global::Avalonia.Utilities.ValueSpan<T> ToAvalonia<T>(this ValueSpan<T> span) =>
        new(span.Start, span.Length, span.Value);

    /// <summary>Converts an Avalonia span to a Vellum one.</summary>
    /// <typeparam name="T">The attached value.</typeparam>
    /// <param name="span">The span to convert.</param>
    public static ValueSpan<T> ToVellum<T>(this global::Avalonia.Utilities.ValueSpan<T> span) =>
        new(span.Start, span.Length, span.Value);
}
