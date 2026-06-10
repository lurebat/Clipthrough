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

    // --- Remote API ---

    private bool _enableRemoteApi = AppSettings.Default.EnableRemoteApi;
    public bool EnableRemoteApi
    {
        get => _enableRemoteApi;
        set => this.RaiseAndSetIfChanged(ref _enableRemoteApi, value);
    }

    private int _remoteApiPort = AppSettings.Default.RemoteApiPort;
    public int RemoteApiPort
    {
        get => _remoteApiPort;
        set
        {
            this.RaiseAndSetIfChanged(ref _remoteApiPort, value);
            this.RaisePropertyChanged(nameof(RemoteApiDocsUrl));
            this.RaisePropertyChanged(nameof(RemoteApiSchemaUrl));
        }
    }

    private string _remoteApiToken = AppSettings.Default.RemoteApiToken;
    public string RemoteApiToken
    {
        get => _remoteApiToken;
        set => this.RaiseAndSetIfChanged(ref _remoteApiToken, value);
    }

    private bool _isRemoteApiTokenRevealed;
    public bool IsRemoteApiTokenRevealed
    {
        get => _isRemoteApiTokenRevealed;
        set => this.RaiseAndSetIfChanged(ref _isRemoteApiTokenRevealed, value);
    }

    private string _remoteApiBindAddress = AppSettings.Default.RemoteApiBindAddress;
    public string RemoteApiBindAddress
    {
        get => _remoteApiBindAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref _remoteApiBindAddress, value);
            this.RaisePropertyChanged(nameof(RemoteApiBindAddressIsNonLoopback));
            this.RaisePropertyChanged(nameof(RemoteApiDocsUrl));
            this.RaisePropertyChanged(nameof(RemoteApiSchemaUrl));
        }
    }

    public bool RemoteApiBindAddressIsNonLoopback
    {
        get
        {
            var v = (_remoteApiBindAddress ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(v)) return false;
            return !(v.Equals("127.0.0.1", StringComparison.Ordinal)
                || v.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || v.Equals("loopback", StringComparison.OrdinalIgnoreCase)
                || v.Equals("::1", StringComparison.Ordinal));
        }
    }

    private string RemoteApiUrlHost
    {
        get
        {
            var v = (_remoteApiBindAddress ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(v)) return "127.0.0.1";
            if (v.Equals("0.0.0.0", StringComparison.Ordinal) || v.Equals("loopback", StringComparison.OrdinalIgnoreCase))
                return "127.0.0.1";
            if (v.Equals("::", StringComparison.Ordinal)) return "[::1]";
            if (v.Contains(':') && !v.StartsWith("[", StringComparison.Ordinal)) return $"[{v}]";
            return v;
        }
    }

    public string RemoteApiDocsUrl => $"http://{RemoteApiUrlHost}:{_remoteApiPort}/docs";
    public string RemoteApiSchemaUrl => $"http://{RemoteApiUrlHost}:{_remoteApiPort}/openapi/v1.json";

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
}
