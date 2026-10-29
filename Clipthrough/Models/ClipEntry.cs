using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class ClipEntry
{
    public long Id { get; init; }

    private readonly string _content = string.Empty;

    /// <summary>
    /// The clip's full text. A list or search row that carries only the first
    /// <see cref="ListContentLength"/> characters (<see cref="IsContentTruncated"/>)
    /// throws here instead of returning them: copying, exporting, transforming or
    /// dragging a prefix would silently hand the user part of their clip. Hydrate
    /// the row first (ClipItemViewModel.EnsureContentHydratedAsync); display code
    /// reads <see cref="ContentPrefix"/>.
    /// </summary>
    public string Content
    {
        get => IsContentTruncated
            ? throw new InvalidOperationException(
                $"Clip {Id} is a list row holding only the first {ListContentLength} characters of its text; hydrate it before reading Content.")
            : _content;
        init => _content = value ?? string.Empty;
    }

    /// <summary>
    /// How much text a list or search row carries (F-10). A row never shows more
    /// than a title and a snippet, and the formatter decides those from this
    /// many characters (ClipDisplayFormatter.DisplayPrefixLength).
    /// </summary>
    public const int ListContentLength = 4096;

    /// <summary>True when this entry came from a list read and its text is longer than <see cref="ListContentLength"/>.</summary>
    public bool IsContentTruncated { get; init; }

    /// <summary>
    /// The text as far as this entry carries it: all of it, or the first
    /// <see cref="ListContentLength"/> characters of a truncated list row. For
    /// display only - never for anything that writes, copies or transforms it.
    /// </summary>
    public string ContentPrefix => _content;

    public byte[]? ContentBytes { get; init; }

    public ContentType ContentType { get; init; } = ContentType.Text;

    public ClipContentFormat ContentFormat { get; init; } = ClipContentFormat.PlainText;

    public string? SourceApp { get; init; }

    public string? SourceAppPath { get; init; }

    public byte[]? SourceAppIconBytes { get; init; }

    /// <summary>
    /// True when the DB row has a non-empty source_app_icon blob. Populated even
    /// when <see cref="SourceAppIconBytes"/> is <c>null</c> (e.g. metadata-only list reads).
    /// </summary>
    public bool SourceAppIconAvailable { get; init; }

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

