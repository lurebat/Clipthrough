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
/// apply stays in the host). Grown one settings section per commit — currently
/// the AI section.
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
}
