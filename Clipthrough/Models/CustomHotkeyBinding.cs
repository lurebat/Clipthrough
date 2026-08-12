using System;

namespace Clipthrough.Models;

public sealed record CustomHotkeyBinding
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public string Gesture { get; init; } = string.Empty;

    /// <summary>
    /// Target identifier in the form:
    ///   "builtin:&lt;TextTransformation enum name&gt;"
    ///   "ai:&lt;AI preset name&gt;"
    ///   "prompt:&lt;free-form AI prompt text&gt;"
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// If true, after copying the transformed text the app simulates Ctrl+V so it
    /// pastes into the foreground window. If false, the transformed text is just
    /// placed on the clipboard.
    /// </summary>
    public bool PasteAfter { get; init; } = true;

    /// <summary>
    /// If true, the hotkey is registered system-wide (works from any focused window).
    /// If false, the hotkey is only active while the Clipthrough window is focused.
    /// </summary>
    public bool IsGlobal { get; init; }
}
