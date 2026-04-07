namespace Clipthrough.Models;

public sealed class SensitivityRule
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Pattern { get; init; } = string.Empty;

    public string Severity { get; init; } = "warning";

    public bool IsEnabled { get; init; } = true;

    public bool IsBuiltIn { get; init; } = true;
}

