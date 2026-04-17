namespace Clipthrough.Models;

public sealed class ClipCaptureRequest
{
    public byte[] ContentBytes { get; init; } = [];

    public string? ContentText { get; init; }

    public ContentType ContentType { get; init; } = ContentType.Text;

    public ClipContentFormat ContentFormat { get; init; } = ClipContentFormat.PlainText;

    public string? SourceApp { get; init; }

    public string? SourceAppPath { get; init; }

    public byte[]? SourceAppIconBytes { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    public string? SourceWindowTitle { get; init; }

    public string? SourceUrl { get; init; }

    public bool IsFavorite { get; init; }

    public bool IncrementExistingCopyCount { get; init; } = true;

    public long? SourceClipId { get; init; }

    public string? TransformKind { get; init; }

    public System.DateTimeOffset? CapturedAtOverride { get; init; }
}
