namespace Clipthrough.Models;

public sealed record AppSettings
{
    public const int DefaultMaxClipSizeBytes = 2_048 * 1_024;
    public const int MinMaxClipSizeBytes = 256;
    public const int MaxMaxClipSizeBytes = 32 * 1_024 * 1_024;
    public const int DefaultNormalClipLifetimeDays = 365;
    public const int DefaultSensitiveClipLifetimeMinutes = 30;
    public const int DefaultMaxLibrarySizeMegabytes = 1_024;
    public const int DefaultMaxEntryCount = 500_000;
    public const int MinNormalClipLifetimeDays = 1;
    public const int MaxNormalClipLifetimeDays = 3_650;
    public const int MinSensitiveClipLifetimeMinutes = 1;
    public const int MaxSensitiveClipLifetimeMinutes = 525_600;
    public const int MinMaxLibrarySizeMegabytes = 1;
    public const int MaxMaxLibrarySizeMegabytes = 1_048_576;
    public const int MinMaxEntryCount = 1;
    public const int MaxMaxEntryCount = 5_000_000;

    public bool EnableToggleRegexHotkey { get; init; } = true;

    public string ToggleRegexHotkey { get; init; } = "Alt+R";

    public bool EnableToggleFavoritesHotkey { get; init; } = true;

    public string ToggleFavoritesHotkey { get; init; } = "Alt+F";

    public bool EnableToggleSensitiveHotkey { get; init; } = true;

    public string ToggleSensitiveHotkey { get; init; } = "Alt+S";

    public bool EnableToggleCaseSensitiveHotkey { get; init; } = true;

    public string ToggleCaseSensitiveHotkey { get; init; } = "Alt+C";

    public bool EnableToggleWildcardHotkey { get; init; } = true;

    public string ToggleWildcardHotkey { get; init; } = "Alt+W";

    public bool EnableToggleWholeWordHotkey { get; init; } = true;

    public string ToggleWholeWordHotkey { get; init; } = "Alt+H";

    public bool EnableTogglePastedHotkey { get; init; } = true;

    public string TogglePastedHotkey { get; init; } = "Alt+P";

    public bool EnableToggleWindowHotkey { get; init; } = true;

    public string ToggleWindowHotkey { get; init; } = "Alt+V";

    public int MaxClipSizeBytes { get; init; } = DefaultMaxClipSizeBytes;

    public bool CloseToTray { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public ThemeMode ThemeMode { get; init; } = ThemeMode.Dark;

    public bool EnableNormalClipLifetime { get; init; } = true;

    public int NormalClipLifetimeDays { get; init; } = DefaultNormalClipLifetimeDays;

    public bool EnableSensitiveClipLifetime { get; init; } = true;

    public int SensitiveClipLifetimeMinutes { get; init; } = DefaultSensitiveClipLifetimeMinutes;

    public bool EnableMaxLibrarySize { get; init; } = true;

    public int MaxLibrarySizeMegabytes { get; init; } = DefaultMaxLibrarySizeMegabytes;

    public bool EnableMaxEntryCount { get; init; } = true;

    public int MaxEntryCount { get; init; } = DefaultMaxEntryCount;

    public static AppSettings Default { get; } = new();

    public AppSettings Normalize() => this with
    {
        EnableToggleRegexHotkey = EnableToggleRegexHotkey,
        ToggleRegexHotkey = NormalizeHotkey(ToggleRegexHotkey, Default.ToggleRegexHotkey),
        EnableToggleFavoritesHotkey = EnableToggleFavoritesHotkey,
        ToggleFavoritesHotkey = NormalizeHotkey(ToggleFavoritesHotkey, Default.ToggleFavoritesHotkey),
        EnableToggleSensitiveHotkey = EnableToggleSensitiveHotkey,
        ToggleSensitiveHotkey = NormalizeHotkey(ToggleSensitiveHotkey, Default.ToggleSensitiveHotkey),
        EnableToggleCaseSensitiveHotkey = EnableToggleCaseSensitiveHotkey,
        ToggleCaseSensitiveHotkey = NormalizeHotkey(ToggleCaseSensitiveHotkey, Default.ToggleCaseSensitiveHotkey),
        EnableToggleWildcardHotkey = EnableToggleWildcardHotkey,
        ToggleWildcardHotkey = NormalizeHotkey(ToggleWildcardHotkey, Default.ToggleWildcardHotkey),
        EnableToggleWholeWordHotkey = EnableToggleWholeWordHotkey,
        ToggleWholeWordHotkey = NormalizeHotkey(ToggleWholeWordHotkey, Default.ToggleWholeWordHotkey),
        EnableTogglePastedHotkey = EnableTogglePastedHotkey,
        TogglePastedHotkey = NormalizeHotkey(TogglePastedHotkey, Default.TogglePastedHotkey),
        EnableToggleWindowHotkey = EnableToggleWindowHotkey,
        ToggleWindowHotkey = NormalizeHotkey(ToggleWindowHotkey, Default.ToggleWindowHotkey),
        MaxClipSizeBytes = MaxClipSizeBytes < MinMaxClipSizeBytes || MaxClipSizeBytes > MaxMaxClipSizeBytes
            ? DefaultMaxClipSizeBytes
            : MaxClipSizeBytes,
        NormalClipLifetimeDays = NormalizeInt(NormalClipLifetimeDays, DefaultNormalClipLifetimeDays, MinNormalClipLifetimeDays, MaxNormalClipLifetimeDays),
        SensitiveClipLifetimeMinutes = NormalizeInt(SensitiveClipLifetimeMinutes, DefaultSensitiveClipLifetimeMinutes, MinSensitiveClipLifetimeMinutes, MaxSensitiveClipLifetimeMinutes),
        MaxLibrarySizeMegabytes = NormalizeInt(MaxLibrarySizeMegabytes, DefaultMaxLibrarySizeMegabytes, MinMaxLibrarySizeMegabytes, MaxMaxLibrarySizeMegabytes),
        MaxEntryCount = NormalizeInt(MaxEntryCount, DefaultMaxEntryCount, MinMaxEntryCount, MaxMaxEntryCount),
    };

    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static int NormalizeInt(int value, int fallback, int min, int max)
        => value < min || value > max ? fallback : value;
}


