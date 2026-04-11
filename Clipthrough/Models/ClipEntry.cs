using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class ClipEntry
{
    public long Id { get; init; }

    public string Content { get; init; } = string.Empty;

    public byte[]? ContentBytes { get; init; }

    public ContentType ContentType { get; init; } = ContentType.Text;

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

    public IReadOnlyList<SensitivityMatch> SensitivityMatches { get; set; } = Array.Empty<SensitivityMatch>();
}

