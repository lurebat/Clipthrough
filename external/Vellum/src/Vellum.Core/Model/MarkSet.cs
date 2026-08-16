using System.Text;

namespace Vellum;

/// <summary>
/// The complete character formatting of a run of text.
/// </summary>
/// <remarks>
/// <para>
/// A value type with structural equality, which is what makes mark-span normalization a
/// plain equality merge rather than a field-by-field comparison, and what lets the view
/// layer memoize one <c>TextRunProperties</c> per distinct mark set.
/// </para>
/// <para>
/// Every property validates on assignment, including through <c>with</c>, so an invalid mark
/// set cannot be constructed by any route.
/// </para>
/// </remarks>
public readonly record struct MarkSet
{
    private readonly TextStyle _style;
    private readonly string? _fontFamily;
    private readonly double? _fontSize;

    /// <summary>Creates a mark set.</summary>
    /// <param name="style">The boolean styles.</param>
    /// <param name="fontFamily">A font family name, or null to inherit. Blank is treated as null.</param>
    /// <param name="fontSize">A font size in device-independent pixels, or null to inherit.</param>
    /// <param name="foreground">A text colour, or null to inherit.</param>
    /// <param name="highlight">A background colour, or null for none.</param>
    /// <param name="link">A hyperlink, or null for none.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="style"/> has bits outside <see cref="TextStyle.All"/>, sets both
    /// <see cref="TextStyle.Sub"/> and <see cref="TextStyle.Super"/>, or
    /// <paramref name="fontSize"/> is not a positive finite number.
    /// </exception>
    public MarkSet(
        TextStyle style = TextStyle.None,
        string? fontFamily = null,
        double? fontSize = null,
        Rgba? foreground = null,
        Rgba? highlight = null,
        LinkMark? link = null)
    {
        Style = style;
        FontFamily = fontFamily;
        FontSize = fontSize;
        Foreground = foreground;
        Highlight = highlight;
        Link = link;
    }

    /// <summary>Formatting that inherits everything. Also <c>default(MarkSet)</c>.</summary>
    public static MarkSet Empty => default;

    /// <summary>The boolean styles.</summary>
    public TextStyle Style
    {
        get => _style;
        init
        {
            if ((value & ~TextStyle.All) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Unknown TextStyle bits.");
            }

            if (value.HasFlag(TextStyle.Sub | TextStyle.Super))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Text cannot be both subscript and superscript.");
            }

            _style = value;
        }
    }

    /// <summary>A font family name, or null to inherit. Blank input is stored as null.</summary>
    public string? FontFamily
    {
        get => _fontFamily;
        init => _fontFamily = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>A font size in device-independent pixels, or null to inherit.</summary>
    public double? FontSize
    {
        get => _fontSize;
        init
        {
            if (value is { } size && (size <= 0 || !double.IsFinite(size)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Font size must be a positive finite number.");
            }

            _fontSize = value;
        }
    }

    /// <summary>A text colour, or null to inherit.</summary>
    public Rgba? Foreground { get; init; }

    /// <summary>A background colour, or null for none.</summary>
    public Rgba? Highlight { get; init; }

    /// <summary>A hyperlink, or null for none.</summary>
    public LinkMark? Link { get; init; }

    /// <summary>Whether this mark set inherits everything and overrides nothing.</summary>
    public bool IsEmpty => this == default;

    /// <summary>Whether all of <paramref name="style"/>'s bits are set.</summary>
    /// <param name="style">The styles to look for.</param>
    public bool Has(TextStyle style) => (Style & style) == style;

    /// <summary>
    /// Returns this mark set with the selected fields taken from <paramref name="value"/>,
    /// leaving unselected fields alone.
    /// </summary>
    /// <remarks>
    /// This is the single primitive behind both applying and removing formatting. Turning
    /// bold on is <c>Apply(new MarkSet(TextStyle.Bold), MarkFields.Bold)</c>; turning it off
    /// is <c>Apply(MarkSet.Empty, MarkFields.Bold)</c>. Removing a mark is therefore not a
    /// separate code path that can drift out of step with applying one.
    /// </remarks>
    /// <param name="value">Supplies the new values for the selected fields.</param>
    /// <param name="fields">Which fields to take from <paramref name="value"/>.</param>
    public MarkSet Apply(MarkSet value, MarkFields fields)
    {
        if (fields == MarkFields.None)
        {
            return this;
        }

        var selected = fields.ToStyle();
        var style = (Style & ~selected) | (value.Style & selected);

        // Sub and Super cannot coexist, and a selection that turns one on without
        // mentioning the other would otherwise produce that invalid combination.
        if (style.HasFlag(TextStyle.Sub | TextStyle.Super))
        {
            style &= selected.HasFlag(TextStyle.Super) ? ~TextStyle.Sub : ~TextStyle.Super;
        }

        return new MarkSet
        {
            Style = style,
            FontFamily = fields.Includes(MarkFields.FontFamily) ? value.FontFamily : FontFamily,
            FontSize = fields.Includes(MarkFields.FontSize) ? value.FontSize : FontSize,
            Foreground = fields.Includes(MarkFields.Foreground) ? value.Foreground : Foreground,
            Highlight = fields.Includes(MarkFields.Highlight) ? value.Highlight : Highlight,
            Link = fields.Includes(MarkFields.Link) ? value.Link : Link,
        };
    }

    /// <summary>Returns this mark set with the selected fields cleared.</summary>
    /// <param name="fields">Which fields to clear.</param>
    public MarkSet Remove(MarkFields fields) => Apply(Empty, fields);

    /// <summary>
    /// Which fields differ between this mark set and <paramref name="other"/>. Useful for
    /// deriving the minimal <see cref="MarkFields"/> an operation needs to touch.
    /// </summary>
    /// <param name="other">The mark set to compare against.</param>
    public MarkFields DifferenceFrom(MarkSet other)
    {
        var fields = (Style ^ other.Style).ToFields();

        if (FontFamily != other.FontFamily)
        {
            fields |= MarkFields.FontFamily;
        }

        if (FontSize != other.FontSize)
        {
            fields |= MarkFields.FontSize;
        }

        if (Foreground != other.Foreground)
        {
            fields |= MarkFields.Foreground;
        }

        if (Highlight != other.Highlight)
        {
            fields |= MarkFields.Highlight;
        }

        if (Link != other.Link)
        {
            fields |= MarkFields.Link;
        }

        return fields;
    }

    /// <summary>Lists only the fields that override something, or <c>(empty)</c>.</summary>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(empty)";
        }

        var parts = new StringBuilder();

        void Add(string part)
        {
            if (parts.Length > 0)
            {
                parts.Append(' ');
            }

            parts.Append(part);
        }

        if (Style != TextStyle.None)
        {
            Add(Style.ToString());
        }

        if (FontFamily is not null)
        {
            Add($"family={FontFamily}");
        }

        if (FontSize is { } size)
        {
            Add($"size={size}");
        }

        if (Foreground is { } fg)
        {
            Add($"fg={fg}");
        }

        if (Highlight is { } hl)
        {
            Add($"hl={hl}");
        }

        if (Link is { } link)
        {
            Add($"link={link}");
        }

        return parts.ToString();
    }
}
