namespace Clipthrough.ViewModels;

/// <summary>
/// Controls how an image clip is displayed in the right pane.
/// </summary>
public enum ImageViewMode
{
    /// <summary>Lightweight static preview of the image bytes.</summary>
    Preview,

    /// <summary>Embedded ShareX image editor with cropping, annotations, etc.</summary>
    Editor,

    /// <summary>OCR-extracted text in a plain text editor.</summary>
    Text,
}
