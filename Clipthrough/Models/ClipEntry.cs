using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class ClipEntry
{
    public long Id { get; init; }

    public string Content { get; init; } = string.Empty;

    public byte[]? ContentBytes { get; init; }

    public ContentType ContentType { get; init; } = ContentType.Text;

    public ClipContentFormat ContentFormat { get; init; } = ClipContentFormat.PlainText;

    public string? SourceApp { get; init; }

    public string? SourceAppPath { get; init; }

    public byte[]? SourceAppIconBytes { get; init; }

    public string Hash { get; init; } = string.Empty;

    public bool IsFavorite { get; set; }

    public bool IsSensitive { get; init; }

    public int CopyCount { get; init; } = 1;

    public DateTimeOffset FirstCopiedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastCopiedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CapturedAt => LastCopiedAt;

    public long ByteSize { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    public string? SourceWindowTitle { get; init; }

    public string? SourceUrl { get; init; }

    public bool IsPasted { get; set; }

    public int PasteCount { get; set; }

    public DateTimeOffset? LastPastedAt { get; set; }

    public DateTimeOffset? PinnedAt { get; set; }

    public bool IsPinned => PinnedAt.HasValue;

    public string? OcrText { get; set; }

    public string? OcrStatus { get; set; }

    public DateTimeOffset? OcrAttemptedAt { get; set; }

    public string? OcrError { get; set; }

    public long? SourceClipId { get; init; }

    public string? TransformKind { get; init; }

    /// <summary>
    /// Marks how the clip entered Clipthrough. NULL = clipboard capture
    /// (default); "drag_drop" = imported via the popup's drag-and-drop
    /// surface. Surfaced as a small badge in the UI.
    /// </summary>
    public string? ImportKind { get; init; }

    public IReadOnlyList<SensitivityMatch> SensitivityMatches { get; set; } = Array.Empty<SensitivityMatch>();
}

