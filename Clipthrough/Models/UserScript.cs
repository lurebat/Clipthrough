namespace Clipthrough.Models;

public sealed record UserScript
{
    public string Name { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;
}
