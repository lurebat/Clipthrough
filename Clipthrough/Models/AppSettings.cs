using System.Linq;

namespace Clipthrough.Models;

public sealed record AppSettings
{
    public const string DefaultUpdateFeedUrl = "https://github.com/lurebat/Clipthrough/releases/latest/download";
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

    public string ToggleRegexHotkey { get; init; } = "Ctrl+R";

    public bool EnableToggleFavoritesHotkey { get; init; } = true;

    // Not Ctrl+D: the clip list handles that itself as "copy selected" (and the
    // Edit menu advertises it), and the built-in handlers run before the
    // configurable filter hotkeys, so a Ctrl+D favorites toggle never fired
    // while the list had focus - the window's normal state.
    public string ToggleFavoritesHotkey { get; init; } = "Ctrl+B";

    public bool EnableToggleSensitiveHotkey { get; init; } = true;

    public string ToggleSensitiveHotkey { get; init; } = "Ctrl+L";

    public bool EnableToggleCaseSensitiveHotkey { get; init; } = true;

    public string ToggleCaseSensitiveHotkey { get; init; } = "Ctrl+K";

    public bool EnableToggleWildcardHotkey { get; init; } = true;

    public string ToggleWildcardHotkey { get; init; } = "Ctrl+M";

    public bool EnableToggleWholeWordHotkey { get; init; } = true;

    public string ToggleWholeWordHotkey { get; init; } = "Ctrl+E";

    public bool EnableTogglePastedHotkey { get; init; } = true;

    public string TogglePastedHotkey { get; init; } = "Ctrl+U";

    public bool EnableToggleFuzzyHotkey { get; init; } = true;

    public string ToggleFuzzyHotkey { get; init; } = "Ctrl+T";

    public bool EnableToggleSemanticHotkey { get; init; } = true;

    public string ToggleSemanticHotkey { get; init; } = "Ctrl+J";

    public bool EnableToggleWindowHotkey { get; init; } = true;

    public string ToggleWindowHotkey { get; init; } = "Alt+V";

    public bool EnableIncrementalPasteHotkey { get; init; }

    public string IncrementalPasteHotkey { get; init; } = string.Empty;

    public bool EnableDecrementalPasteHotkey { get; init; }

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

    public string ExternalImageEditorPath { get; init; } = string.Empty;

    public string ExternalDiffToolPath { get; init; } = string.Empty;

    public ViewModels.ContentDisplayMode LastContentDisplayMode { get; init; } = ViewModels.ContentDisplayMode.Rendered;

    public ViewModels.ImageViewMode LastImageViewMode { get; init; } = ViewModels.ImageViewMode.Editor;

    public bool EnableSemanticSearch { get; init; }

    public bool UseFuzzySettingsSearch { get; init; } = true;

    // Persisted last-session filter toggle states. The two search toggles live
    // here and only here: an earlier pair of UseFuzzyClipSearch /
    // UseSemanticClipSearch was written on every save and never read back, so
    // the same concept had two names and one of them was inert. Removed rather
    // than kept as an alias, because the trap is finding the dead one first.
    public bool LastShowFavoritesOnly { get; init; }
    public bool LastShowSensitiveOnly { get; init; }
    public bool LastShowPastedOnly { get; init; }
    public bool LastUseRegexSearch { get; init; }
    public bool LastCaseSensitiveSearch { get; init; }
    public bool LastUseWildcardSearch { get; init; }
    public bool LastWholeWordSearch { get; init; }
    public bool LastUseFuzzyClipSearch { get; init; }
    public bool LastUseSemanticClipSearch { get; init; }

    /// <summary>
    /// Legacy single-value content-type filter. Retained for backward
    /// compatibility — at load time it's promoted into the
    /// <see cref="LastContentTypeFilters"/> list and cleared on next save.
    /// </summary>
    public ContentType? LastContentTypeFilter { get; init; }

    /// <summary>
    /// Content-type filter chips that were active at the end of the last
    /// session. Empty/null means "no filter — show all content types".
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<ContentType> LastContentTypeFilters { get; init; } = System.Array.Empty<ContentType>();

