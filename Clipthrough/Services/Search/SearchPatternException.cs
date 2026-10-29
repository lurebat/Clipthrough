using System;

namespace Clipthrough.Services.Search;

public enum SearchPatternProblem
{
    /// <summary>The pattern uses a construct only the backtracking engine supports.</summary>
    NeedsBacktracking,

    /// <summary>The backtracking engine hit its match timeout on this pattern.</summary>
    TooSlow,
}

/// <summary>
/// A regex search that could not run to a trustworthy answer. Thrown by the
/// store instead of returning a partial or empty result, so the caller keeps
/// the results it already shows and tells the user why.
/// </summary>
public sealed class SearchPatternException : Exception
{
    public SearchPatternException()
    {
    }

    public SearchPatternException(string message)
        : base(message)
    {
    }

    public SearchPatternException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SearchPatternException(SearchPatternProblem problem, Exception innerException)
        : base(innerException.Message, innerException)
    {
        Problem = problem;
    }

    public SearchPatternProblem Problem { get; }
}
