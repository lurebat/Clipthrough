using Clipthrough.Localization;
namespace Clipthrough.Models;

public sealed class ClipSortOptionItem
{
    public ClipSortOptionItem(ClipSortOption value)
    {
        Value = value;
    }

    public ClipSortOption Value { get; }

    public string Label => Value switch
    {
        ClipSortOption.MostRecent => AppText.SortMostRecentLabel,
        ClipSortOption.OldestFirst => AppText.SortOldestFirstLabel,
        ClipSortOption.MostPasted => AppText.SortMostPastedLabel,
        ClipSortOption.Alphabetical => AppText.SortAlphabeticalLabel,
        ClipSortOption.LargestFirst => AppText.SortLargestFirstLabel,
        _ => Value.ToString(),
    };

    public override string ToString() => Label;
}
