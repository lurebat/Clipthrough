namespace Vellum;

/// <summary>
/// Selects which parts of a <see cref="MarkSet"/> an operation touches.
/// </summary>
/// <remarks>
/// <para>
/// The low bits mirror <see cref="TextStyle"/> exactly, so the style portion converts by a
/// cast. That is what lets applying and removing formatting be a single operation:
/// "make it bold" is <see cref="MarkSet.Apply"/> with a bold value and
/// <see cref="MarkFields.Bold"/>; "make it not bold" is the same call with an empty value.
/// </para>
/// <para>
/// The distinction between a field being *absent* from the selection and being present with
/// a null value is load-bearing. Absent means "leave whatever was there"; present-and-null
/// means "clear it".
/// </para>
/// </remarks>
[Flags]
public enum MarkFields
{
    /// <summary>Touch nothing.</summary>
    None = 0,

    /// <summary>The bold bit.</summary>
    Bold = TextStyle.Bold,

    /// <summary>The italic bit.</summary>
    Italic = TextStyle.Italic,

    /// <summary>The underline bit.</summary>
    Underline = TextStyle.Underline,

    /// <summary>The strikethrough bit.</summary>
    Strikethrough = TextStyle.Strikethrough,

    /// <summary>The subscript bit.</summary>
    Sub = TextStyle.Sub,

    /// <summary>The superscript bit.</summary>
    Super = TextStyle.Super,

    /// <summary>The inline-code bit.</summary>
    Code = TextStyle.Code,

    /// <summary>Every <see cref="TextStyle"/> bit.</summary>
    AllStyles = TextStyle.All,

    /// <summary><see cref="MarkSet.FontFamily"/>.</summary>
    FontFamily = 1 << 8,

    /// <summary><see cref="MarkSet.FontSize"/>.</summary>
    FontSize = 1 << 9,

    /// <summary><see cref="MarkSet.Foreground"/>.</summary>
    Foreground = 1 << 10,

    /// <summary><see cref="MarkSet.Highlight"/>.</summary>
    Highlight = 1 << 11,

    /// <summary><see cref="MarkSet.Link"/>.</summary>
    Link = 1 << 12,

    /// <summary>Every non-style field.</summary>
    AllScalars = FontFamily | FontSize | Foreground | Highlight | Link,

    /// <summary>Every field.</summary>
    All = AllStyles | AllScalars,
}

/// <summary>Conversions between <see cref="MarkFields"/> and <see cref="TextStyle"/>.</summary>
public static class MarkFieldsExtensions
{
    /// <summary>Extracts the style bits, discarding the scalar-field selectors.</summary>
    /// <param name="fields">The selection to convert.</param>
    public static TextStyle ToStyle(this MarkFields fields) =>
        (TextStyle)(fields & MarkFields.AllStyles);

    /// <summary>Widens style bits into a field selection.</summary>
    /// <param name="style">The styles to select.</param>
    public static MarkFields ToFields(this TextStyle style) => (MarkFields)style;

    /// <summary>Whether every bit of <paramref name="wanted"/> is present.</summary>
    /// <param name="fields">The selection to test.</param>
    /// <param name="wanted">The bits to look for.</param>
    public static bool Includes(this MarkFields fields, MarkFields wanted) =>
        (fields & wanted) == wanted;
}
