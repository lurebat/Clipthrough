using System.Linq;

namespace Clipthrough.Models;

/// <summary>
/// Decides in memory whether a clip satisfies the *structural* part of a set of
/// search filters - the content-type set and the three boolean toggles. Search
/// text is deliberately out of scope: matching it here would mean reproducing
/// FTS tokenisation, regex, wildcard, whole-word and fuzzy matching, which is
/// not something a caller can do correctly.
///
/// This exists so the UI can answer "would this newly captured clip appear in
/// the list the user is currently looking at?" without a database round trip.
/// It is a second expression of the rules in
/// <c>ClipStoreService.BuildWhereClauses</c>, which is a cost worth paying only
/// because this subset is small, closed and exactly mirrorable - unlike the
/// ordering rules, where SQLite's BINARY collation and .NET string comparison
/// genuinely disagree. <c>BuildWhereClauses</c> carries a pointer back here;
/// keep the two in step, and note that
/// <c>ClipStructuralFilterTests</c> asserts they agree by round-tripping the
/// same clips through SQLite.
/// </summary>
public static class ClipStructuralFilter
{
    /// <summary>
    /// True when <paramref name="clip"/> passes every structural filter in
    /// <paramref name="filters"/>. Callers must separately establish that the
    /// filters carry no search text before treating this as "the clip belongs
    /// in the current result set".
    /// </summary>
    public static bool Matches(ClipSearchFilters filters, ClipEntry clip)
    {
        if (filters.ContentTypes is { Count: > 0 } types && !types.Contains(clip.ContentType))
        {
            return false;
        }

        if (filters.FavoritesOnly && !clip.IsFavorite)
        {
            return false;
        }

        if (filters.SensitiveOnly && !clip.IsSensitive)
        {
            return false;
        }

        if (filters.PastedOnly && !clip.IsPasted)
        {
            return false;
        }

        return true;
    }
}
