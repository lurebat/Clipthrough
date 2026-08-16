using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Vellum.Avalonia;

/// <summary>
/// Turns a <see cref="MarkSet"/> into the <see cref="TextRunProperties"/> the text formatter
/// wants, per architecture 4.7.
/// </summary>
public interface ITextStyleResolver
{
    /// <summary>The properties used where a mark set overrides nothing.</summary>
    TextRunProperties Default { get; }

    /// <summary>Resolves the properties for a mark set.</summary>
    /// <param name="marks">The formatting to resolve.</param>
    TextRunProperties Resolve(MarkSet marks);
}

/// <summary>
/// The default <see cref="ITextStyleResolver"/>: a set of document-wide defaults that each
/// mark set overrides in part.
/// </summary>
/// <remarks>
/// <para>
/// Results are memoized per distinct mark set. A paragraph is usually a handful of distinct
/// formats repeated over many runs, and <see cref="MarkSet"/> is a value type with structural
/// equality precisely so this cache can be keyed on it directly.
/// </para>
/// <para>
/// The cache is unbounded, which is deliberate and safe: mark sets are drawn from a small
/// fixed vocabulary of styles, sizes, families and colours, so the number of distinct values a
/// real document produces is bounded by its formatting rather than by its length. An instance
/// belongs to one editor and dies with it.
/// </para>
/// <para>
/// This type is not thread-safe. It is owned by the view, which is single-threaded.
/// </para>
/// </remarks>
public sealed class TextStyleResolver : ITextStyleResolver
{
    /// <summary>
    /// How much smaller subscript and superscript are drawn, and how far they are shifted.
    /// Both are the usual typographic approximations; a real font's OpenType subscript and
    /// superscript metrics would be better, and are deliberately left for later.
    /// </summary>
    private const double ScriptScale = 0.66;

    /// <summary>
    /// The monospace stack used for <see cref="TextStyle.Code"/> when none is supplied.
    /// Avalonia resolves a comma-separated family list in order, so this names the usual
    /// Windows, macOS and Linux monospace faces and lets the platform pick.
    /// </summary>
    private static readonly FontFamily DefaultCodeFamily =
        new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private readonly Dictionary<MarkSet, TextRunProperties> _cache = [];
    private readonly Typeface _typeface;
    private readonly FontFamily _codeFamily;
    private readonly double _fontSize;
    private readonly IBrush _foreground;

    /// <summary>Creates a resolver over a set of document-wide defaults.</summary>
    /// <param name="typeface">The typeface used where a mark set names no family.</param>
    /// <param name="fontSize">The size used where a mark set names none. Must be positive and finite.</param>
    /// <param name="foreground">The colour used where a mark set names none.</param>
    /// <param name="codeFamily">
    /// The family used for <see cref="TextStyle.Code"/>, or null for a monospace stack. A mark
    /// set that names its own family overrides this.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fontSize"/> is not a positive finite number.</exception>
    public TextStyleResolver(
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        FontFamily? codeFamily = null)
    {
        ArgumentNullException.ThrowIfNull(foreground);

        if (fontSize <= 0 || !double.IsFinite(fontSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fontSize), fontSize, "Font size must be a positive finite number.");
        }

        _typeface = typeface;
        _codeFamily = codeFamily ?? DefaultCodeFamily;
        _fontSize = fontSize;
        _foreground = foreground;
        Default = Build(MarkSet.Empty);
    }

    /// <inheritdoc/>
    public TextRunProperties Default { get; }

    /// <inheritdoc/>
    public TextRunProperties Resolve(MarkSet marks)
    {
        if (marks.IsEmpty)
        {
            return Default;
        }

        if (_cache.TryGetValue(marks, out var cached))
        {
            return cached;
        }

        var built = Build(marks);

        _cache[marks] = built;

        return built;
    }

    private TextRunProperties Build(MarkSet marks)
    {
        // An explicit family is the mark set being specific on purpose, so it beats the code
        // default; the code family is only what Code falls back to.
        var family = marks.FontFamily is { } name
            ? new FontFamily(name)
            : marks.Has(TextStyle.Code)
                ? _codeFamily
                : _typeface.FontFamily;
        var weight = marks.Has(TextStyle.Bold) ? FontWeight.Bold : _typeface.Weight;
        var style = marks.Has(TextStyle.Italic) ? FontStyle.Italic : _typeface.Style;
        var size = marks.FontSize ?? _fontSize;

        var script = marks.Has(TextStyle.Sub)
            ? BaselineAlignment.Subscript
            : marks.Has(TextStyle.Super)
                ? BaselineAlignment.Superscript
                : BaselineAlignment.Baseline;

        if (script != BaselineAlignment.Baseline)
        {
            size *= ScriptScale;
        }

        return new GenericTextRunProperties(
            new Typeface(family, style, weight, _typeface.Stretch),
            fontRenderingEmSize: size,
            textDecorations: Decorations(marks),
            foregroundBrush: marks.Foreground is { } fg
                ? new ImmutableSolidColorBrush(fg.ToAvalonia())
                : _foreground,
            backgroundBrush: marks.Highlight is { } hl
                ? new ImmutableSolidColorBrush(hl.ToAvalonia())
                : null,
            baselineAlignment: script,
            cultureInfo: CultureInfo.CurrentCulture);
    }

    private static TextDecorationCollection? Decorations(MarkSet marks)
    {
        var underline = marks.Has(TextStyle.Underline) || marks.Link is not null;
        var strike = marks.Has(TextStyle.Strikethrough);

        if (!underline && !strike)
        {
            return null;
        }

        var decorations = new TextDecorationCollection();

        if (underline)
        {
            decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        }

        if (strike)
        {
            decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        }

        return decorations;
    }
}
