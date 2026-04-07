namespace Clipthrough.Models;

public sealed class SensitivityMatch
{
    public long RuleId { get; init; }

    public string RuleName { get; init; } = string.Empty;

    public string Severity { get; init; } = "warning";
}

