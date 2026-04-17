using System.Linq;

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

    public string ToggleRegexHotkey { get; init; } = "Ctrl+Shift+R";

    public bool EnableToggleFavoritesHotkey { get; init; } = true;

    public string ToggleFavoritesHotkey { get; init; } = "Ctrl+Shift+F";

    public bool EnableToggleSensitiveHotkey { get; init; } = true;

    public string ToggleSensitiveHotkey { get; init; } = "Ctrl+Shift+S";

    public bool EnableToggleCaseSensitiveHotkey { get; init; } = true;

    public string ToggleCaseSensitiveHotkey { get; init; } = "Ctrl+Shift+A";

    public bool EnableToggleWildcardHotkey { get; init; } = true;

    public string ToggleWildcardHotkey { get; init; } = "Ctrl+Shift+W";

    public bool EnableToggleWholeWordHotkey { get; init; } = true;

    public string ToggleWholeWordHotkey { get; init; } = "Ctrl+Shift+H";

    public bool EnableTogglePastedHotkey { get; init; } = true;

    public string TogglePastedHotkey { get; init; } = "Ctrl+Shift+P";

    public bool EnableToggleWindowHotkey { get; init; } = true;

    public string ToggleWindowHotkey { get; init; } = "Ctrl+Shift+Space";

    public bool EnableIncrementalPasteHotkey { get; init; } = true;

    public string IncrementalPasteHotkey { get; init; } = "Ctrl+Shift+V";

    public bool EnableDecrementalPasteHotkey { get; init; } = true;

    public string DecrementalPasteHotkey { get; init; } = "Ctrl+Shift+B";

    public bool EnableCopyAndFavoriteHotkey { get; init; }

    public string CopyAndFavoriteHotkey { get; init; } = string.Empty;

    public bool EnableCopyAndSensitiveHotkey { get; init; }

    public string CopyAndSensitiveHotkey { get; init; } = string.Empty;

    public bool EnableCopyWithoutSavingHotkey { get; init; }

    public string CopyWithoutSavingHotkey { get; init; } = string.Empty;

    public bool EnablePasteAndDeleteHotkey { get; init; }

    public string PasteAndDeleteHotkey { get; init; } = string.Empty;

    public bool EnablePasteAndFavoriteHotkey { get; init; }

    public string PasteAndFavoriteHotkey { get; init; } = string.Empty;

    public bool EnablePasteAsPlainTextHotkey { get; init; }

    public string PasteAsPlainTextHotkey { get; init; } = string.Empty;

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

    public string ExternalEditorPath { get; init; } = string.Empty;

    public string ExternalDiffToolPath { get; init; } = string.Empty;

    public ViewModels.ContentDisplayMode LastContentDisplayMode { get; init; } = ViewModels.ContentDisplayMode.Rendered;

    public ViewModels.ImageViewMode LastImageViewMode { get; init; } = ViewModels.ImageViewMode.Editor;

    public bool UseFuzzyClipSearch { get; init; }

    public bool UseSemanticClipSearch { get; init; } = true;

    public bool UseFuzzySettingsSearch { get; init; } = true;

    // Persisted last-session filter toggle states
    public bool LastShowFavoritesOnly { get; init; }
    public bool LastShowSensitiveOnly { get; init; }
    public bool LastShowPastedOnly { get; init; }
    public bool LastUseRegexSearch { get; init; }
    public bool LastCaseSensitiveSearch { get; init; }
    public bool LastUseWildcardSearch { get; init; }
    public bool LastWholeWordSearch { get; init; }
    public ContentType? LastContentTypeFilter { get; init; }

    public bool EnableAi { get; init; }

    public string AiBaseUrl { get; init; } = string.Empty;

    public string AiApiKey { get; init; } = string.Empty;

    public string AiModel { get; init; } = string.Empty;

    public string AiImageModel { get; init; } = string.Empty;

    public string AiReasoningEffort { get; init; } = string.Empty;

    public System.Collections.Generic.IReadOnlyList<UserScript> UserScripts { get; init; } = System.Array.Empty<UserScript>();

    public System.Collections.Generic.IReadOnlyList<AiPreset> AiPresets { get; init; } = System.Array.Empty<AiPreset>();

    public System.Collections.Generic.IReadOnlyList<CustomHotkeyBinding> CustomHotkeys { get; init; } = System.Array.Empty<CustomHotkeyBinding>();

    public bool EnableAutoUpdate { get; init; }

    public string UpdateFeedUrl { get; init; } = string.Empty;

    public string OcrLanguages { get; init; } = "en";

    public bool AutoOcrImageClips { get; init; } = true;

    public bool EnableRemoteApi { get; init; }

    public int RemoteApiPort { get; init; } = 53117;

    public string RemoteApiToken { get; init; } = string.Empty;

    public string RemoteApiBindAddress { get; init; } = "127.0.0.1";

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
        EnableIncrementalPasteHotkey = EnableIncrementalPasteHotkey,
        IncrementalPasteHotkey = NormalizeHotkey(IncrementalPasteHotkey, Default.IncrementalPasteHotkey),
        EnableDecrementalPasteHotkey = EnableDecrementalPasteHotkey,
        DecrementalPasteHotkey = NormalizeHotkey(DecrementalPasteHotkey, Default.DecrementalPasteHotkey),
        EnableCopyAndFavoriteHotkey = EnableCopyAndFavoriteHotkey,
        CopyAndFavoriteHotkey = NormalizeOptionalHotkey(CopyAndFavoriteHotkey),
        EnableCopyAndSensitiveHotkey = EnableCopyAndSensitiveHotkey,
        CopyAndSensitiveHotkey = NormalizeOptionalHotkey(CopyAndSensitiveHotkey),
        EnableCopyWithoutSavingHotkey = EnableCopyWithoutSavingHotkey,
        CopyWithoutSavingHotkey = NormalizeOptionalHotkey(CopyWithoutSavingHotkey),
        EnablePasteAndDeleteHotkey = EnablePasteAndDeleteHotkey,
        PasteAndDeleteHotkey = NormalizeOptionalHotkey(PasteAndDeleteHotkey),
        EnablePasteAndFavoriteHotkey = EnablePasteAndFavoriteHotkey,
        PasteAndFavoriteHotkey = NormalizeOptionalHotkey(PasteAndFavoriteHotkey),
        EnablePasteAsPlainTextHotkey = EnablePasteAsPlainTextHotkey,
        PasteAsPlainTextHotkey = NormalizeOptionalHotkey(PasteAsPlainTextHotkey),
        MaxClipSizeBytes = MaxClipSizeBytes < MinMaxClipSizeBytes || MaxClipSizeBytes > MaxMaxClipSizeBytes
            ? DefaultMaxClipSizeBytes
            : MaxClipSizeBytes,
        NormalClipLifetimeDays = NormalizeInt(NormalClipLifetimeDays, DefaultNormalClipLifetimeDays, MinNormalClipLifetimeDays, MaxNormalClipLifetimeDays),
        SensitiveClipLifetimeMinutes = NormalizeInt(SensitiveClipLifetimeMinutes, DefaultSensitiveClipLifetimeMinutes, MinSensitiveClipLifetimeMinutes, MaxSensitiveClipLifetimeMinutes),
        MaxLibrarySizeMegabytes = NormalizeInt(MaxLibrarySizeMegabytes, DefaultMaxLibrarySizeMegabytes, MinMaxLibrarySizeMegabytes, MaxMaxLibrarySizeMegabytes),
        MaxEntryCount = NormalizeInt(MaxEntryCount, DefaultMaxEntryCount, MinMaxEntryCount, MaxMaxEntryCount),
        ExternalEditorPath = ExternalEditorPath?.Trim() ?? string.Empty,
        ExternalDiffToolPath = ExternalDiffToolPath?.Trim() ?? string.Empty,
        LastContentDisplayMode = LastContentDisplayMode,
        LastImageViewMode = LastImageViewMode,
        LastShowFavoritesOnly = LastShowFavoritesOnly,
        LastShowSensitiveOnly = LastShowSensitiveOnly,
        LastShowPastedOnly = LastShowPastedOnly,
        LastUseRegexSearch = LastUseRegexSearch,
        LastCaseSensitiveSearch = LastCaseSensitiveSearch,
        LastUseWildcardSearch = LastUseWildcardSearch,
        LastWholeWordSearch = LastWholeWordSearch,
        LastContentTypeFilter = LastContentTypeFilter,
        AiBaseUrl = AiBaseUrl?.Trim() ?? string.Empty,
        AiApiKey = AiApiKey?.Trim() ?? string.Empty,
        AiModel = AiModel?.Trim() ?? string.Empty,
        AiImageModel = AiImageModel?.Trim() ?? string.Empty,
        AiReasoningEffort = NormalizeReasoningEffort(AiReasoningEffort),
        UserScripts = (UserScripts ?? System.Array.Empty<UserScript>())
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Code))
            .Select(s => new UserScript { Name = s.Name.Trim(), Code = s.Code })
            .ToList(),
        AiPresets = (AiPresets ?? System.Array.Empty<AiPreset>())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Prompt))
            .Select(p => new AiPreset { Name = p.Name.Trim(), Prompt = p.Prompt.Trim(), Kind = p.Kind })
            .ToList(),
        CustomHotkeys = (CustomHotkeys ?? System.Array.Empty<CustomHotkeyBinding>())
            .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Gesture) && !string.IsNullOrWhiteSpace(h.Target))
            .Select(h => new CustomHotkeyBinding
            {
                Id = string.IsNullOrWhiteSpace(h.Id) ? System.Guid.NewGuid().ToString() : h.Id,
                Gesture = h.Gesture.Trim(),
                Target = h.Target.Trim(),
                PasteAfter = h.PasteAfter,
            })
            .ToList(),
        UpdateFeedUrl = UpdateFeedUrl?.Trim() ?? string.Empty,
        OcrLanguages = string.IsNullOrWhiteSpace(OcrLanguages) ? "en" : OcrLanguages.Trim(),
        AutoOcrImageClips = AutoOcrImageClips,
        EnableRemoteApi = EnableRemoteApi,
        RemoteApiPort = RemoteApiPort <= 0 || RemoteApiPort > 65535 ? 53117 : RemoteApiPort,
        RemoteApiToken = EnableRemoteApi ? (RemoteApiToken?.Trim() ?? string.Empty) : string.Empty,
        RemoteApiBindAddress = string.IsNullOrWhiteSpace(RemoteApiBindAddress) ? "127.0.0.1" : RemoteApiBindAddress.Trim(),
    };

    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : MigrateLegacyAltHotkey(value.Trim(), fallback);

    private static string NormalizeOptionalHotkey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : MigrateLegacyAltHotkey(value.Trim(), string.Empty);

    // Older builds shipped filter hotkeys as bare `Alt+<Letter>` which collides with Avalonia's
    // menu access keys. Migrate any such value to the modern Ctrl-based default so upgraded
    // installs don't permanently keep the conflicting bindings.
    private static string MigrateLegacyAltHotkey(string value, string fallback)
    {
        if (!value.StartsWith("Alt+", System.StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var rest = value.Substring(4);
        if (rest.Length == 0
            || rest.Contains('+', System.StringComparison.Ordinal))
        {
            return value;
        }

        return string.IsNullOrEmpty(fallback) ? "Ctrl+Shift+" + rest : fallback;
    }

    private static int NormalizeInt(int value, int fallback, int min, int max)
        => value < min || value > max ? fallback : value;

    private static string NormalizeReasoningEffort(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        return trimmed switch
        {
            "none" or "minimal" or "low" or "medium" or "high" => trimmed,
            _ => string.Empty,
        };
    }
}


