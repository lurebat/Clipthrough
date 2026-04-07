using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class ClipSearchResult
{
    public IReadOnlyList<ClipEntry> Items { get; init; } = Array.Empty<ClipEntry>();

    public int TotalMatchingCount { get; init; }

    public int TotalClipCount { get; init; }

    public int SensitiveClipCount { get; init; }

    public DateTimeOffset? LastCapturedAt { get; init; }
}

