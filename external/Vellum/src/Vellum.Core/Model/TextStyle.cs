namespace Vellum;

/// <summary>
/// The boolean character styles, as a bit set.
/// </summary>
/// <remarks>
/// The values are pinned, because <see cref="MarkFields"/> mirrors them in its low bits so
/// that the two can be converted by a cast.
/// </remarks>
[Flags]
public enum TextStyle
{
    /// <summary>No style.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1 << 0,

    /// <summary>Italic.</summary>
    Italic = 1 << 1,

    /// <summary>Underline.</summary>
    Underline = 1 << 2,

    /// <summary>Strikethrough.</summary>
    Strikethrough = 1 << 3,

    /// <summary>Subscript. Mutually exclusive with <see cref="Super"/>.</summary>
    Sub = 1 << 4,

    /// <summary>Superscript. Mutually exclusive with <see cref="Sub"/>.</summary>
    Super = 1 << 5,

    /// <summary>Inline code.</summary>
    Code = 1 << 6,

    /// <summary>Every style bit.</summary>
    All = Bold | Italic | Underline | Strikethrough | Sub | Super | Code,
}
