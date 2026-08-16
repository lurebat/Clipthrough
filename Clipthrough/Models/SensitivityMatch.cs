namespace Clipthrough.Models;

public sealed class SensitivityMatch
{
    public long RuleId { get; init; }

    public string RuleName { get; init; } = string.Empty;

    /// <summary>
    /// The rule's regex. Carried on the match because a scan can report a rule the
    /// database has never seen - the service falls back to the in-memory defaults
    /// whenever the rules table cannot be read, and those carry no id. The store
    /// provisions the missing row from this, so it has to be the real pattern and
    /// not something reconstructed from the name.
    /// </summary>
    public string Pattern { get; init; } = string.Empty;

    public string Severity { get; init; } = "warning";
}

