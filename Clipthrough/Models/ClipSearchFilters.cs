namespace Clipthrough.Models;

public sealed class ClipSearchFilters
{
    public string SearchText { get; init; } = string.Empty;

    public ContentType? ContentType { get; init; }

    public bool FavoritesOnly { get; init; }

    public bool SensitiveOnly { get; init; }

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public bool UseWildcard { get; init; }

    public bool WholeWord { get; init; }

    public bool PastedOnly { get; init; }

    public bool UseFuzzy { get; init; }

    public ClipSortOption SortOption { get; init; } = ClipSortOption.MostRecent;

    /// <summary>
    /// When true and a search query is active, sort by the selected <see cref="SortOption"/>
    /// (e.g. most-recent) instead of by FTS relevance (bm25).
    /// </summary>
    public bool SearchSortByDate { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

