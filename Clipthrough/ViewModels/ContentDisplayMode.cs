namespace Clipthrough.ViewModels;

/// <summary>
/// Controls how rich text clip content is displayed.
/// </summary>
public enum ContentDisplayMode
{
    /// <summary>Rendered HTML/RTF view.</summary>
    Rendered,

    /// <summary>Extracted plain text in the text editor.</summary>
    Textual,

    /// <summary>Raw HTML/RTF source in the text editor with syntax highlighting.</summary>
    Raw,
}