    /// <summary>
    /// Takes the session state — every <c>Last*</c> field above — from
    /// <paramref name="other"/>, leaving configuration untouched.
    ///
    /// The settings dialog composes a record from a snapshot it read when it
    /// opened, but it owns none of these: they are written continuously by the
    /// search filters and the content/image view mode while the dialog is up. A
    /// dialog save that carried its own stale copy of them would revert every
    /// filter toggle the user made in the meantime.
    ///
    /// Kept next to the fields deliberately — adding a <c>Last*</c> setting and
    /// forgetting this method reintroduces exactly that bug.
    /// </summary>
    public AppSettings WithSessionStateFrom(AppSettings other) => this with
    {
        LastContentDisplayMode = other.LastContentDisplayMode,
        LastImageViewMode = other.LastImageViewMode,
        LastShowFavoritesOnly = other.LastShowFavoritesOnly,
        LastShowSensitiveOnly = other.LastShowSensitiveOnly,
        LastShowPastedOnly = other.LastShowPastedOnly,
        LastUseRegexSearch = other.LastUseRegexSearch,
        LastCaseSensitiveSearch = other.LastCaseSensitiveSearch,
        LastUseWildcardSearch = other.LastUseWildcardSearch,
        LastWholeWordSearch = other.LastWholeWordSearch,
        LastUseFuzzyClipSearch = other.LastUseFuzzyClipSearch,
        LastUseSemanticClipSearch = other.LastUseSemanticClipSearch,
        LastContentTypeFilter = other.LastContentTypeFilter,
        LastContentTypeFilters = other.LastContentTypeFilters,
    };

    public bool EnableAi { get; init; }

    public AiProvider AiProvider { get; init; }

    public string AiBaseUrl { get; init; } = string.Empty;

    public string AiApiKey { get; init; } = string.Empty;

    public string AiModel { get; init; } = string.Empty;

    public string AiImageModel { get; init; } = string.Empty;

    public string AiReasoningEffort { get; init; } = string.Empty;

    public System.Collections.Generic.IReadOnlyList<AiPreset> AiPresets { get; init; } = System.Array.Empty<AiPreset>();

    public System.Collections.Generic.IReadOnlyList<CustomHotkeyBinding> CustomHotkeys { get; init; } = System.Array.Empty<CustomHotkeyBinding>();

    public bool EnableAutoUpdate { get; init; } = true;

    /// <summary>
    /// When true, an update that finished downloading in a previous run is
    /// applied silently on the next startup (the app restarts itself before
    /// the user sees the window). Defaults to false so users are never
    /// surprised by an automatic restart; instead they are notified and can
    /// install on their own schedule.
    /// </summary>
    public bool AutoApplyUpdatesOnStartup { get; init; }

    public string UpdateFeedUrl { get; init; } = DefaultUpdateFeedUrl;

    public string OcrLanguages { get; init; } = "en";

    public bool AutoOcrImageClips { get; init; } = true;

    /// <summary>
    /// Applications whose clipboard writes are never captured. Each entry is a
    /// process name, executable file name, full path, or a <c>*</c>/<c>?</c>
    /// wildcard pattern; see <see cref="Services.CaptureExclusionPolicy"/> for
    /// the matching rules and the deliberate fail-open behaviour.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> ExcludedCaptureApps { get; init; } = System.Array.Empty<string>();

    public static AppSettings Default { get; } = new();

