namespace Clipthrough.Models;

/// <summary>
/// The .NET regex engine regex search runs on (OD-7, S38).
/// </summary>
public enum RegexSearchEngine
{
    /// <summary>
    /// Default. Linear time, so no pattern can hang a search; rejects
    /// backreferences, lookarounds and atomic groups.
    /// </summary>
    NonBacktracking,

    /// <summary>
    /// Supports every construct, bounded by a match timeout: a pattern that
    /// exceeds it is reported as too slow instead of running forever.
    /// </summary>
    Backtracking,
}
