namespace Clipthrough.Models;

public sealed class ClipSearchFilters
{
    public string SearchText { get; init; } = string.Empty;

    /// <summary>
    /// Filter clips to one of these content types. <c>null</c> or an empty
    /// collection means "no filter — include every content type".
    /// </summary>
    public System.Collections.Generic.IReadOnlyCollection<ContentType>? ContentTypes { get; init; }

    public bool FavoritesOnly { get; init; }

    public bool SensitiveOnly { get; init; }

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public bool UseWildcard { get; init; }

    public bool WholeWord { get; init; }

    public bool PastedOnly { get; init; }

    public bool UseFuzzy { get; init; }

    public ClipSortOption SortOption { get; init; } = ClipSortOption.MostRecent;

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