    /// <summary>
    /// True when the only difference between two saved settings is the
    /// <c>Last*</c> view state the history window persists as the user clicks
    /// around - filter toggles, search modes, the content-type selection.
    ///
    /// Callers use this to skip re-applying system state (global hotkeys, the
    /// Run registry key) for a save that cannot possibly have changed it.
    /// Filter toggles are persisted on a 500 ms debounce, so without a gate
    /// every checkbox click tears down and re-registers every global hotkey.
    ///
    /// Deliberately conservative: it answers "is this *definitely* only view
    /// state?", so a setting added later is treated as relevant until someone
    /// says otherwise. Over-applying wastes work; under-applying leaves a
    /// hotkey the user just rebound silently dead until the next restart.
    /// </summary>
    public static bool OnlyViewStateChanged(AppSettings previous, AppSettings current)
    {
        System.ArgumentNullException.ThrowIfNull(previous);
        System.ArgumentNullException.ThrowIfNull(current);

        return WithoutViewState(previous) == WithoutViewState(current)
            && previous.AiPresets.SequenceEqual(current.AiPresets)
            && previous.CustomHotkeys.SequenceEqual(current.CustomHotkeys)
            && previous.ExcludedCaptureApps.SequenceEqual(current.ExcludedCaptureApps, System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Blanks the view-state fields so the record's synthesized equality can
    /// answer "is everything else the same?".
    ///
    /// The four list properties need care. Record equality compares them by
    /// *reference*, and <see cref="Normalize"/> rebuilds AiPresets,
    /// CustomHotkeys and ExcludedCaptureApps into fresh lists on every single
    /// save - so comparing two saved instances directly always reports a
    /// difference and any gate built on it would never fire. Pinning both sides
    /// to <see cref="System.Array.Empty{T}"/>, which returns one cached
    /// instance, makes that comparison meaningful; the contents are then
    /// compared structurally by the caller.
    /// </summary>
    private static AppSettings WithoutViewState(AppSettings settings) => settings with
    {
        LastContentDisplayMode = default,
        LastImageViewMode = default,
        LastShowFavoritesOnly = default,
        LastShowSensitiveOnly = default,
        LastShowPastedOnly = default,
        LastUseRegexSearch = default,
        LastCaseSensitiveSearch = default,
        LastUseWildcardSearch = default,
        LastWholeWordSearch = default,
        LastUseFuzzyClipSearch = default,
        LastUseSemanticClipSearch = default,
        LastContentTypeFilter = default,
        LastContentTypeFilters = System.Array.Empty<ContentType>(),
        AiPresets = System.Array.Empty<AiPreset>(),
        CustomHotkeys = System.Array.Empty<CustomHotkeyBinding>(),
        ExcludedCaptureApps = System.Array.Empty<string>(),
    };

    public AppSettings Normalize() => this with
    {
        EnableToggleRegexHotkey = EnableToggleRegexHotkey,
        ToggleRegexHotkey = MigrateFilterHotkey(ToggleRegexHotkey, Default.ToggleRegexHotkey, "Alt+R", "Ctrl+Shift+R"),
        EnableToggleFavoritesHotkey = EnableToggleFavoritesHotkey,
        ToggleFavoritesHotkey = MigrateFilterHotkey(ToggleFavoritesHotkey, Default.ToggleFavoritesHotkey, "Alt+F", "Ctrl+Shift+F", "Ctrl+D"),
        EnableToggleSensitiveHotkey = EnableToggleSensitiveHotkey,
        ToggleSensitiveHotkey = MigrateFilterHotkey(ToggleSensitiveHotkey, Default.ToggleSensitiveHotkey, "Alt+S", "Ctrl+Shift+S"),
        EnableToggleCaseSensitiveHotkey = EnableToggleCaseSensitiveHotkey,
        ToggleCaseSensitiveHotkey = MigrateFilterHotkey(ToggleCaseSensitiveHotkey, Default.ToggleCaseSensitiveHotkey, "Alt+C", "Alt+A", "Ctrl+Shift+A"),
        EnableToggleWildcardHotkey = EnableToggleWildcardHotkey,
        ToggleWildcardHotkey = MigrateFilterHotkey(ToggleWildcardHotkey, Default.ToggleWildcardHotkey, "Alt+W", "Ctrl+Shift+W"),
        EnableToggleWholeWordHotkey = EnableToggleWholeWordHotkey,
        ToggleWholeWordHotkey = MigrateFilterHotkey(ToggleWholeWordHotkey, Default.ToggleWholeWordHotkey, "Alt+H", "Ctrl+Shift+H"),
        EnableTogglePastedHotkey = EnableTogglePastedHotkey,
        TogglePastedHotkey = MigrateFilterHotkey(TogglePastedHotkey, Default.TogglePastedHotkey, "Alt+P", "Ctrl+Shift+P"),
        EnableToggleFuzzyHotkey = EnableToggleFuzzyHotkey,
        ToggleFuzzyHotkey = MigrateFilterHotkey(ToggleFuzzyHotkey, Default.ToggleFuzzyHotkey),
        EnableToggleSemanticHotkey = EnableToggleSemanticHotkey,
        ToggleSemanticHotkey = MigrateFilterHotkey(ToggleSemanticHotkey, Default.ToggleSemanticHotkey),
        EnableToggleWindowHotkey = EnableToggleWindowHotkey,
        ToggleWindowHotkey = MigrateFilterHotkey(ToggleWindowHotkey, Default.ToggleWindowHotkey, "Ctrl+Shift+Space"),
        EnableIncrementalPasteHotkey = EnableIncrementalPasteHotkey,
        IncrementalPasteHotkey = NormalizeOptionalHotkey(IncrementalPasteHotkey),
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
        ExternalImageEditorPath = ExternalImageEditorPath?.Trim() ?? string.Empty,
        ExternalDiffToolPath = ExternalDiffToolPath?.Trim() ?? string.Empty,
        LastContentDisplayMode = NormalizeContentDisplayMode(LastContentDisplayMode),
        LastImageViewMode = LastImageViewMode,
        LastShowFavoritesOnly = LastShowFavoritesOnly,
        LastShowSensitiveOnly = LastShowSensitiveOnly,
        LastShowPastedOnly = LastShowPastedOnly,
        LastUseRegexSearch = LastUseRegexSearch,
        LastCaseSensitiveSearch = LastCaseSensitiveSearch,
        LastUseWildcardSearch = LastUseWildcardSearch,
        LastWholeWordSearch = LastWholeWordSearch,
        LastUseFuzzyClipSearch = LastUseFuzzyClipSearch,
        LastUseSemanticClipSearch = LastUseSemanticClipSearch,
        // Promote any legacy single-value LastContentTypeFilter into the new
        // multi-value list. Save flow always writes both, but we only emit
        // the legacy field as long as the list is empty to avoid drift.
        LastContentTypeFilter = LastContentTypeFilters?.Count > 0 ? null : LastContentTypeFilter,
        LastContentTypeFilters = (LastContentTypeFilters?.Count > 0
            ? LastContentTypeFilters
            : LastContentTypeFilter is { } legacy
                ? new[] { legacy }
                : System.Array.Empty<ContentType>()) ?? System.Array.Empty<ContentType>(),
        AiBaseUrl = AiBaseUrl?.Trim() ?? string.Empty,
        AiApiKey = AiApiKey?.Trim() ?? string.Empty,
        AiModel = AiModel?.Trim() ?? string.Empty,
        AiImageModel = AiImageModel?.Trim() ?? string.Empty,
        AiReasoningEffort = NormalizeReasoningEffort(AiReasoningEffort),
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
                IsGlobal = h.IsGlobal,
            })
            .ToList(),
        UpdateFeedUrl = UpdateFeedUrl?.Trim() ?? string.Empty,
        AutoApplyUpdatesOnStartup = AutoApplyUpdatesOnStartup,
        OcrLanguages = string.IsNullOrWhiteSpace(OcrLanguages) ? "en" : OcrLanguages.Trim(),
        AutoOcrImageClips = AutoOcrImageClips,
        ExcludedCaptureApps = (ExcludedCaptureApps ?? System.Array.Empty<string>())
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : MigrateLegacyAltHotkey(value.Trim(), fallback);

    private static string NormalizeOptionalHotkey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : MigrateLegacyAltHotkey(value.Trim(), string.Empty);

    private static ViewModels.ContentDisplayMode NormalizeContentDisplayMode(ViewModels.ContentDisplayMode value)
        => value == ViewModels.ContentDisplayMode.WebView
            ? ViewModels.ContentDisplayMode.Rendered
            : value;

    // Filter-toggle hotkeys were originally shipped as bare Alt+<letter> (conflicts with
    // menu access keys) and later as Ctrl+Shift+<letter>. Both are now considered legacy and
    // should be migrated to the modern Ctrl+<letter> default so existing installs pick up the
    // simpler bindings. Any value the user has customised away from those is preserved.
    private static string MigrateFilterHotkey(string? value, string newDefault, params string[] legacyDefaults)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return newDefault;
        }

        var trimmed = value.Trim();

        foreach (var legacy in legacyDefaults)
        {
            if (string.Equals(trimmed, legacy, System.StringComparison.OrdinalIgnoreCase))
            {
                return newDefault;
            }
        }

        return MigrateLegacyAltHotkey(trimmed, newDefault);
    }

    // Older builds shipped filter hotkeys as bare `Alt+<Letter>` which collides with Avalonia's
    // menu access keys. Migrate any such value to the modern default so upgraded installs don't
    // permanently keep the conflicting bindings. Combined gestures like Ctrl+Alt+T are left alone.
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

        return string.IsNullOrEmpty(fallback) ? "Ctrl+" + rest : fallback;
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

