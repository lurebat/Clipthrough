namespace Clipthrough.Models;

public sealed record AppSettings
{
    public const int DefaultMaxClipSizeBytes = 2_048 * 1_024;
    public const int MinMaxClipSizeBytes = 256;
    public const int MaxMaxClipSizeBytes = 32 * 1_024 * 1_024;

    public string ToggleRegexHotkey { get; init; } = "Alt+R";

    public string ToggleFavoritesHotkey { get; init; } = "Alt+F";

    public string ToggleSensitiveHotkey { get; init; } = "Alt+S";

    public string ToggleCaseSensitiveHotkey { get; init; } = "Alt+C";

    public string ToggleWindowHotkey { get; init; } = "Alt+V";

    public int MaxClipSizeBytes { get; init; } = DefaultMaxClipSizeBytes;

    public bool CloseToTray { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public static AppSettings Default { get; } = new();

    public AppSettings Normalize() => this with
    {
        ToggleRegexHotkey = NormalizeHotkey(ToggleRegexHotkey, Default.ToggleRegexHotkey),
        ToggleFavoritesHotkey = NormalizeHotkey(ToggleFavoritesHotkey, Default.ToggleFavoritesHotkey),
        ToggleSensitiveHotkey = NormalizeHotkey(ToggleSensitiveHotkey, Default.ToggleSensitiveHotkey),
        ToggleCaseSensitiveHotkey = NormalizeHotkey(ToggleCaseSensitiveHotkey, Default.ToggleCaseSensitiveHotkey),
        ToggleWindowHotkey = NormalizeHotkey(ToggleWindowHotkey, Default.ToggleWindowHotkey),
        MaxClipSizeBytes = MaxClipSizeBytes < MinMaxClipSizeBytes || MaxClipSizeBytes > MaxMaxClipSizeBytes
            ? DefaultMaxClipSizeBytes
            : MaxClipSizeBytes,
    };

    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}


