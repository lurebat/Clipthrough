namespace Clipthrough.Models;

public sealed record AiMenuEntry(string Label, AiPreset? Preset, bool IsCustomPrompt, AiPresetKind Kind = AiPresetKind.TextToText);
