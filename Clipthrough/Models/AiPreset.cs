namespace Clipthrough.Models;

public sealed record AiPreset
{
    public string Name { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
}
