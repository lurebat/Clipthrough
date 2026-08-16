using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>
/// The three conversions a <see cref="RichTextToolbar"/> template needs and markup cannot do.
/// </summary>
/// <remarks>
/// A toolbar reports what the selection <em>is</em> — Heading2, Center, Ordered — and a button
/// asks whether that is the one thing it stands for. Avalonia has no comparison binding, so the
/// alternative to these is a boolean property per button on the control, which would put the
/// template's arrangement of buttons back into the control the template exists to keep it out of.
/// </remarks>
public static class ToolbarConverters
{
    /// <summary>Whether a value equals the converter parameter.</summary>
    /// <remarks>
    /// The parameter arrives from markup as a string, so the comparison is by name. That also
    /// makes null answer false rather than throwing, which is what a mixed selection is.
    /// </remarks>
    public static readonly IValueConverter Is = new IsConverter();

    /// <summary>A Vellum colour as a brush, or transparent where there is no colour.</summary>
    /// <remarks>
    /// No colour is not black. It means the text takes the colour it inherits, and a swatch that
    /// showed black for it would say the opposite of what is true.
    /// </remarks>
    public static readonly IValueConverter Swatch = new SwatchConverter();

    /// <summary>A font size in points, or the empty string where there is no one size.</summary>
    public static readonly IValueConverter Points = new PointsConverter();

    private sealed class IsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not null
            && parameter is not null
            && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            null;
    }

    private sealed class SwatchConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is Rgba colour
                ? new SolidColorBrush(colour.ToAvalonia())
                : Brushes.Transparent;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            null;
    }

    private sealed class PointsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double size ? size.ToString("0.#", culture) : string.Empty;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value?.ToString();
    }
}
