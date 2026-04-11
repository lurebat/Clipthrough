namespace Clipthrough.Models;

public sealed class ClipSearchFilters
{
    public string SearchText { get; init; } = string.Empty;

    public ContentType? ContentType { get; init; }

    public bool FavoritesOnly { get; init; }

    public bool SensitiveOnly { get; init; }

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

