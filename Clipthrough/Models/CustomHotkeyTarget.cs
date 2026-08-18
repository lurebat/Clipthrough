using System;

namespace Clipthrough.Models;

/// <summary>
/// What a custom hotkey does, parsed from its <c>Target</c> string.
/// </summary>
public enum CustomHotkeyKind
{
    /// <summary>The target did not parse and the hotkey does nothing.</summary>
    Unknown,

    /// <summary>A built-in <see cref="TextTransformation"/>, named by its enum member.</summary>
    BuiltIn,

    /// <summary>A saved AI preset, named by its preset name.</summary>
    AiPreset,

    /// <summary>A one-off system prompt carried inline, with no preset to save.</summary>
    InlinePrompt,

    /// <summary>Opens the AI prompt dialog; needs no clip and produces nothing to paste.</summary>
    AiPromptDialog,
}

/// <summary>
/// The <c>kind:value</c> target of a custom hotkey binding.
/// </summary>
/// <remarks>
/// Parsed here rather than inline in <c>App.ExecuteCustomHotkey</c> because a
/// target that does not parse is silent by nature: the hotkey is registered with
/// the OS, the key press is swallowed, and nothing happens. The rule was
/// documented in AGENTS.md and implemented in a 137-line <c>async void</c>, with
/// no test anywhere - and the documentation had already drifted, listing three
/// kinds where the code accepts four.
/// </remarks>
/// <param name="Token">
/// The kind exactly as it is written in the target, lowercased. Kept because it
/// is persisted: a transformed clip records <c>TransformKind</c> as
/// <c>token:value</c>, so deriving it from <see cref="Kind"/> instead would
/// silently change what is written into the database.
/// </param>
public readonly record struct CustomHotkeyTarget(CustomHotkeyKind Kind, string Token, string Value)
{
    /// <summary>
    /// Splits a target at its first colon. The value keeps any later colons,
    /// which matters for <see cref="CustomHotkeyKind.InlinePrompt"/>: a prompt is
    /// free text and routinely contains one.
    /// </summary>
    public static CustomHotkeyTarget Parse(string? target)
    {
        var text = target ?? string.Empty;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return new CustomHotkeyTarget(CustomHotkeyKind.Unknown, string.Empty, string.Empty);
        }

        var kind = text[..colon].Trim().ToLowerInvariant();
        var value = text[(colon + 1)..].Trim();

        return kind switch
        {
            "builtin" => new CustomHotkeyTarget(CustomHotkeyKind.BuiltIn, kind, value),
            "ai" => new CustomHotkeyTarget(CustomHotkeyKind.AiPreset, kind, value),
            "prompt" => new CustomHotkeyTarget(CustomHotkeyKind.InlinePrompt, kind, value),
            "aiprompt" => new CustomHotkeyTarget(CustomHotkeyKind.AiPromptDialog, kind, value),
            _ => new CustomHotkeyTarget(CustomHotkeyKind.Unknown, kind, value),
        };
    }
}
