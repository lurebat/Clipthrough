using System;
using System.Collections.Generic;
using Clipthrough.Models;
using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// Editable draft state for the Settings form, extracted from
/// <see cref="MainWindowViewModel"/> (#10). Holds the form values bound by
/// <c>SettingsWindow.axaml</c>; the host view model's <c>LoadSettingsDraft</c>
/// populates it and <c>SaveSettingsAsync</c> applies it (the storage-lifecycle
/// apply stays in the host). Grown one settings section per commit; it now also
/// owns the form's own search box and the collapsible sections it filters.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    // --- AI ---

    private bool _enableAi = AppSettings.Default.EnableAi;
    public bool EnableAi
    {
        get => _enableAi;
        set => this.RaiseAndSetIfChanged(ref _enableAi, value);
    }

    private AiProvider _aiProvider = AppSettings.Default.AiProvider;
    public AiProvider AiProvider
    {
        get => _aiProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _aiProvider, value);
            this.RaisePropertyChanged(nameof(IsOpenAiSettingsVisible));
            this.RaisePropertyChanged(nameof(IsCopilotSettingsVisible));
        }
    }

    public bool IsOpenAiSettingsVisible => AiProvider == Models.AiProvider.OpenAi;
    public bool IsCopilotSettingsVisible => AiProvider == Models.AiProvider.Copilot;

    private string _aiBaseUrl = AppSettings.Default.AiBaseUrl;
    public string AiBaseUrl
    {
        get => _aiBaseUrl;
        set => this.RaiseAndSetIfChanged(ref _aiBaseUrl, value);
    }

    private string _aiApiKey = AppSettings.Default.AiApiKey;
    public string AiApiKey
    {
        get => _aiApiKey;
        set => this.RaiseAndSetIfChanged(ref _aiApiKey, value);
    }

    private string _aiModel = AppSettings.Default.AiModel;
    public string AiModel
    {
        get => _aiModel;
        set => this.RaiseAndSetIfChanged(ref _aiModel, value);
    }

    private string _aiImageModel = AppSettings.Default.AiImageModel;
    public string AiImageModel
    {
        get => _aiImageModel;
        set => this.RaiseAndSetIfChanged(ref _aiImageModel, value);
    }

    private string _aiReasoningEffort = AppSettings.Default.AiReasoningEffort;
    public string AiReasoningEffort
    {
        get => _aiReasoningEffort;
        set => this.RaiseAndSetIfChanged(ref _aiReasoningEffort, value);
    }

    public IReadOnlyList<string> AiReasoningEffortOptions { get; } = new[] { "", "none", "minimal", "low", "medium", "high" };

    public AiProvider[] AiProviderOptions { get; } = Enum.GetValues<AiProvider>();

    // --- Update ---

    private bool _enableAutoUpdate = AppSettings.Default.EnableAutoUpdate;
    public bool EnableAutoUpdate
    {
        get => _enableAutoUpdate;
        set => this.RaiseAndSetIfChanged(ref _enableAutoUpdate, value);
    }

    private bool _autoApplyUpdatesOnStartup = AppSettings.Default.AutoApplyUpdatesOnStartup;
    public bool AutoApplyUpdatesOnStartup
    {
        get => _autoApplyUpdatesOnStartup;
        set => this.RaiseAndSetIfChanged(ref _autoApplyUpdatesOnStartup, value);
    }

    private string _updateFeedUrl = AppSettings.Default.UpdateFeedUrl;
    public string UpdateFeedUrl
    {
        get => _updateFeedUrl;
        set => this.RaiseAndSetIfChanged(ref _updateFeedUrl, value);
    }

    // --- OCR ---

    private string _ocrLanguages = AppSettings.Default.OcrLanguages;
    public string OcrLanguages
    {
        get => _ocrLanguages;
        set => this.RaiseAndSetIfChanged(ref _ocrLanguages, value);
    }

    private bool _autoOcrImageClips = AppSettings.Default.AutoOcrImageClips;
    public bool AutoOcrImageClips
    {
        get => _autoOcrImageClips;
        set => this.RaiseAndSetIfChanged(ref _autoOcrImageClips, value);
    }

    // --- Theme ---

    private ThemeMode _themeMode = AppSettings.Default.ThemeMode;
    public ThemeMode ThemeMode
    {
        get => _themeMode;
        set => this.RaiseAndSetIfChanged(ref _themeMode, value);
    }

    public ThemeMode[] ThemeModeOptions { get; } = Enum.GetValues<ThemeMode>();

    // --- Storage paths / limits ---

    private string _maxClipSizeKilobytes = (AppSettings.Default.MaxClipSizeBytes / 1024d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    public string MaxClipSizeKilobytes
    {
        get => _maxClipSizeKilobytes;
        set => this.RaiseAndSetIfChanged(ref _maxClipSizeKilobytes, value);
    }

    private string _databasePath = StorageOptions.Default.DatabasePath;
    public string DatabasePath
    {
        get => _databasePath;
        set => this.RaiseAndSetIfChanged(ref _databasePath, value);
    }

    private string _excludedCaptureAppsText = string.Empty;
    /// <summary>
    /// Newline-separated exclusion patterns, edited as free text and parsed by
    /// <see cref="Services.CaptureExclusionPolicy.ParsePatterns"/> on save.
    /// </summary>
    public string ExcludedCaptureAppsText
    {
        get => _excludedCaptureAppsText;
        set => this.RaiseAndSetIfChanged(ref _excludedCaptureAppsText, value);
    }

    private string _externalEditorPath = AppSettings.Default.ExternalEditorPath;

    public string ExternalEditorPath
    {
        get => _externalEditorPath;
        set => this.RaiseAndSetIfChanged(ref _externalEditorPath, value);
    }

    private string _externalImageEditorPath = AppSettings.Default.ExternalImageEditorPath;
    public string ExternalImageEditorPath
    {
        get => _externalImageEditorPath;
        set => this.RaiseAndSetIfChanged(ref _externalImageEditorPath, value);
    }

    private string _externalDiffToolPath = AppSettings.Default.ExternalDiffToolPath;
    public string ExternalDiffToolPath
    {
        get => _externalDiffToolPath;
        set => this.RaiseAndSetIfChanged(ref _externalDiffToolPath, value);
    }

    // --- Retention / capacity limits ---
    //
    // The host mirrors four of these into its storage- and entry-capacity readouts,
    // so the main window reflects a limit as it is typed. It does that by observing
    // this view model rather than by having these setters raise the host's property
    // names, which is what they used to do while they lived on the host: a dependency
    // declared once beats the same four RaisePropertyChanged calls copied into four
    // setters, where adding a fifth limit means remembering to copy them again.

    private bool _enableNormalClipLifetime = AppSettings.Default.EnableNormalClipLifetime;
    public bool EnableNormalClipLifetime
    {
        get => _enableNormalClipLifetime;
        set => this.RaiseAndSetIfChanged(ref _enableNormalClipLifetime, value);
    }

    private string _normalClipLifetimeDays =
        AppSettings.Default.NormalClipLifetimeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string NormalClipLifetimeDays
    {
        get => _normalClipLifetimeDays;
        set => this.RaiseAndSetIfChanged(ref _normalClipLifetimeDays, value);
    }

    private bool _enableSensitiveClipLifetime = AppSettings.Default.EnableSensitiveClipLifetime;
    public bool EnableSensitiveClipLifetime
    {
        get => _enableSensitiveClipLifetime;
        set => this.RaiseAndSetIfChanged(ref _enableSensitiveClipLifetime, value);
    }

    private string _sensitiveClipLifetimeMinutes =
        AppSettings.Default.SensitiveClipLifetimeMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string SensitiveClipLifetimeMinutes
    {
        get => _sensitiveClipLifetimeMinutes;
        set => this.RaiseAndSetIfChanged(ref _sensitiveClipLifetimeMinutes, value);
    }

    private bool _enableMaxLibrarySize = AppSettings.Default.EnableMaxLibrarySize;
    public bool EnableMaxLibrarySize
    {
        get => _enableMaxLibrarySize;
        set => this.RaiseAndSetIfChanged(ref _enableMaxLibrarySize, value);
    }

    private string _maxLibrarySizeMegabytes =
        AppSettings.Default.MaxLibrarySizeMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MaxLibrarySizeMegabytes
    {
        get => _maxLibrarySizeMegabytes;
        set => this.RaiseAndSetIfChanged(ref _maxLibrarySizeMegabytes, value);
    }

    private bool _enableMaxEntryCount = AppSettings.Default.EnableMaxEntryCount;
    public bool EnableMaxEntryCount
    {
        get => _enableMaxEntryCount;
        set => this.RaiseAndSetIfChanged(ref _enableMaxEntryCount, value);
    }

    private string _maxEntryCount =
        AppSettings.Default.MaxEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MaxEntryCount
    {
        get => _maxEntryCount;
        set => this.RaiseAndSetIfChanged(ref _maxEntryCount, value);
    }

    // --- Tray / startup ---

    private bool _closeToTray = AppSettings.Default.CloseToTray;
    public bool CloseToTray
    {
        get => _closeToTray;
        set => this.RaiseAndSetIfChanged(ref _closeToTray, value);
    }

    private bool _minimizeToTray = AppSettings.Default.MinimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => this.RaiseAndSetIfChanged(ref _minimizeToTray, value);
    }

    private bool _startWithWindows = AppSettings.Default.StartWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => this.RaiseAndSetIfChanged(ref _startWithWindows, value);
    }

    // --- Hotkeys (local filter toggles) ---

    private string _toggleRegexHotkey = AppSettings.Default.ToggleRegexHotkey;
    public string ToggleRegexHotkey
    {
        get => _toggleRegexHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleRegexHotkey, value);
    }

    private bool _enableToggleRegexHotkey = AppSettings.Default.EnableToggleRegexHotkey;
    public bool EnableToggleRegexHotkey
    {
        get => _enableToggleRegexHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleRegexHotkey, value);
    }

    private string _toggleFavoritesHotkey = AppSettings.Default.ToggleFavoritesHotkey;
    public string ToggleFavoritesHotkey
    {
        get => _toggleFavoritesHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleFavoritesHotkey, value);
    }

    private bool _enableToggleFavoritesHotkey = AppSettings.Default.EnableToggleFavoritesHotkey;
    public bool EnableToggleFavoritesHotkey
    {
        get => _enableToggleFavoritesHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleFavoritesHotkey, value);
    }

    private string _toggleSensitiveHotkey = AppSettings.Default.ToggleSensitiveHotkey;
    public string ToggleSensitiveHotkey
    {
        get => _toggleSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleSensitiveHotkey, value);
    }

    private bool _enableToggleSensitiveHotkey = AppSettings.Default.EnableToggleSensitiveHotkey;
    public bool EnableToggleSensitiveHotkey
    {
        get => _enableToggleSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleSensitiveHotkey, value);
    }

    private string _toggleCaseSensitiveHotkey = AppSettings.Default.ToggleCaseSensitiveHotkey;
    public string ToggleCaseSensitiveHotkey
    {
        get => _toggleCaseSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleCaseSensitiveHotkey, value);
    }

    private bool _enableToggleCaseSensitiveHotkey = AppSettings.Default.EnableToggleCaseSensitiveHotkey;
    public bool EnableToggleCaseSensitiveHotkey
    {
        get => _enableToggleCaseSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleCaseSensitiveHotkey, value);
    }

    private string _toggleWildcardHotkey = AppSettings.Default.ToggleWildcardHotkey;
    public string ToggleWildcardHotkey
    {
        get => _toggleWildcardHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleWildcardHotkey, value);
    }

    private bool _enableToggleWildcardHotkey = AppSettings.Default.EnableToggleWildcardHotkey;
    public bool EnableToggleWildcardHotkey
    {
        get => _enableToggleWildcardHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleWildcardHotkey, value);
    }

    private string _toggleWholeWordHotkey = AppSettings.Default.ToggleWholeWordHotkey;
    public string ToggleWholeWordHotkey
    {
        get => _toggleWholeWordHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleWholeWordHotkey, value);
    }

    private bool _enableToggleWholeWordHotkey = AppSettings.Default.EnableToggleWholeWordHotkey;
    public bool EnableToggleWholeWordHotkey
    {
        get => _enableToggleWholeWordHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleWholeWordHotkey, value);
    }

    private string _togglePastedHotkey = AppSettings.Default.TogglePastedHotkey;
    public string TogglePastedHotkey
    {
        get => _togglePastedHotkey;
        set => this.RaiseAndSetIfChanged(ref _togglePastedHotkey, value);
    }

    private bool _enableTogglePastedHotkey = AppSettings.Default.EnableTogglePastedHotkey;
    public bool EnableTogglePastedHotkey
    {
        get => _enableTogglePastedHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableTogglePastedHotkey, value);
    }

    private string _toggleFuzzyHotkey = AppSettings.Default.ToggleFuzzyHotkey;
    public string ToggleFuzzyHotkey
    {
        get => _toggleFuzzyHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleFuzzyHotkey, value);
    }

    private bool _enableToggleFuzzyHotkey = AppSettings.Default.EnableToggleFuzzyHotkey;
    public bool EnableToggleFuzzyHotkey
    {
        get => _enableToggleFuzzyHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleFuzzyHotkey, value);
    }

    private string _toggleSemanticHotkey = AppSettings.Default.ToggleSemanticHotkey;
    public string ToggleSemanticHotkey
    {
        get => _toggleSemanticHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleSemanticHotkey, value);
    }

    private bool _enableToggleSemanticHotkey = AppSettings.Default.EnableToggleSemanticHotkey;
    public bool EnableToggleSemanticHotkey
    {
        get => _enableToggleSemanticHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleSemanticHotkey, value);
    }

    // --- Hotkeys (global & paste) ---

    private string _toggleWindowHotkey = AppSettings.Default.ToggleWindowHotkey;
    public string ToggleWindowHotkey
    {
        get => _toggleWindowHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleWindowHotkey, value);
    }

    private bool _enableToggleWindowHotkey = AppSettings.Default.EnableToggleWindowHotkey;
    public bool EnableToggleWindowHotkey
    {
        get => _enableToggleWindowHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableToggleWindowHotkey, value);
    }

    private string _incrementalPasteHotkey = AppSettings.Default.IncrementalPasteHotkey;
    public string IncrementalPasteHotkey
    {
        get => _incrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _incrementalPasteHotkey, value);
    }

    private bool _enableIncrementalPasteHotkey = AppSettings.Default.EnableIncrementalPasteHotkey;
    public bool EnableIncrementalPasteHotkey
    {
        get => _enableIncrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableIncrementalPasteHotkey, value);
    }

    private string _decrementalPasteHotkey = AppSettings.Default.DecrementalPasteHotkey;
    public string DecrementalPasteHotkey
    {
        get => _decrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _decrementalPasteHotkey, value);
    }

    private bool _enableDecrementalPasteHotkey = AppSettings.Default.EnableDecrementalPasteHotkey;
    public bool EnableDecrementalPasteHotkey
    {
        get => _enableDecrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableDecrementalPasteHotkey, value);
    }

    private string _copyAndFavoriteHotkey = AppSettings.Default.CopyAndFavoriteHotkey;
    public string CopyAndFavoriteHotkey
    {
        get => _copyAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _copyAndFavoriteHotkey, value);
    }

    private bool _enableCopyAndFavoriteHotkey = AppSettings.Default.EnableCopyAndFavoriteHotkey;
    public bool EnableCopyAndFavoriteHotkey
    {
        get => _enableCopyAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableCopyAndFavoriteHotkey, value);
    }

    private string _copyAndSensitiveHotkey = AppSettings.Default.CopyAndSensitiveHotkey;
    public string CopyAndSensitiveHotkey
    {
        get => _copyAndSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _copyAndSensitiveHotkey, value);
    }

    private bool _enableCopyAndSensitiveHotkey = AppSettings.Default.EnableCopyAndSensitiveHotkey;
    public bool EnableCopyAndSensitiveHotkey
    {
        get => _enableCopyAndSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableCopyAndSensitiveHotkey, value);
    }

    private string _copyWithoutSavingHotkey = AppSettings.Default.CopyWithoutSavingHotkey;
    public string CopyWithoutSavingHotkey
    {
        get => _copyWithoutSavingHotkey;
        set => this.RaiseAndSetIfChanged(ref _copyWithoutSavingHotkey, value);
    }

    private bool _enableCopyWithoutSavingHotkey = AppSettings.Default.EnableCopyWithoutSavingHotkey;
    public bool EnableCopyWithoutSavingHotkey
    {
        get => _enableCopyWithoutSavingHotkey;
        set => this.RaiseAndSetIfChanged(ref _enableCopyWithoutSavingHotkey, value);
    }

    private string _pasteAndDeleteHotkey = AppSettings.Default.PasteAndDeleteHotkey;
    public string PasteAndDeleteHotkey
    {
        get => _pasteAndDeleteHotkey;
        set => this.RaiseAndSetIfChanged(ref _pasteAndDeleteHotkey, value);
    }

    private bool _enablePasteAndDeleteHotkey = AppSettings.Default.EnablePasteAndDeleteHotkey;
    public bool EnablePasteAndDeleteHotkey
    {
        get => _enablePasteAndDeleteHotkey;
        set => this.RaiseAndSetIfChanged(ref _enablePasteAndDeleteHotkey, value);
    }

    private string _pasteAndFavoriteHotkey = AppSettings.Default.PasteAndFavoriteHotkey;
    public string PasteAndFavoriteHotkey
    {
        get => _pasteAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _pasteAndFavoriteHotkey, value);
    }

    private bool _enablePasteAndFavoriteHotkey = AppSettings.Default.EnablePasteAndFavoriteHotkey;
    public bool EnablePasteAndFavoriteHotkey
    {
        get => _enablePasteAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _enablePasteAndFavoriteHotkey, value);
    }

    private string _pasteAsPlainTextHotkey = AppSettings.Default.PasteAsPlainTextHotkey;
    public string PasteAsPlainTextHotkey
    {
        get => _pasteAsPlainTextHotkey;
        set => this.RaiseAndSetIfChanged(ref _pasteAsPlainTextHotkey, value);
    }

    private bool _enablePasteAsPlainTextHotkey = AppSettings.Default.EnablePasteAsPlainTextHotkey;
    public bool EnablePasteAsPlainTextHotkey
    {
        get => _enablePasteAsPlainTextHotkey;
        set => this.RaiseAndSetIfChanged(ref _enablePasteAsPlainTextHotkey, value);
    }

    // --- Settings search / sections ---

    private readonly List<SettingsSectionViewModel> _sections = [];

    private string _filter = string.Empty;

    /// <summary>
    /// The settings-search box. While it is non-empty only matching sections
    /// show, and those that match are opened so the hit is visible without a
    /// second click.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _filter, value);
            RefreshSectionVisibility();
        }
    }

    private bool _useFuzzySearch = AppSettings.Default.UseFuzzySettingsSearch;

    /// <summary>
    /// Whether a filter that matches no keyword exactly falls back to fuzzy
    /// matching. Persisted, so it is part of the draft rather than session state.
    /// </summary>
    public bool UseFuzzySearch
    {
        get => _useFuzzySearch;
        set
        {
            if (_useFuzzySearch == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _useFuzzySearch, value);
            RefreshSectionVisibility();
        }
    }

    public SettingsSectionViewModel BehaviorSection { get; }
    public SettingsSectionViewModel LocalHotkeysSection { get; }
    public SettingsSectionViewModel GlobalHotkeySection { get; }
    public SettingsSectionViewModel StorageSection { get; }
    public SettingsSectionViewModel ToolsSection { get; }
    public SettingsSectionViewModel RetentionSection { get; }
    public SettingsSectionViewModel CapacitySection { get; }
    public SettingsSectionViewModel SensitivitySection { get; }
    public SettingsSectionViewModel ExcludedAppsSection { get; }
    public SettingsSectionViewModel AiSection { get; }
    public SettingsSectionViewModel UpdatesSection { get; }
    public SettingsSectionViewModel OcrSection { get; }
    public SettingsSectionViewModel SemanticSection { get; }

    public SettingsViewModel()
    {
        BehaviorSection = Section(
            "theme dark light tray minimize close start windows startup behavior appearance");
        LocalHotkeysSection = Section(
            "hotkey shortcut local regex favorite sensitive case wildcard whole word pasted toggle");
        GlobalHotkeySection = Section(
            "hotkey shortcut global toggle window show hide incremental decremental paste");
        StorageSection = Section(
            "storage database path password encryption sqlite file location clipangel import legacy migration");
        ToolsSection = Section(
            "tools external editor diff winmerge beyond compare vscode meld kdiff");
        RetentionSection = Section(
            "retention lifetime expiry expire clips days normal sensitive minutes age");
        CapacitySection = Section(
            "capacity size library entries count limit max megabytes clip kb kilobytes");
        SensitivitySection = Section(
            "sensitivity rules pattern regex severity warn block name enabled");
        ExcludedAppsSection = Section(
            "excluded exclude exclusion ignore app apps application blocklist blacklist password manager keepass 1password bitwarden privacy capture source process never",
            isExpanded: false);
        AiSection = Section(
            "ai openai chatgpt gpt model api key base url prompt transform",
            isExpanded: false);
        UpdatesSection = Section(
            "update updates auto-update velopack feed url release version",
            isExpanded: false);
        OcrSection = Section(
            "ocr image text extract recognition language bcp-47 windows.media.ocr",
            isExpanded: false);
        SemanticSection = Section(
            "semantic embedding embeddings similarity vector search meaning ai ml rerun reembed sort relevance date proximity",
            isExpanded: false);
    }

    private SettingsSectionViewModel Section(string keywords, bool isExpanded = true)
    {
        var section = new SettingsSectionViewModel(keywords, MatchesFilter, isExpanded);
        _sections.Add(section);
        return section;
    }

    private bool MatchesFilter(string keywords)
    {
        if (string.IsNullOrWhiteSpace(_filter))
        {
            return true;
        }

        var filter = _filter.Trim();
        if (keywords.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return UseFuzzySearch && Services.FuzzyMatcher.SettingsMatch(keywords, filter);
    }

    /// <summary>
    /// Re-evaluates every section against the filter. Both loops run over
    /// <c>_sections</c> rather than naming the sections, so a section that
    /// exists necessarily participates in both.
    /// </summary>
    private void RefreshSectionVisibility()
    {
        foreach (var section in _sections)
        {
            section.RaiseVisibilityChanged();
        }

        // An empty filter matches everything, so auto-expanding on it would
        // throw away whatever the user had collapsed.
        if (string.IsNullOrWhiteSpace(_filter))
        {
            return;
        }

        foreach (var section in _sections)
        {
            section.IsExpanded = section.IsVisible;
        }
    }
}
