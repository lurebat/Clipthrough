using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class ClipEntry
{
    public long Id { get; init; }

    public string Content { get; init; } = string.Empty;

    public ContentType ContentType { get; init; } = ContentType.Text;

    public string? SourceApp { get; init; }

    public string Hash { get; init; } = string.Empty;

    public bool IsFavorite { get; set; }

    public bool IsSensitive { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    public long ByteSize { get; init; }

    public IReadOnlyList<SensitivityMatch> SensitivityMatches { get; set; } = Array.Empty<SensitivityMatch>();
}

