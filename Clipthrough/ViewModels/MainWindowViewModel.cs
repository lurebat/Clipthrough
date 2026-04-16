using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 200;
    private static readonly IBrush s_defaultDetailBorderBrush = new SolidColorBrush(Color.Parse("#243247"));
    private static readonly IBrush s_defaultDetailAccentBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush s_warningBadgeBackgroundBrush = new SolidColorBrush(Color.Parse("#3A2807"));
    private static readonly IBrush s_warningBadgeBorderBrush = new SolidColorBrush(Color.Parse("#A16207"));
    private static readonly IBrush s_warningBadgeForegroundBrush = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly IBrush s_criticalBadgeBackgroundBrush = new SolidColorBrush(Color.Parse("#3B0D18"));
    private static readonly IBrush s_criticalBadgeBorderBrush = new SolidColorBrush(Color.Parse("#BE123C"));
    private static readonly IBrush s_criticalBadgeForegroundBrush = new SolidColorBrush(Color.Parse("#FDA4AF"));
    private static readonly FilePickerFileType s_databaseFileType = new("SQLite database")
    {
        Patterns = ["*.db", "*.sqlite", "*.sqlite3"],
    };

    private readonly IClipStoreService _clipStoreService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly IClipSampleDataService _clipSampleDataService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly IStorageOptionsService _storageOptionsService;
    private readonly ISensitivityService _sensitivityService;
    private readonly IAppNotificationService _notificationService;
    private readonly IClipExportService _clipExportService;
    private readonly IImageEditorService _imageEditorService;
    private readonly ISearchHistoryService _searchHistoryService;
    private readonly IAiTransformService _aiTransformService;
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly Dictionary<long, (ClipEntry Clip, CancellationTokenSource Cts)> _pendingDeletes = new();

    private DateTimeOffset? _lastCapturedAtRaw;
    private bool _suppressEditAutoSave;
    private string _searchText = string.Empty;
    private ContentTypeOption _selectedContentTypeOption = new(null);
    private bool _showFavoritesOnly;
    private bool _showSensitiveOnly;
    private bool _useRegexSearch;
    private bool _caseSensitiveSearch;
    private bool _useWildcardSearch;
    private bool _wholeWordSearch;
    private bool _showPastedOnly;
    private ClipItemViewModel? _selectedClip;
    private ClipFileItemViewModel? _selectedFileItem;
    private bool _hasMoreResults;
    private bool _isBusy;
    private string _statusText = AppText.LoadingStatus;
    private int _currentOffset;
    private int _matchingClipCount;
    private int _totalClipCount;
    private int _sensitiveClipCount;
    private long _totalStoredBytes;
    private string _lastCaptureSummary = AppText.WaitingForFirstCapture;
    private ContentDisplayMode _contentDisplayMode;
    private string _selectedClipRenderedText = AppText.PreviewSelectContent;
    private string _selectedClipImageHint = AppText.PreviewSelectImage;
    private bool _isStartupInProgress;
    private bool _isDatabaseReady;
    private bool _isStarted;
    private bool _isSettingsOpen;
    private bool _isWelcomeOpen;
    private bool _isPasswordPromptOpen;
    private bool _isAiPromptOpen;
    private string _aiPromptInput = string.Empty;
    private string _aiPromptError = string.Empty;
    private bool _isAiPromptBusy;
    private string _passwordPromptInput = string.Empty;
    private string _passwordPromptError = string.Empty;
    private bool _isPasswordPromptPasswordVisible;
    private bool _isDatabasePasswordVisible;
    private string _settingsToggleRegexHotkey = AppSettings.Default.ToggleRegexHotkey;
    private bool _settingsEnableToggleRegexHotkey = AppSettings.Default.EnableToggleRegexHotkey;
    private string _settingsToggleFavoritesHotkey = AppSettings.Default.ToggleFavoritesHotkey;
    private bool _settingsEnableToggleFavoritesHotkey = AppSettings.Default.EnableToggleFavoritesHotkey;
    private string _settingsToggleSensitiveHotkey = AppSettings.Default.ToggleSensitiveHotkey;
    private bool _settingsEnableToggleSensitiveHotkey = AppSettings.Default.EnableToggleSensitiveHotkey;
    private string _settingsToggleCaseSensitiveHotkey = AppSettings.Default.ToggleCaseSensitiveHotkey;
    private bool _settingsEnableToggleCaseSensitiveHotkey = AppSettings.Default.EnableToggleCaseSensitiveHotkey;
    private string _settingsToggleWindowHotkey = AppSettings.Default.ToggleWindowHotkey;
    private bool _settingsEnableToggleWindowHotkey = AppSettings.Default.EnableToggleWindowHotkey;
    private string _settingsMaxClipSizeKilobytes = (AppSettings.Default.MaxClipSizeBytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture);
    private string _settingsDatabasePath = StorageOptions.Default.DatabasePath;
    private string _settingsDatabasePassword = StorageOptions.Default.DatabasePassword;
    private bool _settingsCloseToTray = AppSettings.Default.CloseToTray;
    private bool _settingsMinimizeToTray = AppSettings.Default.MinimizeToTray;
    private bool _settingsStartWithWindows = AppSettings.Default.StartWithWindows;
    private ThemeMode _settingsThemeMode = AppSettings.Default.ThemeMode;
    private bool _settingsEnableNormalClipLifetime = AppSettings.Default.EnableNormalClipLifetime;
    private string _settingsNormalClipLifetimeDays = AppSettings.Default.NormalClipLifetimeDays.ToString(CultureInfo.InvariantCulture);
    private bool _settingsEnableSensitiveClipLifetime = AppSettings.Default.EnableSensitiveClipLifetime;
    private string _settingsSensitiveClipLifetimeMinutes = AppSettings.Default.SensitiveClipLifetimeMinutes.ToString(CultureInfo.InvariantCulture);
    private bool _settingsEnableMaxLibrarySize = AppSettings.Default.EnableMaxLibrarySize;
    private string _settingsMaxLibrarySizeMegabytes = AppSettings.Default.MaxLibrarySizeMegabytes.ToString(CultureInfo.InvariantCulture);
    private bool _settingsEnableMaxEntryCount = AppSettings.Default.EnableMaxEntryCount;
    private string _settingsMaxEntryCount = AppSettings.Default.MaxEntryCount.ToString(CultureInfo.InvariantCulture);
    private string _settingsToggleWildcardHotkey = AppSettings.Default.ToggleWildcardHotkey;
    private bool _settingsEnableToggleWildcardHotkey = AppSettings.Default.EnableToggleWildcardHotkey;
    private string _settingsToggleWholeWordHotkey = AppSettings.Default.ToggleWholeWordHotkey;
    private bool _settingsEnableToggleWholeWordHotkey = AppSettings.Default.EnableToggleWholeWordHotkey;
    private string _settingsTogglePastedHotkey = AppSettings.Default.TogglePastedHotkey;
    private bool _settingsEnableTogglePastedHotkey = AppSettings.Default.EnableTogglePastedHotkey;
    private string _settingsIncrementalPasteHotkey = AppSettings.Default.IncrementalPasteHotkey;
    private bool _settingsEnableIncrementalPasteHotkey = AppSettings.Default.EnableIncrementalPasteHotkey;
    private string _settingsDecrementalPasteHotkey = AppSettings.Default.DecrementalPasteHotkey;
    private bool _settingsEnableDecrementalPasteHotkey = AppSettings.Default.EnableDecrementalPasteHotkey;
    private string _settingsExternalEditorPath = AppSettings.Default.ExternalEditorPath;
    private string _settingsExternalDiffToolPath = AppSettings.Default.ExternalDiffToolPath;
    private bool _settingsEnableAi = AppSettings.Default.EnableAi;
    private string _settingsAiBaseUrl = AppSettings.Default.AiBaseUrl;
    private string _settingsAiApiKey = AppSettings.Default.AiApiKey;
    private string _settingsAiModel = AppSettings.Default.AiModel;
    private string _editedClipText = string.Empty;
    private string _editedClipBaseline = string.Empty;
    private long? _checkedSelectionAnchorId;

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService, IClipSampleDataService clipSampleDataService, ISettingsService settingsService, ISystemInteractionService systemInteractionService, IStorageOptionsService storageOptionsService, ISensitivityService sensitivityService, IAppNotificationService notificationService, ISessionLogService sessionLogService, IClipExportService clipExportService, IImageEditorService imageEditorService, ISearchHistoryService searchHistoryService, IAiTransformService aiTransformService, DatabaseInitializer databaseInitializer)
    {
        _clipStoreService = clipStoreService;
        _clipboardMonitorService = clipboardMonitorService;
        _clipSampleDataService = clipSampleDataService;
        _settingsService = settingsService;
        _systemInteractionService = systemInteractionService;
        _storageOptionsService = storageOptionsService;
        _sensitivityService = sensitivityService;
        _notificationService = notificationService;
        _clipExportService = clipExportService;
        _imageEditorService = imageEditorService;
        _searchHistoryService = searchHistoryService;
        _aiTransformService = aiTransformService;
        _databaseInitializer = databaseInitializer;
        SessionLogs = new SessionLogsViewModel(sessionLogService);
        ContentTypeOptions =
        [
            new ContentTypeOption(null),
            new ContentTypeOption(ContentType.Text),
            new ContentTypeOption(ContentType.Image),
            new ContentTypeOption(ContentType.RichText),
            new ContentTypeOption(ContentType.Files),
        ];
        _selectedContentTypeOption = ContentTypeOptions[0];
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync, this.WhenAnyValue(x => x.HasMoreResults, x => x.IsBusy, static (hasMore, isBusy) => hasMore && !isBusy));

        var hasSelection = this.WhenAnyValue(x => x.SelectedClip).Select(static clip => clip is not null);
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync, hasSelection);
        TogglePinCommand = ReactiveCommand.CreateFromTask(TogglePinAsync, hasSelection);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasSelection);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);
        ExportSelectedCommand = ReactiveCommand.CreateFromTask(ExportSelectedAsync, hasSelection);
        OpenInEditorCommand = ReactiveCommand.CreateFromTask(OpenInEditorAsync, hasSelection);
        CompareClipsCommand = ReactiveCommand.CreateFromTask(CompareClipsAsync);
        OpenSelectedClipSourceUrlCommand = ReactiveCommand.CreateFromTask(OpenSelectedClipSourceUrlAsync);
        CopySelectedClipWindowTitleCommand = ReactiveCommand.CreateFromTask(CopySelectedClipWindowTitleAsync);
        EditSelectedImageCommand = ReactiveCommand.CreateFromTask(EditSelectedImageAsync);
        SelectAllClipsCommand = ReactiveCommand.Create(SelectAllClips);
        SelectNoClipsCommand = ReactiveCommand.Create(SelectNoClips);
        FavoriteCheckedClipsCommand = ReactiveCommand.CreateFromTask(FavoriteCheckedClipsAsync);
        PinCheckedClipsCommand = ReactiveCommand.CreateFromTask(PinCheckedClipsAsync);
        DeleteCheckedClipsCommand = ReactiveCommand.CreateFromTask(DeleteCheckedClipsAsync);
        CopyEditedClipCommand = ReactiveCommand.CreateFromTask(CopyEditedClipAsync);
        ApplyTextTransformationCommand = ReactiveCommand.CreateFromTask<TextTransformation>(ApplyTextTransformationAsync);
        AddSensitivityRuleCommand = ReactiveCommand.Create(AddSensitivityRule);
        OpenSettingsCommand = ReactiveCommand.Create(OpenSettings);
        OpenHelpCommand = ReactiveCommand.Create(OpenHelp);
        CloseSettingsCommand = ReactiveCommand.Create(CloseSettings);
        SaveSettingsCommand = ReactiveCommand.CreateFromTask(SaveSettingsAsync);
        BrowseDatabasePathCommand = ReactiveCommand.CreateFromTask<Window?>(BrowseDatabasePathAsync);
        UnlockDatabaseCommand = ReactiveCommand.CreateFromTask(UnlockDatabaseAsync);
        ExitApplicationCommand = ReactiveCommand.Create(ExitApplication);
        OpenAiPromptCommand = ReactiveCommand.Create(OpenAiPrompt);
        SubmitAiPromptCommand = ReactiveCommand.CreateFromTask(SubmitAiPromptAsync);
        CancelAiPromptCommand = ReactiveCommand.Create(CancelAiPrompt);

        _settingsService.SettingsChanged += OnSettingsChanged;

        _subscriptions.Add(
            Observable.Merge(
                this.WhenAnyValue(
                        x => x.SearchText,
                        x => x.SelectedContentTypeOption,
                        x => x.ShowFavoritesOnly,
                        x => x.ShowSensitiveOnly,
                        x => x.UseRegexSearch,
                        x => x.CaseSensitiveSearch)
                    .Select(static _ => Unit.Default),
                this.WhenAnyValue(
                        x => x.UseWildcardSearch,
                        x => x.WholeWordSearch,
                        x => x.ShowPastedOnly,
                        x => x.UseFuzzyClipSearch)
                    .Select(static _ => Unit.Default))
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .InvokeCommand(RefreshCommand));

        _subscriptions.Add(
            _clipboardMonitorService.CapturedClips
                .ObserveOn(RxApp.MainThreadScheduler)
                .SelectMany(clip => Observable.FromAsync(() => RefreshAsync(clip.Id)))
                .Subscribe(_ => { }, ex => StatusText = AppText.FormatErrorStatus(ex.Message)));

        _subscriptions.Add(
            _notificationService.Notifications
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(ShowNotification));

        _subscriptions.Add(
            Observable.Interval(TimeSpan.FromSeconds(10), RxApp.MainThreadScheduler)
                .Subscribe(_ => RefreshLastCaptureSummary()));

        _subscriptions.Add(
            RefreshCommand.ThrownExceptions
                .Merge(LoadMoreCommand.ThrownExceptions)
                .Merge(ToggleFavoriteCommand.ThrownExceptions)
                .Merge(TogglePinCommand.ThrownExceptions)
                .Merge(CopySelectedCommand.ThrownExceptions)
                .Merge(ExportSelectedCommand.ThrownExceptions)
                .Merge(OpenInEditorCommand.ThrownExceptions)
                .Merge(CompareClipsCommand.ThrownExceptions)
                .Merge(EditSelectedImageCommand.ThrownExceptions)
                .Merge(DeleteSelectedCommand.ThrownExceptions)
                .Merge(FavoriteCheckedClipsCommand.ThrownExceptions)
                .Merge(PinCheckedClipsCommand.ThrownExceptions)
                .Merge(DeleteCheckedClipsCommand.ThrownExceptions)
                .Merge(CopyEditedClipCommand.ThrownExceptions)
                .Merge(SaveSettingsCommand.ThrownExceptions)
                .Merge(BrowseDatabasePathCommand.ThrownExceptions)
                .Merge(UnlockDatabaseCommand.ThrownExceptions)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(ex => StatusText = AppText.FormatErrorStatus(ex.Message)));
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public ObservableCollection<string> RecentSearches { get; } = [];

    public ObservableCollection<ClipFileItemViewModel> SelectedClipFiles { get; } = [];

    public ObservableCollection<AppNotificationViewModel> Notifications { get; } = [];

    public ObservableCollection<SensitivityRuleEditorViewModel> SensitivityRules { get; } = [];

    public IReadOnlyList<ContentTypeOption> ContentTypeOptions { get; }

    public IReadOnlyList<string> SensitivitySeverityOptions { get; } = ["info", "warning", "critical"];

    public SessionLogsViewModel SessionLogs { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> TogglePinCommand { get; }

    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportSelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenInEditorCommand { get; }

    public ReactiveCommand<Unit, Unit> CompareClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSelectedClipSourceUrlCommand { get; }

    public ReactiveCommand<Unit, Unit> CopySelectedClipWindowTitleCommand { get; }

    public ReactiveCommand<Unit, Unit> EditSelectedImageCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectAllClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectNoClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> FavoriteCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> PinCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyEditedClipCommand { get; }

    public ReactiveCommand<TextTransformation, Unit> ApplyTextTransformationCommand { get; }

    public ReactiveCommand<Unit, Unit> AddSensitivityRuleCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }

    public ReactiveCommand<Window?, Unit> BrowseDatabasePathCommand { get; }

    public ReactiveCommand<Unit, Unit> UnlockDatabaseCommand { get; }

    public ReactiveCommand<Unit, Unit> ExitApplicationCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenAiPromptCommand { get; }

    public ReactiveCommand<Unit, Unit> SubmitAiPromptCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelAiPromptCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            RaiseFilterStateProperties();
        }
    }

    public ContentTypeOption SelectedContentTypeOption
    {
        get => _selectedContentTypeOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedContentTypeOption, value);
            RaiseFilterStateProperties();
            RaiseContentTypeToggleProperties();
        }
    }

    public bool ShowFavoritesOnly
    {
        get => _showFavoritesOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFavoritesOnly, value);
            RaiseFilterStateProperties();
        }
    }

    public bool ShowSensitiveOnly
    {
        get => _showSensitiveOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showSensitiveOnly, value);
            RaiseFilterStateProperties();
        }
    }

    public bool UseRegexSearch
    {
        get => _useRegexSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _useRegexSearch, value);
            RaiseFilterStateProperties();
        }
    }

    public bool CaseSensitiveSearch
    {
        get => _caseSensitiveSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _caseSensitiveSearch, value);
            RaiseFilterStateProperties();
        }
    }

    public bool UseWildcardSearch
    {
        get => _useWildcardSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _useWildcardSearch, value);
            RaiseFilterStateProperties();
        }
    }

    public bool WholeWordSearch
    {
        get => _wholeWordSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _wholeWordSearch, value);
            RaiseFilterStateProperties();
        }
    }

    public bool ShowPastedOnly
    {
        get => _showPastedOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showPastedOnly, value);
            RaiseFilterStateProperties();
        }
    }

    public bool ShowRawContent
    {
        get => _contentDisplayMode == ContentDisplayMode.Raw;
        set
        {
            // Legacy compat: toggling raw on/off maps to Raw vs Rendered
            SelectedContentDisplayMode = value ? ContentDisplayMode.Raw : ContentDisplayMode.Rendered;
        }
    }

    public ContentDisplayMode SelectedContentDisplayMode
    {
        get => _contentDisplayMode;
        set
        {
            if (_contentDisplayMode == value)
            {
                return;
            }

            _contentDisplayMode = value;
            this.RaisePropertyChanged(nameof(SelectedContentDisplayMode));
            this.RaisePropertyChanged(nameof(ShowRawContent));
            SyncEditedClipText();
            RaiseRenderModeProperties();
            PersistContentDisplayModeInBackground(value);
        }
    }

    public ContentDisplayMode[] DisplayModeOptions { get; } =
    [
        ContentDisplayMode.Rendered,
        ContentDisplayMode.Textual,
        ContentDisplayMode.Raw,
    ];

    public bool IsRenderedMode
    {
        get => _contentDisplayMode == ContentDisplayMode.Rendered;
        set { if (value) SelectedContentDisplayMode = ContentDisplayMode.Rendered; }
    }

    public bool IsTextualMode
    {
        get => _contentDisplayMode == ContentDisplayMode.Textual;
        set { if (value) SelectedContentDisplayMode = ContentDisplayMode.Textual; }
    }

    public bool IsRawMode
    {
        get => _contentDisplayMode == ContentDisplayMode.Raw;
        set { if (value) SelectedContentDisplayMode = ContentDisplayMode.Raw; }
    }

    public bool IsAllTypeSelected
    {
        get => _selectedContentTypeOption.Value is null;
        set { if (value) SelectedContentTypeOption = ContentTypeOptions[0]; }
    }

    public bool IsTextTypeSelected
    {
        get => _selectedContentTypeOption.Value == ContentType.Text;
        set { if (value) SelectedContentTypeOption = ContentTypeOptions[1]; }
    }

    public bool IsImageTypeSelected
    {
        get => _selectedContentTypeOption.Value == ContentType.Image;
        set { if (value) SelectedContentTypeOption = ContentTypeOptions[2]; }
    }

    public bool IsRichTextTypeSelected
    {
        get => _selectedContentTypeOption.Value == ContentType.RichText;
        set { if (value) SelectedContentTypeOption = ContentTypeOptions[3]; }
    }

    public bool IsFilesTypeSelected
    {
        get => _selectedContentTypeOption.Value == ContentType.Files;
        set { if (value) SelectedContentTypeOption = ContentTypeOptions[4]; }
    }

    public bool HasCheckedOrSelectedClip => HasCheckedClips || HasSelectedClip;

    public bool IsCompareAvailable => !string.IsNullOrWhiteSpace(_settingsService.Current.ExternalDiffToolPath);

    public ClipItemViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            // When deselecting while clips exist, auto-select the first clip.
            if (value is null && Clips.Count > 0)
            {
                value = Clips[0];
            }

            if (_selectedClip == value)
            {
                return;
            }

            // Auto-save edited text as a new clip before switching away.
            _ = CommitEditedClipOnSelectionChangeAsync();

            this.RaiseAndSetIfChanged(ref _selectedClip, value);
            if (value is not null)
            {
                _checkedSelectionAnchorId = value.Id;
            }
            UpdateSelectedClipPresentation();
            RaiseSelectionStateProperties();
        }
    }

    public ClipFileItemViewModel? SelectedFileItem
    {
        get => _selectedFileItem;
        set => this.RaiseAndSetIfChanged(ref _selectedFileItem, value);
    }

    public bool HasMoreResults
    {
        get => _hasMoreResults;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasMoreResults, value);
            this.RaisePropertyChanged(nameof(ClipboardStateText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(ClipboardStateText));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public int MatchingClipCount
    {
        get => _matchingClipCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _matchingClipCount, value);
            this.RaisePropertyChanged(nameof(MatchingClipCountText));
        }
    }

    public int TotalClipCount
    {
        get => _totalClipCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalClipCount, value);
            this.RaisePropertyChanged(nameof(EntryUsageText));
            this.RaisePropertyChanged(nameof(EntryCapacityText));
            this.RaisePropertyChanged(nameof(EntryUsagePercent));
        }
    }

    public int SensitiveClipCount
    {
        get => _sensitiveClipCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _sensitiveClipCount, value);
            this.RaisePropertyChanged(nameof(SensitiveClipCountText));
        }
    }

    public long TotalStoredBytes
    {
        get => _totalStoredBytes;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalStoredBytes, value);
            this.RaisePropertyChanged(nameof(StorageUsageText));
            this.RaisePropertyChanged(nameof(StorageCapacityText));
            this.RaisePropertyChanged(nameof(StorageUsagePercent));
        }
    }

    public string LastCaptureSummary
    {
        get => _lastCaptureSummary;
        private set => this.RaiseAndSetIfChanged(ref _lastCaptureSummary, value);
    }

    public string WindowTitle => AppText.WindowTitle;

    public string HeroTitle => AppText.WindowTitle;

    public string SearchWatermark => AppText.SearchWatermark;

    public string ClipboardHistoryCaptionText => AppText.ClipboardHistoryCaption;

    public string ClipsPanelTitleText => AppText.ClipsPanelTitle;

    public string FavoritesFilterLabel => AppText.FavoritesFilterLabel;

    public string SensitiveFilterLabel => AppText.SensitiveFilterLabel;

    public string RegexFilterLabel => AppText.RegexFilterLabel;

    public string RefreshButtonLabel => AppText.RefreshButtonLabel;

    public string RawToggleLabel => AppText.RawToggleLabel;

    public bool IsDisplayModeApplicable => HasSelectedClip
        && SelectedClip?.Clip.ContentType is ContentType.RichText or ContentType.Files;

    public string CopyButtonLabel => AppText.CopyButtonLabel;

    public string DeleteButtonLabel => AppText.DeleteButtonLabel;

    public string ExportButtonLabel => AppText.ExportButtonLabel;

    public string FavoriteBadgeLabel => AppText.FavoriteBadgeLabel;

    public string FavoriteButtonLabel => AppText.FavoriteButtonLabel;

    public string SelectedClipFavoriteButtonLabel => AppText.FavoriteButtonLabel;

    public string SelectedClipPinButtonLabel => IsSelectedClipPinned ? "📌 Unpin" : "📌 Pin";

    public string SelectAllButtonLabel => AppText.SelectAllButtonLabel;

    public string SelectNoneButtonLabel => AppText.SelectNoneButtonLabel;

    public string FavoriteSelectedButtonLabel => AppText.FavoriteSelectedButtonLabel;

    public string CopyAsNewButtonLabel => AppText.CopyAsNewButtonLabel;

    public string EditImageButtonLabel => AppText.EditImageButtonLabel;

    public string ResetImageEditsButtonLabel => AppText.ResetImageEditsButtonLabel;

    public string LogsButtonLabel => AppText.LogsButtonLabel;

    public string CaseSensitiveFilterLabel => AppText.CaseSensitiveFilterLabel;

    public string SettingsButtonLabel => AppText.SettingsButtonLabel;

    public string CloseButtonLabel => AppText.CloseButtonLabel;

    public string SettingsTitleText => AppText.SettingsTitleText;

    public string SettingsDescriptionText => AppText.SettingsDescriptionText;

    public string SettingsLocalHotkeysTitle => AppText.SettingsLocalHotkeysTitle;

    public string SettingsGlobalHotkeyTitle => AppText.SettingsGlobalHotkeyTitle;

    public string SettingsStorageTitle => AppText.SettingsStorageTitle;

    public string SettingsBehaviorTitle => AppText.SettingsBehaviorTitle;

    public string SettingsRetentionTitle => AppText.SettingsRetentionTitle;

    public string SettingsCapacityTitle => AppText.SettingsCapacityTitle;

    public string SettingsSensitivityTitle => AppText.SettingsSensitivityTitle;

    public string SettingsClipLimitLabel => AppText.SettingsClipLimitLabel;

    public string SettingsDatabasePathLabel => AppText.SettingsDatabasePathLabel;

    public string SettingsDatabasePasswordLabel => AppText.SettingsDatabasePasswordLabel;

    public string SettingsBrowseDatabasePathButtonLabel => AppText.SettingsBrowseDatabasePathButtonLabel;

    public string SettingsShowPasswordLabel => AppText.SettingsShowPasswordLabel;

    public string SettingsRegexHotkeyLabel => AppText.SettingsRegexHotkeyLabel;

    public string SettingsFavoritesHotkeyLabel => AppText.SettingsFavoritesHotkeyLabel;

    public string SettingsSensitiveHotkeyLabel => AppText.SettingsSensitiveHotkeyLabel;

    public string SettingsCaseSensitiveHotkeyLabel => AppText.SettingsCaseSensitiveHotkeyLabel;

    public string SettingsToggleWindowHotkeyLabel => AppText.SettingsToggleWindowHotkeyLabel;

    public string SettingsEnableShortcutLabel => AppText.SettingsEnableShortcutLabel;

    public string SettingsCloseToTrayLabel => AppText.SettingsCloseToTrayLabel;

    public string SettingsMinimizeToTrayLabel => AppText.SettingsMinimizeToTrayLabel;

    public string SettingsStartWithWindowsLabel => AppText.SettingsStartWithWindowsLabel;

    public string SettingsNormalClipLifetimeLabel => AppText.SettingsNormalClipLifetimeLabel;

    public string SettingsSensitiveClipLifetimeLabel => AppText.SettingsSensitiveClipLifetimeLabel;

    public string SettingsMaxLibrarySizeLabel => AppText.SettingsMaxLibrarySizeLabel;

    public string SettingsMaxEntryCountLabel => AppText.SettingsMaxEntryCountLabel;

    public string SettingsRuleNameLabel => AppText.SettingsRuleNameLabel;

    public string SettingsRulePatternLabel => AppText.SettingsRulePatternLabel;

    public string SettingsRuleSeverityLabel => AppText.SettingsRuleSeverityLabel;

    public string SettingsRuleEnabledLabel => AppText.SettingsRuleEnabledLabel;

    public string SettingsAddRuleButtonLabel => AppText.SettingsAddRuleButtonLabel;

    public string WelcomeTitleText => AppText.WelcomeTitleText;

    public string WelcomeDescriptionText => AppText.WelcomeDescriptionText;

    public string WelcomeSaveButtonLabel => AppText.WelcomeSaveButtonLabel;

    public string SettingsSaveButtonLabel => AppText.SettingsSaveButtonLabel;

    public string SettingsCancelButtonLabel => AppText.SettingsCancelButtonLabel;

    public string SettingsWildcardHotkeyLabel => AppText.SettingsWildcardHotkeyLabel;
    public string SettingsWholeWordHotkeyLabel => AppText.SettingsWholeWordHotkeyLabel;
    public string SettingsPastedHotkeyLabel => AppText.SettingsPastedHotkeyLabel;
    public string SettingsIncrementalPasteHotkeyLabel => AppText.SettingsIncrementalPasteHotkeyLabel;
    public string SettingsDecrementalPasteHotkeyLabel => AppText.SettingsDecrementalPasteHotkeyLabel;
    public string SettingsToolsTitle => AppText.SettingsToolsTitle;
    public string SettingsExternalEditorPathLabel => AppText.SettingsExternalEditorPathLabel;
    public string SettingsExternalDiffToolPathLabel => AppText.SettingsExternalDiffToolPathLabel;
    public string OpenInEditorButtonLabel => AppText.OpenInEditorButtonLabel;
    public string CompareClipsButtonLabel => AppText.CompareClipsButtonLabel;
    public string WildcardFilterLabel => AppText.WildcardFilterLabel;
    public string WholeWordFilterLabel => AppText.WholeWordFilterLabel;
    public string PastedFilterLabel => AppText.PastedFilterLabel;

    public string SettingsHintText => AppText.SettingsHintText;

    public string SettingsStorageHintText => AppText.SettingsStorageHintText;

    public string EmptySelectionTitleText => AppText.EmptySelectionTitle;

    public string EmptySelectionDescriptionText => AppText.EmptySelectionDescription;

    public string SelectedImageTypeText => AppText.ImageClipTitle;

    public string AppLabelText => AppText.AppLabel;

    public string FirstCopiedLabelText => AppText.FirstCopiedLabel;

    public string CapturedLabelText => AppText.CapturedLabel;

    public string ExpiresLabelText => AppText.ExpiresLabel;

    public string CopiesLabelText => AppText.CopiesLabel;

    public string SizeLabelText => AppText.SizeLabel;

    public string ResolutionLabelText => AppText.ResolutionLabel;

    public string SensitivityLabelText => AppText.SensitivityLabel;

    public string MatchingClipCountText => AppText.FormatMatchingCount(MatchingClipCount);

    public string SensitiveClipCountText => AppText.FormatSensitiveCount(SensitiveClipCount);

    public string StorageUsageText => AppText.FormatStorageUsage(TotalStoredBytes);

    public string EntryUsageText => AppText.FormatEntryUsage(TotalClipCount);

    public string StorageCapacityText => BuildStorageCapacityText();

    public string EntryCapacityText => BuildEntryCapacityText();

    public double StorageUsagePercent => BuildUsagePercent(
        TotalStoredBytes,
        SettingsEnableMaxLibrarySize ? ParseIntOrDefault(SettingsMaxLibrarySizeMegabytes, AppSettings.DefaultMaxLibrarySizeMegabytes) * 1024d * 1024d : 0d);

    public double EntryUsagePercent => BuildUsagePercent(
        TotalClipCount,
        SettingsEnableMaxEntryCount ? ParseIntOrDefault(SettingsMaxEntryCount, AppSettings.DefaultMaxEntryCount) : 0d);

    public bool IsSelectedClipFavorite => SelectedClip?.IsFavorite == true;

    public bool IsSelectedClipPinned => SelectedClip?.IsPinned == true;

    public bool HasSelectedClip => SelectedClip is not null;

    public bool ShowEmptySelectionState => !HasSelectedClip;

    public bool ShowRenderedContent => HasSelectedClip
        && _contentDisplayMode == ContentDisplayMode.Rendered;

    public bool ShowRawTextContent => HasSelectedClip
        && SelectedClip?.Clip.ContentType != ContentType.Image
        && (_contentDisplayMode == ContentDisplayMode.Raw
            || SelectedClip?.Clip.ContentType == ContentType.Text);

    public bool ShowSelectedTextRenderer => HasSelectedClip
        && SelectedClip?.Clip.ContentType == ContentType.RichText
        && _contentDisplayMode == ContentDisplayMode.Textual;

    public bool ShowSelectedRichTextRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.RichText;

    public bool ShowSelectedFilesRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Files && HasSelectedClipFileItems;

    public bool ShowSelectedFilesFallback => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Files && !HasSelectedClipFileItems;

    public bool ShowSelectedImageRenderer => HasSelectedClip && SelectedClip?.Clip.ContentType == ContentType.Image;

    public bool ShowSelectedImageEditor => ShowSelectedImageRenderer && SelectedClip?.Clip.ContentBytes is { Length: > 0 };

    public bool ShowSelectedImagePlaceholder => ShowSelectedImageRenderer && !ShowSelectedImageEditor;

    public bool HasSelectedClipFileItems => SelectedClipFiles.Count > 0;

    public bool HasCheckedClips => Clips.Any(static clip => clip.IsChecked);

    public int CheckedClipCount => Clips.Count(static clip => clip.IsChecked);

    public string CheckedClipSummaryText => AppText.FormatCheckedClipCount(CheckedClipCount);

    public bool IsSelectedClipTextEditable =>
        SelectedClip?.Clip.ContentType is ContentType.Text
        || (SelectedClip?.Clip.ContentType is ContentType.RichText
            && _contentDisplayMode is ContentDisplayMode.Textual or ContentDisplayMode.Raw);

    public bool SelectedClipTextIsReadOnly => !IsSelectedClipTextEditable;

    public bool ShowCopyEditedClipButton => IsSelectedClipTextEditable
        && (ShowSelectedTextRenderer || ShowSelectedRichTextRenderer || ShowRawTextContent);

    public bool HasEditedClipChanges => IsSelectedClipTextEditable
        && !string.Equals(_editedClipText, _editedClipBaseline, StringComparison.Ordinal);

    public string SelectedClipRenderedText => _selectedClipRenderedText;

    public string SelectedClipRawContent => ClipDisplayFormatter.GetRawContentDisplay(SelectedClip?.Clip);

    public string RawContentSyntaxHint => SelectedClip?.Clip.ContentFormat switch
    {
        ClipContentFormat.Html => ".html",
        ClipContentFormat.Rtf => "",
        _ => "",
    };

    public string EditedClipText
    {
        get => _editedClipText;
        set
        {
            if (_editedClipText == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _editedClipText, value);
            RaiseEditedClipProperties();
        }
    }

    public IReadOnlyList<TextTransformation> TextTransformationOptions { get; } = Enum.GetValues<TextTransformation>();

    private TextTransformation _selectedTextTransformation = TextTransformation.None;

    public TextTransformation SelectedTextTransformation
    {
        get => _selectedTextTransformation;
        set
        {
            if (_selectedTextTransformation == value)
            {
                return;
            }

            _selectedTextTransformation = value;
            this.RaisePropertyChanged(nameof(SelectedTextTransformation));

            if (value != TextTransformation.None && IsSelectedClipTextEditable && !string.IsNullOrEmpty(_editedClipText))
            {
                var transformed = TextTransformationService.Apply(value, _editedClipText);
                if (!string.Equals(transformed, _editedClipText, StringComparison.Ordinal))
                {
                    EditedClipText = transformed;
                    _ = CopyEditedClipAsync();
                }

                // Snap back to None so re-selecting same transformation re-triggers
                _selectedTextTransformation = TextTransformation.None;
                this.RaisePropertyChanged(nameof(SelectedTextTransformation));
            }
        }
    }

    public byte[]? SelectedClipImageBytes => SelectedClip?.Clip.ContentType == ContentType.Image ? SelectedClip.Clip.ContentBytes : null;

    public string SelectedClipImageHint => _selectedClipImageHint;

    public string SelectedClipContentTypeText => SelectedClip?.DisplayContentType ?? AppText.SelectClipTypeFallback;

    public ClipContentFormat SelectedClipContentFormat => SelectedClip?.Clip.ContentFormat ?? ClipContentFormat.PlainText;

    public string SelectedClipTitleText => SelectedClip?.Title ?? AppText.SelectClipTitleFallback;

    public string SelectedClipSourceText => SelectedClip?.SourceApp ?? AppText.UnknownSource;

    public Bitmap? SelectedClipSourceAppIcon => SelectedClip?.SourceAppIconImage;

    public bool ShowSelectedClipSourceAppIcon => SelectedClip?.HasSourceAppIcon == true;

    public string SelectedClipFirstCopiedAtText => SelectedClip?.FirstCopiedAtDisplay ?? AppText.NotAvailable;

    public string SelectedClipCapturedAtText => SelectedClip?.CapturedAtDisplay ?? AppText.NotAvailable;

    public string SelectedClipCopyCountText => SelectedClip?.CopyCountDisplay ?? AppText.NotAvailable;

    public string SelectedClipByteSizeText => SelectedClip?.ByteSizeDisplay ?? AppText.FormatByteCount(0);

    public string SelectedClipExpiresAtText => BuildSelectedClipExpirationText();

    public string SelectedClipImageResolutionText => SelectedClip?.ImageResolutionDisplay ?? AppText.NotAvailable;

    public bool ShowSelectedImageResolutionCard => SelectedClip?.Clip.ContentType == ContentType.Image;

    public string SelectedClipSensitivityText => SelectedClip?.SensitivitySummary ?? AppText.NoClipSelected;

    public string SelectedClipWindowTitleText => SelectedClip?.SourceWindowTitle ?? string.Empty;

    public bool ShowSelectedClipWindowTitle => SelectedClip?.HasSourceWindowTitle == true;

    public bool HasSelectedClipSourceUrl => SelectedClip?.HasSourceUrl == true;

    public string SelectedClipPastedText => SelectedClip?.PastedMarker ?? string.Empty;

    public bool ShowSelectedClipPasted => SelectedClip?.HasBeenPasted == true;

    public IBrush SelectedClipTypeChipBackground => SelectedClip?.TypeChipBackground ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipTypeChipBorderBrush => SelectedClip?.TypeChipBorderBrush ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipTypeChipForeground => SelectedClip?.TypeChipForeground ?? s_defaultDetailAccentBrush;

    public IBrush SelectedClipAgeChipBackground => SelectedClip?.AgeChipBackground ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipAgeChipBorderBrush => SelectedClip?.AgeChipBorderBrush ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipAgeChipForeground => SelectedClip?.AgeChipForeground ?? s_defaultDetailAccentBrush;

    public IBrush SelectedClipPastedChipBackground => SelectedClip?.PastedChipBackground ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipPastedChipBorderBrush => SelectedClip?.PastedChipBorderBrush ?? s_defaultDetailBorderBrush;
    public IBrush SelectedClipPastedChipForeground => SelectedClip?.PastedChipForeground ?? s_defaultDetailAccentBrush;

    // Size chip (amber-ish, based on byte size)
    private static readonly IBrush s_sizeSmallBg = new SolidColorBrush(Color.Parse("#22263D"));
    private static readonly IBrush s_sizeSmallBorder = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush s_sizeSmallFg = new SolidColorBrush(Color.Parse("#CBD5E1"));
    private static readonly IBrush s_sizeMedBg = new SolidColorBrush(Color.Parse("#3A2807"));
    private static readonly IBrush s_sizeMedBorder = new SolidColorBrush(Color.Parse("#A16207"));
    private static readonly IBrush s_sizeMedFg = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly IBrush s_sizeLargeBg = new SolidColorBrush(Color.Parse("#2D1421"));
    private static readonly IBrush s_sizeLargeBorder = new SolidColorBrush(Color.Parse("#BE123C"));
    private static readonly IBrush s_sizeLargeFg = new SolidColorBrush(Color.Parse("#FDA4AF"));

    private (IBrush Bg, IBrush Border, IBrush Fg) GetSizeColors()
    {
        var bytes = SelectedClip?.Clip.ByteSize ?? 0;
        if (bytes < 10 * 1024) return (s_sizeSmallBg, s_sizeSmallBorder, s_sizeSmallFg);
        if (bytes < 256 * 1024) return (s_sizeMedBg, s_sizeMedBorder, s_sizeMedFg);
        return (s_sizeLargeBg, s_sizeLargeBorder, s_sizeLargeFg);
    }

    public IBrush SelectedClipSizeChipBackground => GetSizeColors().Bg;
    public IBrush SelectedClipSizeChipBorderBrush => GetSizeColors().Border;
    public IBrush SelectedClipSizeChipForeground => GetSizeColors().Fg;

    // Sensitivity chip colors derived from severity
    public IBrush SelectedClipSensitivityChipBackground => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeBackgroundBrush
        : SelectedClip?.IsSensitive == true ? s_warningBadgeBackgroundBrush : s_defaultDetailBorderBrush;
    public IBrush SelectedClipSensitivityChipBorderBrush => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeBorderBrush
        : SelectedClip?.IsSensitive == true ? s_warningBadgeBorderBrush : s_defaultDetailBorderBrush;
    public IBrush SelectedClipSensitivityChipForeground => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeForegroundBrush
        : SelectedClip?.IsSensitive == true ? s_warningBadgeForegroundBrush : s_defaultDetailAccentBrush;

    public IBrush SelectedClipAccentBrush => SelectedClip?.StateAccentBrush ?? s_defaultDetailAccentBrush;

    public IBrush SelectedClipAreaBorderBrush => SelectedClip?.RowBorderBrush ?? s_defaultDetailBorderBrush;

    public Thickness SelectedClipAreaBorderThickness => SelectedClip?.RowBorderThickness ?? new Thickness(1);

    public bool ShowSelectedClipSeverityIndicator => SelectedClip?.IsSensitive == true;

    public string SelectedClipSeverityIndicatorText => SelectedClip is null
        ? string.Empty
        : AppText.GetSeverityBadgeLabel(SelectedClip.HighestSeverity);

    public IBrush SelectedClipSeverityBadgeBackground => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeBackgroundBrush
        : s_warningBadgeBackgroundBrush;

    public IBrush SelectedClipSeverityBadgeBorderBrush => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeBorderBrush
        : s_warningBadgeBorderBrush;

    public IBrush SelectedClipSeverityBadgeForeground => SelectedClip?.HasCriticalSeverity == true
        ? s_criticalBadgeForegroundBrush
        : s_warningBadgeForegroundBrush;

    public bool HasNoClips => Clips.Count == 0;

    public string SelectionStateTitle => HasSelectedClip
        ? AppText.SelectedClipStateTitle
        : AppText.EmptySelectionStateTitle;

    public string ClipboardStateText => IsBusy
        ? AppText.ClipboardRefreshingState
        : HasMoreResults
            ? AppText.ClipboardLoadMoreState
            : AppText.ClipboardLoadedState;

    public string ActiveFilterSummary
    {
        get
        {
            var parts = new List<string>();

            if (SelectedContentTypeOption.Value is not null)
            {
                parts.Add(SelectedContentTypeOption.Label);
            }

            if (ShowFavoritesOnly)
            {
                parts.Add(AppText.FilterFavorites);
            }

            if (ShowSensitiveOnly)
            {
                parts.Add(AppText.FilterSensitive);
            }

            if (UseRegexSearch)
            {
                parts.Add(AppText.FilterRegex);
            }

            if (CaseSensitiveSearch)
            {
                parts.Add(AppText.FilterCaseSensitive);
            }

            if (UseWildcardSearch)
            {
                parts.Add("Wildcard");
            }

            if (WholeWordSearch)
            {
                parts.Add("Whole Word");
            }

            if (ShowPastedOnly)
            {
                parts.Add("Pasted");
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                parts.Add(AppText.FormatSearchFilter(SearchText.Trim()));
            }

            return parts.Count == 0 ? AppText.FilterSummaryAll : string.Join(" · ", parts);
        }
    }

    public string EmptyListMessage => IsBusy
        ? AppText.LoadingStatus
        : UseRegexSearch
            ? AppText.EmptyListRegex
            : AppText.EmptyListDefault;

    public string EmptySessionLogsMessage => AppText.NoLogsMatchFilters;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => this.RaiseAndSetIfChanged(ref _isSettingsOpen, value);
    }

    public bool IsWelcomeOpen
    {
        get => _isWelcomeOpen;
        private set
        {
            if (!this.RaiseAndSetIfChanged(ref _isWelcomeOpen, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(IsMainWorkspaceVisible));
        }
    }

    public bool IsMainWorkspaceVisible => !IsWelcomeOpen && !IsPasswordPromptOpen;

    public bool IsPasswordPromptOpen
    {
        get => _isPasswordPromptOpen;
        private set
        {
            if (!this.RaiseAndSetIfChanged(ref _isPasswordPromptOpen, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(IsMainWorkspaceVisible));
        }
    }

    public string PasswordPromptInput
    {
        get => _passwordPromptInput;
        set => this.RaiseAndSetIfChanged(ref _passwordPromptInput, value);
    }

    public string PasswordPromptError
    {
        get => _passwordPromptError;
        private set => this.RaiseAndSetIfChanged(ref _passwordPromptError, value);
    }

    public bool IsPasswordPromptPasswordVisible
    {
        get => _isPasswordPromptPasswordVisible;
        set => this.RaiseAndSetIfChanged(ref _isPasswordPromptPasswordVisible, value);
    }

    public bool IsAiPromptOpen
    {
        get => _isAiPromptOpen;
        private set => this.RaiseAndSetIfChanged(ref _isAiPromptOpen, value);
    }

    public string AiPromptInput
    {
        get => _aiPromptInput;
        set => this.RaiseAndSetIfChanged(ref _aiPromptInput, value);
    }

    public string AiPromptError
    {
        get => _aiPromptError;
        private set => this.RaiseAndSetIfChanged(ref _aiPromptError, value);
    }

    public bool IsAiPromptBusy
    {
        get => _isAiPromptBusy;
        private set => this.RaiseAndSetIfChanged(ref _isAiPromptBusy, value);
    }

    public bool SettingsEnableAi
    {
        get => _settingsEnableAi;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableAi, value);
    }

    public string SettingsAiBaseUrl
    {
        get => _settingsAiBaseUrl;
        set => this.RaiseAndSetIfChanged(ref _settingsAiBaseUrl, value);
    }

    public string SettingsAiApiKey
    {
        get => _settingsAiApiKey;
        set => this.RaiseAndSetIfChanged(ref _settingsAiApiKey, value);
    }

    public string SettingsAiModel
    {
        get => _settingsAiModel;
        set => this.RaiseAndSetIfChanged(ref _settingsAiModel, value);
    }

    public string SettingsToggleRegexHotkey
    {
        get => _settingsToggleRegexHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleRegexHotkey, value);
    }

    public bool SettingsEnableToggleRegexHotkey
    {
        get => _settingsEnableToggleRegexHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleRegexHotkey, value);
    }

    public string SettingsToggleFavoritesHotkey
    {
        get => _settingsToggleFavoritesHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleFavoritesHotkey, value);
    }

    public bool SettingsEnableToggleFavoritesHotkey
    {
        get => _settingsEnableToggleFavoritesHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleFavoritesHotkey, value);
    }

    public string SettingsToggleSensitiveHotkey
    {
        get => _settingsToggleSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleSensitiveHotkey, value);
    }

    public bool SettingsEnableToggleSensitiveHotkey
    {
        get => _settingsEnableToggleSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleSensitiveHotkey, value);
    }

    public string SettingsToggleCaseSensitiveHotkey
    {
        get => _settingsToggleCaseSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleCaseSensitiveHotkey, value);
    }

    public bool SettingsEnableToggleCaseSensitiveHotkey
    {
        get => _settingsEnableToggleCaseSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleCaseSensitiveHotkey, value);
    }

    public string SettingsToggleWindowHotkey
    {
        get => _settingsToggleWindowHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleWindowHotkey, value);
    }

    public bool SettingsEnableToggleWindowHotkey
    {
        get => _settingsEnableToggleWindowHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleWindowHotkey, value);
    }

    public string SettingsToggleWildcardHotkey
    {
        get => _settingsToggleWildcardHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleWildcardHotkey, value);
    }

    public bool SettingsEnableToggleWildcardHotkey
    {
        get => _settingsEnableToggleWildcardHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleWildcardHotkey, value);
    }

    public string SettingsToggleWholeWordHotkey
    {
        get => _settingsToggleWholeWordHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleWholeWordHotkey, value);
    }

    public bool SettingsEnableToggleWholeWordHotkey
    {
        get => _settingsEnableToggleWholeWordHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleWholeWordHotkey, value);
    }

    public string SettingsTogglePastedHotkey
    {
        get => _settingsTogglePastedHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsTogglePastedHotkey, value);
    }

    public bool SettingsEnableTogglePastedHotkey
    {
        get => _settingsEnableTogglePastedHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableTogglePastedHotkey, value);
    }

    public string SettingsIncrementalPasteHotkey
    {
        get => _settingsIncrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsIncrementalPasteHotkey, value);
    }

    public bool SettingsEnableIncrementalPasteHotkey
    {
        get => _settingsEnableIncrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableIncrementalPasteHotkey, value);
    }

    public string SettingsDecrementalPasteHotkey
    {
        get => _settingsDecrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsDecrementalPasteHotkey, value);
    }

    public bool SettingsEnableDecrementalPasteHotkey
    {
        get => _settingsEnableDecrementalPasteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableDecrementalPasteHotkey, value);
    }

    private string _settingsFilter = string.Empty;

    public string SettingsFilter
    {
        get => _settingsFilter;
        set
        {
            if (_settingsFilter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _settingsFilter, value);
            RaiseSettingsSectionVisibility();
        }
    }

    private bool _settingsUseFuzzySearch = AppSettings.Default.UseFuzzySettingsSearch;

    public bool SettingsUseFuzzySearch
    {
        get => _settingsUseFuzzySearch;
        set
        {
            if (_settingsUseFuzzySearch == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _settingsUseFuzzySearch, value);
            RaiseSettingsSectionVisibility();
        }
    }

    private bool _useFuzzyClipSearch = AppSettings.Default.UseFuzzyClipSearch;

    public bool UseFuzzyClipSearch
    {
        get => _useFuzzyClipSearch;
        set => this.RaiseAndSetIfChanged(ref _useFuzzyClipSearch, value);
    }

    private bool _isSettingsSectionBehaviorExpanded = true;
    private bool _isSettingsSectionLocalHotkeysExpanded = true;
    private bool _isSettingsSectionGlobalHotkeyExpanded = true;
    private bool _isSettingsSectionStorageExpanded = true;
    private bool _isSettingsSectionToolsExpanded = true;
    private bool _isSettingsSectionRetentionExpanded = true;
    private bool _isSettingsSectionCapacityExpanded = true;
    private bool _isSettingsSectionSensitivityExpanded = true;

    public bool IsSettingsSectionBehaviorExpanded
    {
        get => _isSettingsSectionBehaviorExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionBehaviorExpanded, value);
    }

    public bool IsSettingsSectionLocalHotkeysExpanded
    {
        get => _isSettingsSectionLocalHotkeysExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionLocalHotkeysExpanded, value);
    }

    public bool IsSettingsSectionGlobalHotkeyExpanded
    {
        get => _isSettingsSectionGlobalHotkeyExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionGlobalHotkeyExpanded, value);
    }

    public bool IsSettingsSectionStorageExpanded
    {
        get => _isSettingsSectionStorageExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionStorageExpanded, value);
    }

    public bool IsSettingsSectionToolsExpanded
    {
        get => _isSettingsSectionToolsExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionToolsExpanded, value);
    }

    public bool IsSettingsSectionRetentionExpanded
    {
        get => _isSettingsSectionRetentionExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionRetentionExpanded, value);
    }

    public bool IsSettingsSectionCapacityExpanded
    {
        get => _isSettingsSectionCapacityExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionCapacityExpanded, value);
    }

    public bool IsSettingsSectionSensitivityExpanded
    {
        get => _isSettingsSectionSensitivityExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionSensitivityExpanded, value);
    }

    // Keywords searched by SettingsFilter. When filter is empty the section shows.
    // When non-empty, the section shows only if its keyword blob contains the filter.
    private static readonly string _behaviorKeywords = "theme dark light tray minimize close start windows startup behavior appearance";
    private static readonly string _localHotkeyKeywords = "hotkey shortcut local regex favorite sensitive case wildcard whole word pasted toggle";
    private static readonly string _globalHotkeyKeywords = "hotkey shortcut global toggle window show hide incremental decremental paste size limit clip";
    private static readonly string _storageKeywords = "storage database path password encryption sqlite file location";
    private static readonly string _toolsKeywords = "tools external editor diff winmerge beyond compare vscode meld kdiff";
    private static readonly string _retentionKeywords = "retention lifetime expiry expire clips days normal sensitive minutes age";
    private static readonly string _capacityKeywords = "capacity size library entries count limit max megabytes";
    private static readonly string _sensitivityKeywords = "sensitivity rules pattern regex severity warn block name enabled";

    private bool MatchesFilter(string keywords)
    {
        if (string.IsNullOrWhiteSpace(_settingsFilter))
        {
            return true;
        }

        var filter = _settingsFilter.Trim();
        if (keywords.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SettingsUseFuzzySearch && Clipthrough.Services.FuzzyMatcher.SettingsMatch(keywords, filter);
    }

    public bool IsSettingsSectionBehaviorVisible => MatchesFilter(_behaviorKeywords);
    public bool IsSettingsSectionLocalHotkeysVisible => MatchesFilter(_localHotkeyKeywords);
    public bool IsSettingsSectionGlobalHotkeyVisible => MatchesFilter(_globalHotkeyKeywords);
    public bool IsSettingsSectionStorageVisible => MatchesFilter(_storageKeywords);
    public bool IsSettingsSectionToolsVisible => MatchesFilter(_toolsKeywords);
    public bool IsSettingsSectionRetentionVisible => MatchesFilter(_retentionKeywords);
    public bool IsSettingsSectionCapacityVisible => MatchesFilter(_capacityKeywords);
    public bool IsSettingsSectionSensitivityVisible => MatchesFilter(_sensitivityKeywords);

    private void RaiseSettingsSectionVisibility()
    {
        this.RaisePropertyChanged(nameof(IsSettingsSectionBehaviorVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionLocalHotkeysVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionGlobalHotkeyVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionStorageVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionToolsVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionRetentionVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionCapacityVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionSensitivityVisible));

        // Auto-expand sections that match the current filter, collapse those that don't.
        if (!string.IsNullOrWhiteSpace(_settingsFilter))
        {
            IsSettingsSectionBehaviorExpanded = IsSettingsSectionBehaviorVisible;
            IsSettingsSectionLocalHotkeysExpanded = IsSettingsSectionLocalHotkeysVisible;
            IsSettingsSectionGlobalHotkeyExpanded = IsSettingsSectionGlobalHotkeyVisible;
            IsSettingsSectionStorageExpanded = IsSettingsSectionStorageVisible;
            IsSettingsSectionToolsExpanded = IsSettingsSectionToolsVisible;
            IsSettingsSectionRetentionExpanded = IsSettingsSectionRetentionVisible;
            IsSettingsSectionCapacityExpanded = IsSettingsSectionCapacityVisible;
            IsSettingsSectionSensitivityExpanded = IsSettingsSectionSensitivityVisible;
        }
    }

    public string SettingsExternalEditorPath
    {
        get => _settingsExternalEditorPath;
        set => this.RaiseAndSetIfChanged(ref _settingsExternalEditorPath, value);
    }

    public string SettingsExternalDiffToolPath
    {
        get => _settingsExternalDiffToolPath;
        set => this.RaiseAndSetIfChanged(ref _settingsExternalDiffToolPath, value);
    }

    public string SettingsMaxClipSizeKilobytes
    {
        get => _settingsMaxClipSizeKilobytes;
        set => this.RaiseAndSetIfChanged(ref _settingsMaxClipSizeKilobytes, value);
    }

    public string SettingsDatabasePath
    {
        get => _settingsDatabasePath;
        set => this.RaiseAndSetIfChanged(ref _settingsDatabasePath, value);
    }

    public string SettingsDatabasePassword
    {
        get => _settingsDatabasePassword;
        set => this.RaiseAndSetIfChanged(ref _settingsDatabasePassword, value);
    }

    public bool IsDatabasePasswordVisible
    {
        get => _isDatabasePasswordVisible;
        set => this.RaiseAndSetIfChanged(ref _isDatabasePasswordVisible, value);
    }

    public bool SettingsCloseToTray
    {
        get => _settingsCloseToTray;
        set => this.RaiseAndSetIfChanged(ref _settingsCloseToTray, value);
    }

    public bool SettingsMinimizeToTray
    {
        get => _settingsMinimizeToTray;
        set => this.RaiseAndSetIfChanged(ref _settingsMinimizeToTray, value);
    }

    public bool SettingsStartWithWindows
    {
        get => _settingsStartWithWindows;
        set => this.RaiseAndSetIfChanged(ref _settingsStartWithWindows, value);
    }

    public ThemeMode SettingsThemeMode
    {
        get => _settingsThemeMode;
        set => this.RaiseAndSetIfChanged(ref _settingsThemeMode, value);
    }

    public ThemeMode[] ThemeModeOptions { get; } = Enum.GetValues<ThemeMode>();

    public string SettingsThemeModeLabel => AppText.SettingsThemeModeLabel;

    public bool SettingsEnableNormalClipLifetime
    {
        get => _settingsEnableNormalClipLifetime;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableNormalClipLifetime, value);
    }

    public string SettingsNormalClipLifetimeDays
    {
        get => _settingsNormalClipLifetimeDays;
        set => this.RaiseAndSetIfChanged(ref _settingsNormalClipLifetimeDays, value);
    }

    public bool SettingsEnableSensitiveClipLifetime
    {
        get => _settingsEnableSensitiveClipLifetime;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableSensitiveClipLifetime, value);
    }

    public string SettingsSensitiveClipLifetimeMinutes
    {
        get => _settingsSensitiveClipLifetimeMinutes;
        set => this.RaiseAndSetIfChanged(ref _settingsSensitiveClipLifetimeMinutes, value);
    }

    public bool SettingsEnableMaxLibrarySize
    {
        get => _settingsEnableMaxLibrarySize;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsEnableMaxLibrarySize, value);
            this.RaisePropertyChanged(nameof(StorageCapacityText));
            this.RaisePropertyChanged(nameof(StorageUsagePercent));
        }
    }

    public string SettingsMaxLibrarySizeMegabytes
    {
        get => _settingsMaxLibrarySizeMegabytes;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsMaxLibrarySizeMegabytes, value);
            this.RaisePropertyChanged(nameof(StorageCapacityText));
            this.RaisePropertyChanged(nameof(StorageUsagePercent));
        }
    }

    public bool SettingsEnableMaxEntryCount
    {
        get => _settingsEnableMaxEntryCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsEnableMaxEntryCount, value);
            this.RaisePropertyChanged(nameof(EntryCapacityText));
            this.RaisePropertyChanged(nameof(EntryUsagePercent));
        }
    }

    public string SettingsMaxEntryCount
    {
        get => _settingsMaxEntryCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsMaxEntryCount, value);
            this.RaisePropertyChanged(nameof(EntryCapacityText));
            this.RaisePropertyChanged(nameof(EntryUsagePercent));
        }
    }

    public void Dispose()
    {
        CancelAllPendingDeletes();
        _clipboardMonitorService.Stop();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        SelectedClipFiles.Clear();
        ClearClips();
        SessionLogs.Dispose();
        _subscriptions.Dispose();
    }

    public async Task InitializeAsync()
    {
        if (_isStarted || _isStartupInProgress)
        {
            return;
        }

        _isStartupInProgress = true;
        StatusText = AppText.LoadingStatus;

        try
        {
            if (_settingsService.HasSavedSettings)
            {
                await _settingsService.InitializeAsync();
            }

            var draftSettings = _settingsService.HasSavedSettings ? _settingsService.Current : AppSettings.Default;
            LoadSettingsDraft(draftSettings);
            _contentDisplayMode = draftSettings.LastContentDisplayMode;
            RaiseRenderModeProperties();

            if (!_storageOptionsService.HasSavedConfig || !_storageOptionsService.DatabaseExists)
            {
                ReplaceSensitivityRules(_sensitivityService.GetDefaultRules());
                IsWelcomeOpen = true;
                StatusText = AppText.WelcomeStatusText;
                _isStarted = true;
                return;
            }

            if (StorageOptionsService.RequiresPassword(_storageOptionsService.Current.DatabasePath))
            {
                IsPasswordPromptOpen = true;
                StatusText = "Enter your database password to continue.";
                _isStarted = true;
                return;
            }

            await StartDatabaseAsync();
            await ApplyMaintenanceAndRefreshAsync();

            _isStarted = true;
        }
        finally
        {
            _isStartupInProgress = false;
        }
    }

    public void ReportStartupFailure(Exception ex)
    {
        StatusText = AppText.FormatErrorStatus(ex.Message);
    }

    private Task RefreshAsync() => RefreshAsync(null);

    private async Task RefreshAsync(long? preferredSelectionId)
    {
        if (!_isDatabaseReady)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var filters = BuildFilters(offset: 0);
            var result = await _clipStoreService.SearchAsync(filters);
            ApplyRefreshResult(result, preferredSelectionId);

            if (!string.IsNullOrWhiteSpace(filters.SearchText))
            {
                await _searchHistoryService.SaveSearchAsync(filters.SearchText);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        if (!_isDatabaseReady || !HasMoreResults)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _clipStoreService.SearchAsync(BuildFilters(_currentOffset));
            foreach (var item in result.Items.Select(clip => CreateClipItemViewModel(clip)))
            {
                Clips.Add(item);
            }

            _currentOffset += result.Items.Count;
            HasMoreResults = Clips.Count < result.TotalMatchingCount;
            this.RaisePropertyChanged(nameof(HasNoClips));
            RaiseBulkSelectionProperties();
            UpdateStatus(result);
            UpdateClipDisplayIndices();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        var clip = GetEffectiveSelectedClip();
        if (clip is null)
        {
            return;
        }

        await ToggleFavoriteStateAsync(clip);
    }

    private async Task CopySelectedAsync()
    {
        try
        {
            var clip = GetEffectiveSelectedClip();
            if (clip is null)
            {
                return;
            }

            if (!ReferenceEquals(SelectedClip, clip))
            {
                SelectedClip = clip;
            }

            _clipboardMonitorService.SuppressNext();

            if (clip.Clip.ContentType == ContentType.Image)
            {
                using var bitmap = TryLoadImage(clip.Clip, _settingsService.Current.MaxClipSizeBytes);
                if (bitmap is null)
                {
                    throw new InvalidOperationException("The selected image clip could not be decoded for copying.");
                }

                await _systemInteractionService.CopyBitmapAsync(bitmap);
                StatusText = AppText.CopiedImageStatus;
                PublishSensitiveCopyNotificationIfNeeded(clip);
                TrackPasteInBackground(clip.Clip.Id);
                return;
            }

            if (clip.Clip.ContentType == ContentType.RichText)
            {
                await _systemInteractionService.CopyRichContentAsync(clip.FullContent, SelectedClipRenderedText, clip.Clip.ContentFormat);
                StatusText = AppText.FormatCopiedClip(clip.DisplayContentType.ToLower(AppText.CurrentCulture));
                PublishSensitiveCopyNotificationIfNeeded(clip);
                TrackPasteInBackground(clip.Clip.Id);
                return;
            }

            var isFileList = clip.Clip.ContentType == ContentType.Files && SelectedClipFiles.Count > 0;
            var contentToCopy = isFileList
                ? string.Join(Environment.NewLine, SelectedClipFiles.Select(static file => file.FilePath))
                : clip.FullContent;

            await _systemInteractionService.CopyTextAsync(contentToCopy);
            StatusText = isFileList
                ? AppText.FormatCopiedFileList(SelectedClipFiles.Count)
                : AppText.FormatCopiedClip(clip.DisplayContentType.ToLower(AppText.CurrentCulture));
            PublishSensitiveCopyNotificationIfNeeded(clip);
            TrackPasteInBackground(clip.Clip.Id);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy selected failed: {ex}");
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private async void TrackPasteInBackground(long clipId)
    {
        try
        {
            await _clipStoreService.MarkPastedAsync(clipId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to track paste for clip {clipId}: {ex.Message}");
        }

        try
        {
            WarnIfTargetWindowElevated();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Elevation check failed: {ex.Message}");
        }

        try
        {
            await RefreshAsync(clipId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Refresh after paste failed: {ex.Message}");
        }
    }

    private async Task ExportSelectedAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        var exportResult = await _clipExportService.ExportAsync(SelectedClip.Clip);
        _clipboardMonitorService.SuppressNext();
        await _systemInteractionService.CopyTextAsync(exportResult.PrimaryPath);
        await _systemInteractionService.OpenPathAsync(exportResult.PrimaryPath);
        StatusText = AppText.FormatExportedClipStatus(exportResult.PrimaryPath);
    }

    private async Task OpenInEditorAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        var exportResult = await _clipExportService.ExportAsync(SelectedClip.Clip);
        var editorPath = _settingsService.Current.ExternalEditorPath;
        await _systemInteractionService.OpenInEditorAsync(exportResult.PrimaryPath, editorPath);
        StatusText = $"{AppText.OpenedInEditorStatus}: {Path.GetFileName(exportResult.PrimaryPath)}";
    }

    private async Task OpenSelectedClipSourceUrlAsync()
    {
        var url = SelectedClip?.SourceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            await _systemInteractionService.OpenPathAsync(url);
            StatusText = $"Opened {url}";
        }
        catch (Exception ex)
        {
            StatusText = $"Open URL failed: {ex.Message}";
        }
    }

    private async Task CopySelectedClipWindowTitleAsync()
    {
        var title = SelectedClip?.SourceWindowTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            _clipboardMonitorService.SuppressNext();
            await _systemInteractionService.CopyTextAsync(title);
            StatusText = "Copied window title";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy title failed: {ex.Message}";
        }
    }

    private async Task CompareClipsAsync()
    {
        var checkedClips = Clips.Where(static c => c.IsChecked).Take(2).ToList();
        if (checkedClips.Count < 2)
        {
            StatusText = AppText.CompareNeedsTwoClipsStatus;
            return;
        }

        var diffToolPath = _settingsService.Current.ExternalDiffToolPath;
        if (string.IsNullOrWhiteSpace(diffToolPath))
        {
            StatusText = AppText.CompareNeedsDiffToolStatus;
            return;
        }

        var left = await _clipExportService.ExportAsync(checkedClips[0].Clip);
        var right = await _clipExportService.ExportAsync(checkedClips[1].Clip);

        await _systemInteractionService.OpenInDiffToolAsync(left.PrimaryPath, right.PrimaryPath, diffToolPath);
        StatusText = AppText.CompareOpenedStatus;
    }

    private async Task EditSelectedImageAsync()
    {
        var clip = GetEffectiveSelectedClip();
        if (clip?.Clip.ContentType != ContentType.Image || clip.Clip.ContentBytes is not { Length: > 0 } imageBytes)
        {
            return;
        }

        if (!ReferenceEquals(SelectedClip, clip))
        {
            SelectedClip = clip;
        }

        var editedBytes = await _imageEditorService.EditImageAsync(imageBytes, clip.Clip.SourceAppPath);
        if (editedBytes is not { Length: > 0 })
        {
            return;
        }

        await CopyEditedImageAsync(editedBytes);
    }

    public async Task CopyEditedImageAsync(byte[] editedBytes)
    {
        var clip = GetEffectiveSelectedClip();
        if (clip?.Clip.ContentType != ContentType.Image || editedBytes is not { Length: > 0 })
        {
            return;
        }

        if (!ReferenceEquals(SelectedClip, clip))
        {
            SelectedClip = clip;
        }

        using var bitmapStream = new MemoryStream(editedBytes, writable: false);
        using var bitmap = new Bitmap(bitmapStream);

        var capturedClip = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = editedBytes,
            ContentText = string.IsNullOrWhiteSpace(clip.FullContent) ? null : clip.FullContent,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = AppText.WindowTitle,
            SourceAppPath = Environment.ProcessPath,
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
        });

        _clipboardMonitorService.SuppressNext();
        await _systemInteractionService.CopyBitmapAsync(bitmap);

        if (capturedClip is not null)
        {
            await RefreshAsync(capturedClip.Id);
            PublishSensitiveCopyNotificationIfNeeded(Clips.FirstOrDefault(current => current.Id == capturedClip.Id));
            StatusText = AppText.EditedImageCopiedStatus;
            return;
        }

        StatusText = AppText.CopiedImageStatus;
    }

    private void SelectAllClips()
    {
        foreach (var clip in Clips)
        {
            clip.IsChecked = true;
        }

        RaiseBulkSelectionProperties();
    }

    private void SelectNoClips()
    {
        foreach (var clip in Clips)
        {
            clip.IsChecked = false;
        }

        RaiseBulkSelectionProperties();
    }

    private async Task FavoriteCheckedClipsAsync()
    {
        var targetClips = GetCheckedOrSelectedClips();
        if (targetClips.Length == 0)
        {
            return;
        }

        var nextIsFavorite = targetClips.Any(static clip => !clip.IsFavorite);
        foreach (var clip in targetClips)
        {
            await _clipStoreService.SetFavoriteAsync(clip.Id, nextIsFavorite);
            clip.SetFavoriteState(nextIsFavorite);
        }

        if (ShowFavoritesOnly && !nextIsFavorite)
        {
            await RefreshAsync(SelectedClip?.Id ?? targetClips[0].Id);
        }
        else
        {
            RaiseSelectionStateProperties();
            RaiseBulkSelectionProperties();
        }

        StatusText = AppText.FormatFavoritedClipCount(targetClips.Length);
    }

    private async Task PinCheckedClipsAsync()
    {
        var targetClips = GetCheckedOrSelectedClips();
        if (targetClips.Length == 0)
        {
            return;
        }

        var nextIsPinned = targetClips.Any(static clip => !clip.IsPinned);
        foreach (var clip in targetClips)
        {
            await _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned);
            clip.SetPinnedState(nextIsPinned);
        }

        await RefreshAsync(SelectedClip?.Id ?? targetClips[0].Id);
        StatusText = nextIsPinned
            ? $"Pinned {targetClips.Length} clip(s)"
            : $"Unpinned {targetClips.Length} clip(s)";
    }

    private async Task TogglePinAsync()
    {
        var clip = GetEffectiveSelectedClip();
        if (clip is null)
        {
            return;
        }

        var nextIsPinned = !clip.IsPinned;
        await _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned);
        clip.SetPinnedState(nextIsPinned);
        await RefreshAsync(clip.Id);
    }

    private async Task TogglePinClipAsync(ClipItemViewModel clip)
    {
        var nextIsPinned = !clip.IsPinned;
        await _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned);
        clip.SetPinnedState(nextIsPinned);
        await RefreshAsync(clip.Id);
    }

    private async Task DeleteCheckedClipsAsync()
    {
        var targetClips = GetCheckedOrSelectedClips();
        if (targetClips.Length == 0)
        {
            return;
        }

        await SoftDeleteClipsAsync(targetClips);
    }

    private async Task CopyEditedClipAsync()
    {
        if (SelectedClip is null || !IsSelectedClipTextEditable)
        {
            return;
        }

        _clipboardMonitorService.SuppressNext();
        if (SelectedClip.Clip.ContentType == ContentType.RichText && _contentDisplayMode == ContentDisplayMode.Raw)
        {
            var renderedText = ClipDisplayFormatter.RenderRichContent(EditedClipText);
            await _systemInteractionService.CopyRichContentAsync(EditedClipText, renderedText, SelectedClip.Clip.ContentFormat);
        }
        else
        {
            await _systemInteractionService.CopyTextAsync(EditedClipText);
        }

        _editedClipBaseline = EditedClipText;
        RaiseEditedClipProperties();
        StatusText = AppText.EditedClipCopiedStatus;
        PublishSensitiveCopyNotificationIfNeeded(SelectedClip);
    }

    public async Task CommitEditedClipOnFocusLossAsync() => await CommitEditedClipOnSelectionChangeAsync();

    private async Task ApplyTextTransformationAsync(TextTransformation transformation)
    {
        if (transformation == TextTransformation.None)
        {
            return;
        }

        var checkedClips = Clips.Where(static c => c.IsChecked).ToList();
        var targets = checkedClips.Count > 0
            ? checkedClips
            : SelectedClip is not null ? new List<ClipItemViewModel> { SelectedClip } : new List<ClipItemViewModel>();

        var transformed = 0;
        foreach (var target in targets)
        {
            if (target.Clip.ContentType != ContentType.Text && target.Clip.ContentType != ContentType.RichText)
            {
                continue;
            }

            var source = target.Clip.Content ?? string.Empty;
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            var result = TextTransformationService.Apply(transformation, source);
            if (string.Equals(result, source, StringComparison.Ordinal))
            {
                continue;
            }

            var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
            await _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = textBytes,
                ContentText = result,
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = target.SourceApp,
                SourceAppPath = target.Clip.SourceAppPath,
                SourceAppIconBytes = target.Clip.SourceAppIconBytes,
                IncrementExistingCopyCount = false,
            });
            transformed++;
        }

        if (transformed > 0)
        {
            StatusText = transformed == 1
                ? AppText.EditedClipCopiedStatus
                : $"Applied transformation to {transformed} clips";
        }
    }

    private async Task CommitEditedClipOnSelectionChangeAsync()
    {
        if (_suppressEditAutoSave)
        {
            return;
        }

        if (_selectedClip is null || !IsSelectedClipTextEditable)
        {
            return;
        }

        if (string.Equals(_editedClipText, _editedClipBaseline, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_editedClipText))
        {
            return;
        }

        var textBytes = System.Text.Encoding.UTF8.GetBytes(_editedClipText);
        var request = new ClipCaptureRequest
        {
            ContentBytes = textBytes,
            ContentText = _editedClipText,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = _selectedClip.SourceApp,
            SourceAppPath = _selectedClip.Clip.SourceAppPath,
            SourceAppIconBytes = _selectedClip.Clip.SourceAppIconBytes,
            IncrementExistingCopyCount = false,
        };

        await _clipStoreService.CaptureAsync(request);
        _editedClipBaseline = _editedClipText;
    }

    private async Task CopyClipAsync(ClipItemViewModel clip)
    {
        SelectedClip = clip;
        await CopySelectedAsync();
    }

    private async Task ExportClipAsync(ClipItemViewModel clip)
    {
        SelectedClip = clip;
        await ExportSelectedAsync();
    }

    public async Task<bool> CopyClipByIndexAsync(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > Clips.Count)
        {
            return false;
        }

        var clip = Clips[oneBasedIndex - 1];
        await CopyClipAsync(clip);
        return true;
    }

    public void SelectClipByIndex(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > Clips.Count)
        {
            return;
        }

        SelectedClip = Clips[oneBasedIndex - 1];
    }

    private async Task ToggleFavoriteClipAsync(ClipItemViewModel clip)
    {
        SelectedClip = clip;
        await ToggleFavoriteStateAsync(clip);
    }

    private async Task ToggleFavoriteStateAsync(ClipItemViewModel clip)
    {
        var nextIsFavorite = !clip.IsFavorite;
        await _clipStoreService.SetFavoriteAsync(clip.Id, nextIsFavorite);
        clip.SetFavoriteState(nextIsFavorite);

        if (ReferenceEquals(SelectedClip, clip))
        {
            RaiseSelectionStateProperties();
        }

        if (ShowFavoritesOnly && !nextIsFavorite)
        {
            await RefreshAsync();
        }
    }

    private async Task DeleteClipAsync(ClipItemViewModel clip)
    {
        SelectedClip = clip;
        await SoftDeleteClipsAsync([clip]);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        await SoftDeleteClipsAsync([SelectedClip]);
    }

    private async Task SoftDeleteClipsAsync(ClipItemViewModel[] clipVms)
    {
        var entries = new List<(long Id, ClipEntry Clip)>(clipVms.Length);
        foreach (var vm in clipVms)
        {
            entries.Add((vm.Id, vm.Clip));
            Clips.Remove(vm);
            vm.Dispose();
        }

        this.RaisePropertyChanged(nameof(HasNoClips));
        RaiseBulkSelectionProperties();

        if (entries.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        foreach (var (id, clip) in entries)
        {
            _pendingDeletes[id] = (clip, cts);
        }

        var ids = entries.Select(static e => e.Id).ToArray();

        var preview = entries.Count == 1
            ? $"'{ClipDisplayFormatter.BuildSingleLinePreview(entries[0].Clip)}' removed"
            : AppText.FormatDeletedClipCount(entries.Count);

        StatusText = entries.Count == 1 ? "Clip deleted" : AppText.FormatDeletedClipCount(entries.Count);

        _notificationService.Publish(new AppNotification
        {
            Title = entries.Count == 1 ? "Clip deleted" : $"{entries.Count} clips deleted",
            Message = preview,
            Level = AppNotificationLevel.Information,
            Actions =
            [
                new AppNotificationAction
                {
                    Label = "Undo",
                    ExecuteAsync = () => UndoDeleteAsync(ids, cts),
                },
            ],
        });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            await CommitPendingDeletesAsync(ids);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task UndoDeleteAsync(long[] clipIds, CancellationTokenSource cts)
    {
        var anyRestored = false;
        foreach (var id in clipIds)
        {
            if (_pendingDeletes.Remove(id))
            {
                anyRestored = true;
            }
        }

        if (!anyRestored)
        {
            return;
        }

        cts.Cancel();
        await RefreshAsync();
        StatusText = clipIds.Length == 1 ? "Clip restored" : $"{clipIds.Length} clips restored";
    }

    private async Task CommitPendingDeletesAsync(long[] clipIds)
    {
        foreach (var id in clipIds)
        {
            if (_pendingDeletes.Remove(id, out _))
            {
                await _clipStoreService.DeleteAsync(id);
            }
        }
    }

    private void CancelAllPendingDeletes()
    {
        var seen = new HashSet<CancellationTokenSource>(ReferenceEqualityComparer.Instance);
        foreach (var (_, cts) in _pendingDeletes.Values)
        {
            if (seen.Add(cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        _pendingDeletes.Clear();
    }

    private ClipSearchFilters BuildFilters(int offset) => new()
    {
        SearchText = SearchText,
        ContentType = SelectedContentTypeOption.Value,
        FavoritesOnly = ShowFavoritesOnly,
        SensitiveOnly = ShowSensitiveOnly,
        PastedOnly = ShowPastedOnly,
        UseRegex = UseRegexSearch,
        CaseSensitive = CaseSensitiveSearch,
        UseWildcard = UseWildcardSearch,
        WholeWord = WholeWordSearch,
        UseFuzzy = UseFuzzyClipSearch && !UseRegexSearch && !UseWildcardSearch && !WholeWordSearch,
        Limit = PageSize,
        Offset = offset,
    };

    public void ToggleClipCheckedSelection(ClipItemViewModel clip)
    {
        var index = Clips.IndexOf(clip);
        if (index < 0)
        {
            return;
        }

        clip.IsChecked = !clip.IsChecked;
        SelectedClip = clip;
        _checkedSelectionAnchorId = clip.Id;
        RaiseBulkSelectionProperties();
    }

    public void ExtendClipCheckedSelection(ClipItemViewModel clip, bool preserveExistingSelection)
    {
        var targetIndex = Clips.IndexOf(clip);
        if (targetIndex < 0)
        {
            return;
        }

        var anchorIndex = _checkedSelectionAnchorId is long anchorId
            ? IndexOfClip(anchorId)
            : -1;
        if (anchorIndex < 0)
        {
            anchorIndex = SelectedClip is null ? targetIndex : Clips.IndexOf(SelectedClip);
        }

        if (anchorIndex < 0)
        {
            anchorIndex = targetIndex;
        }

        if (!preserveExistingSelection)
        {
            foreach (var current in Clips)
            {
                current.IsChecked = false;
            }
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var index = start; index <= end; index++)
        {
            Clips[index].IsChecked = true;
        }

        SelectedClip = clip;
        RaiseBulkSelectionProperties();
    }

    private int IndexOfClip(long clipId)
    {
        for (var index = 0; index < Clips.Count; index++)
        {
            if (Clips[index].Id == clipId)
            {
                return index;
            }
        }

        return -1;
    }

    private void ApplyRefreshResult(ClipSearchResult result, long? preferredSelectionId = null)
    {
        var previousSelectionId = preferredSelectionId ?? SelectedClip?.Id;
        var checkedIds = Clips
            .Where(static clip => clip.IsChecked)
            .Select(static clip => clip.Id)
            .ToHashSet();

        _suppressEditAutoSave = true;
        try
        {
            ClearClips();
            foreach (var item in result.Items.Select(clip => CreateClipItemViewModel(clip, checkedIds)))
            {
                Clips.Add(item);
            }

            _currentOffset = result.Items.Count;
            HasMoreResults = Clips.Count < result.TotalMatchingCount;
            this.RaisePropertyChanged(nameof(HasNoClips));
            SelectedClip = previousSelectionId is null
                ? Clips.FirstOrDefault()
                : Clips.FirstOrDefault(clip => clip.Id == previousSelectionId) ?? Clips.FirstOrDefault();
        }
        finally
        {
            _suppressEditAutoSave = false;
        }

        RaiseBulkSelectionProperties();
        UpdateStatus(result);
        UpdateClipDisplayIndices();
    }

    private void UpdateSelectedClipPresentation()
    {
        ReplaceSelectedClipFiles(ClipDisplayFormatter.BuildFileItems(SelectedClip?.FullContent));
        _selectedClipRenderedText = ClipDisplayFormatter.BuildRenderedText(SelectedClip?.Clip, SelectedClipFiles.Select(static file => file.FilePath).ToArray());
        SyncEditedClipText();

        var hasImageBytes = SelectedClip?.Clip.ContentType == ContentType.Image
            && SelectedClip.Clip.ContentBytes is { Length: > 0 };
        var imageTooLarge = hasImageBytes
            && SelectedClip!.Clip.ContentBytes!.Length > _settingsService.Current.MaxClipSizeBytes;
        _selectedClipImageHint = imageTooLarge
            ? AppText.PreviewImageTooLarge
            : ClipDisplayFormatter.BuildImageHint(SelectedClip?.Clip, hasImageBytes);
        this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
    }

    private void ReplaceSelectedClipFiles(IReadOnlyList<string> fileItems)
    {
        SelectedClipFiles.Clear();
        SelectedFileItem = null;

        foreach (var fileItem in fileItems)
        {
            SelectedClipFiles.Add(new ClipFileItemViewModel(fileItem, _systemInteractionService, message => StatusText = message));
        }

        SelectedFileItem = SelectedClipFiles.FirstOrDefault();
    }

    private void RaiseSelectionStateProperties()
    {
        this.RaisePropertyChanged(nameof(IsSelectedClipFavorite));
        this.RaisePropertyChanged(nameof(IsSelectedClipPinned));
        this.RaisePropertyChanged(nameof(SelectedClipFavoriteButtonLabel));
        this.RaisePropertyChanged(nameof(SelectedClipPinButtonLabel));
        this.RaisePropertyChanged(nameof(HasSelectedClip));
        this.RaisePropertyChanged(nameof(SelectionStateTitle));
        this.RaisePropertyChanged(nameof(ShowEmptySelectionState));
        this.RaisePropertyChanged(nameof(SelectedClipFiles));
        this.RaisePropertyChanged(nameof(HasSelectedClipFileItems));
        this.RaisePropertyChanged(nameof(SelectedClipRenderedText));
        this.RaisePropertyChanged(nameof(SelectedClipRawContent));
        this.RaisePropertyChanged(nameof(SelectedClipImageBytes));
        this.RaisePropertyChanged(nameof(SelectedClipImageHint));
        this.RaisePropertyChanged(nameof(SelectedClipContentTypeText));
        this.RaisePropertyChanged(nameof(SelectedClipContentFormat));
        this.RaisePropertyChanged(nameof(SelectedClipTitleText));
        this.RaisePropertyChanged(nameof(SelectedClipSourceText));
        this.RaisePropertyChanged(nameof(SelectedClipSourceAppIcon));
        this.RaisePropertyChanged(nameof(ShowSelectedClipSourceAppIcon));
        this.RaisePropertyChanged(nameof(SelectedClipFirstCopiedAtText));
        this.RaisePropertyChanged(nameof(SelectedClipCapturedAtText));
        this.RaisePropertyChanged(nameof(SelectedClipExpiresAtText));
        this.RaisePropertyChanged(nameof(SelectedClipCopyCountText));
        this.RaisePropertyChanged(nameof(SelectedClipByteSizeText));
        this.RaisePropertyChanged(nameof(SelectedClipImageResolutionText));
        this.RaisePropertyChanged(nameof(ShowSelectedImageResolutionCard));
        this.RaisePropertyChanged(nameof(SelectedClipSensitivityText));
        this.RaisePropertyChanged(nameof(SelectedClipWindowTitleText));
        this.RaisePropertyChanged(nameof(ShowSelectedClipWindowTitle));
        this.RaisePropertyChanged(nameof(HasSelectedClipSourceUrl));
        this.RaisePropertyChanged(nameof(SelectedClipPastedText));
        this.RaisePropertyChanged(nameof(ShowSelectedClipPasted));
        this.RaisePropertyChanged(nameof(SelectedClipTypeChipBackground));
        this.RaisePropertyChanged(nameof(SelectedClipTypeChipBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipTypeChipForeground));
        this.RaisePropertyChanged(nameof(SelectedClipAgeChipBackground));
        this.RaisePropertyChanged(nameof(SelectedClipAgeChipBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipAgeChipForeground));
        this.RaisePropertyChanged(nameof(SelectedClipPastedChipBackground));
        this.RaisePropertyChanged(nameof(SelectedClipPastedChipBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipPastedChipForeground));
        this.RaisePropertyChanged(nameof(SelectedClipSizeChipBackground));
        this.RaisePropertyChanged(nameof(SelectedClipSizeChipBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipSizeChipForeground));
        this.RaisePropertyChanged(nameof(SelectedClipSensitivityChipBackground));
        this.RaisePropertyChanged(nameof(SelectedClipSensitivityChipBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipSensitivityChipForeground));
        this.RaisePropertyChanged(nameof(SelectedClipAccentBrush));
        this.RaisePropertyChanged(nameof(SelectedClipAreaBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipAreaBorderThickness));
        this.RaisePropertyChanged(nameof(ShowSelectedClipSeverityIndicator));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityIndicatorText));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeBackground));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeForeground));
        this.RaisePropertyChanged(nameof(IsSelectedClipTextEditable));
        this.RaisePropertyChanged(nameof(SelectedClipTextIsReadOnly));
        this.RaisePropertyChanged(nameof(IsDisplayModeApplicable));
        this.RaisePropertyChanged(nameof(HasCheckedOrSelectedClip));
        RaiseRenderModeProperties();
        RaiseEditedClipProperties();
    }

    private void RaiseFilterStateProperties()
    {
        this.RaisePropertyChanged(nameof(ActiveFilterSummary));
        this.RaisePropertyChanged(nameof(EmptyListMessage));
    }

    private void RaiseContentTypeToggleProperties()
    {
        this.RaisePropertyChanged(nameof(IsAllTypeSelected));
        this.RaisePropertyChanged(nameof(IsTextTypeSelected));
        this.RaisePropertyChanged(nameof(IsImageTypeSelected));
        this.RaisePropertyChanged(nameof(IsRichTextTypeSelected));
        this.RaisePropertyChanged(nameof(IsFilesTypeSelected));
    }

    private void RefreshLastCaptureSummary()
    {
        if (_lastCapturedAtRaw is null)
        {
            return;
        }

        LastCaptureSummary = AppText.FormatLastCapture(ClipDisplayFormatter.ToRelativeTime(_lastCapturedAtRaw.Value));
    }

    public async Task LoadRecentSearchesAsync()
    {
        if (!_isDatabaseReady)
        {
            return;
        }

        try
        {
            var searches = await _searchHistoryService.GetRecentSearchesAsync();
            RecentSearches.Clear();
            foreach (var search in searches)
            {
                RecentSearches.Add(search);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to load search history: {ex.Message}");
        }
    }

    public bool TryHandleShortcut(KeyEventArgs e)
    {
        if (IsSettingsOpen || IsWelcomeOpen || IsPasswordPromptOpen || IsAiPromptOpen || SessionLogs.IsOpen)
        {
            return false;
        }

        return TryHandleShortcut(e, _settingsService.Current.EnableToggleRegexHotkey, _settingsService.Current.ToggleRegexHotkey, () => UseRegexSearch = !UseRegexSearch)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleFavoritesHotkey, _settingsService.Current.ToggleFavoritesHotkey, () => ShowFavoritesOnly = !ShowFavoritesOnly)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleSensitiveHotkey, _settingsService.Current.ToggleSensitiveHotkey, () => ShowSensitiveOnly = !ShowSensitiveOnly)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleCaseSensitiveHotkey, _settingsService.Current.ToggleCaseSensitiveHotkey, () => CaseSensitiveSearch = !CaseSensitiveSearch)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleWildcardHotkey, _settingsService.Current.ToggleWildcardHotkey, () => UseWildcardSearch = !UseWildcardSearch)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleWholeWordHotkey, _settingsService.Current.ToggleWholeWordHotkey, () => WholeWordSearch = !WholeWordSearch)
            || TryHandleShortcut(e, _settingsService.Current.EnableTogglePastedHotkey, _settingsService.Current.TogglePastedHotkey, () => ShowPastedOnly = !ShowPastedOnly);
    }

    private bool TryHandleShortcut(KeyEventArgs e, bool isEnabled, string hotkeyText, Action action)
    {
        if (!isEnabled
            || !TryParseAvaloniaGesture(hotkeyText, out var gesture)
            || gesture is null
            || NormalizeKey(e.Key, e.PhysicalKey) != gesture.Key
            || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta)) != gesture.KeyModifiers)
        {
            return false;
        }

        action();
        return true;
    }

    private void OpenSettings()
    {
        SessionLogs.Close();
        LoadSettingsDraft(_settingsService.Current);
        IsSettingsOpen = true;
    }

    private void OpenHelp()
    {
        // Handled in the view-layer (code-behind) via an observable; the command
        // just pulses and the view listens and opens the HelpWindow.
        HelpRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? HelpRequested;

    private void OpenAiPrompt()
    {
        if (!_aiTransformService.IsConfigured)
        {
            StatusText = "AI is not configured. Enable it in Settings → AI and set an API key (or OPENAI_API_KEY env var).";
            return;
        }
        AiPromptError = string.Empty;
        AiPromptInput = string.Empty;
        IsAiPromptOpen = true;
    }

    private void CancelAiPrompt()
    {
        IsAiPromptOpen = false;
        AiPromptError = string.Empty;
    }

    private async Task SubmitAiPromptAsync()
    {
        if (IsAiPromptBusy)
        {
            return;
        }
        var prompt = (AiPromptInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            AiPromptError = "Please enter a prompt.";
            return;
        }

        var checkedClips = Clips.Where(static c => c.IsChecked).ToList();
        var targets = checkedClips.Count > 0
            ? checkedClips
            : SelectedClip is not null ? new List<ClipItemViewModel> { SelectedClip } : new List<ClipItemViewModel>();

        targets = targets
            .Where(t => (t.Clip.ContentType == ContentType.Text || t.Clip.ContentType == ContentType.RichText)
                && !string.IsNullOrEmpty(t.Clip.Content))
            .ToList();

        if (targets.Count == 0)
        {
            AiPromptError = "Select one or more text clips first.";
            return;
        }

        IsAiPromptBusy = true;
        AiPromptError = string.Empty;
        try
        {
            var produced = 0;
            foreach (var target in targets)
            {
                var source = target.Clip.Content ?? string.Empty;
                string result;
                try
                {
                    result = await _aiTransformService.TransformAsync(prompt, source);
                }
                catch (Exception ex)
                {
                    AiPromptError = ex.Message;
                    return;
                }

                if (string.IsNullOrEmpty(result) || string.Equals(result, source, StringComparison.Ordinal))
                {
                    continue;
                }

                var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
                await _clipStoreService.CaptureAsync(new ClipCaptureRequest
                {
                    ContentBytes = textBytes,
                    ContentText = result,
                    ContentType = ContentType.Text,
                    ContentFormat = ClipContentFormat.PlainText,
                    SourceApp = target.SourceApp,
                    SourceAppPath = target.Clip.SourceAppPath,
                    SourceAppIconBytes = target.Clip.SourceAppIconBytes,
                    IncrementExistingCopyCount = false,
                });
                produced++;
            }

            if (produced > 0)
            {
                StatusText = produced == 1
                    ? "AI transform produced a new clip."
                    : $"AI transform produced {produced} new clips.";
            }
            else
            {
                StatusText = "AI transform returned no new content.";
            }
            IsAiPromptOpen = false;
        }
        finally
        {
            IsAiPromptBusy = false;
        }
    }

    private void CloseSettings()
    {
        LoadSettingsDraft(_settingsService.Current);
        IsSettingsOpen = false;
    }

    private async Task BrowseDatabasePathAsync(Window? window)
    {
        if (window?.StorageProvider is null)
        {
            return;
        }

        var selectedPath = await PickDatabasePathAsync(window.StorageProvider, SettingsDatabasePath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SettingsDatabasePath = selectedPath;
        }
    }

    private async Task SaveSettingsAsync()
    {
        var localHotkeys = new[]
        {
            new HotkeyDraft(nameof(AppSettings.ToggleRegexHotkey), SettingsEnableToggleRegexHotkey, SettingsToggleRegexHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleFavoritesHotkey), SettingsEnableToggleFavoritesHotkey, SettingsToggleFavoritesHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleSensitiveHotkey), SettingsEnableToggleSensitiveHotkey, SettingsToggleSensitiveHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleCaseSensitiveHotkey), SettingsEnableToggleCaseSensitiveHotkey, SettingsToggleCaseSensitiveHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleWildcardHotkey), SettingsEnableToggleWildcardHotkey, SettingsToggleWildcardHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleWholeWordHotkey), SettingsEnableToggleWholeWordHotkey, SettingsToggleWholeWordHotkey),
            new HotkeyDraft(nameof(AppSettings.TogglePastedHotkey), SettingsEnableTogglePastedHotkey, SettingsTogglePastedHotkey),
        };

        var normalizedHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in localHotkeys)
        {
            if (!pair.IsEnabled)
            {
                normalizedHotkeys[pair.Name] = pair.HotkeyText.Trim();
                continue;
            }

            if (!TryParseAvaloniaGesture(pair.HotkeyText, out var gesture) || gesture is null)
            {
                StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidHotkeyFallback);
                return;
            }

            normalizedHotkeys[pair.Name] = gesture.ToString();
        }

        var normalizedGlobalHotkey = SettingsToggleWindowHotkey.Trim();
        HotkeyGesture? parsedGlobalHotkey = null;
        string? globalHotkeyError = null;
        if (SettingsEnableToggleWindowHotkey
            && (!HotkeyGesture.TryParse(SettingsToggleWindowHotkey, out parsedGlobalHotkey, out globalHotkeyError) || parsedGlobalHotkey is null))
        {
            StatusText = AppText.FormatSettingsValidationError(globalHotkeyError ?? AppText.SettingsInvalidHotkeyFallback);
            return;
        }
        else if (SettingsEnableToggleWindowHotkey)
        {
            normalizedGlobalHotkey = parsedGlobalHotkey!.ToString();
        }

        var normalizedIncrementalHotkey = SettingsIncrementalPasteHotkey.Trim();
        HotkeyGesture? parsedIncrementalHotkey = null;
        if (SettingsEnableIncrementalPasteHotkey
            && (!HotkeyGesture.TryParse(SettingsIncrementalPasteHotkey, out parsedIncrementalHotkey, out var incHotkeyError) || parsedIncrementalHotkey is null))
        {
            StatusText = AppText.FormatSettingsValidationError(incHotkeyError ?? AppText.SettingsInvalidHotkeyFallback);
            return;
        }
        else if (SettingsEnableIncrementalPasteHotkey)
        {
            normalizedIncrementalHotkey = parsedIncrementalHotkey!.ToString();
        }

        var normalizedDecrementalHotkey = SettingsDecrementalPasteHotkey.Trim();
        HotkeyGesture? parsedDecrementalHotkey = null;
        if (SettingsEnableDecrementalPasteHotkey
            && (!HotkeyGesture.TryParse(SettingsDecrementalPasteHotkey, out parsedDecrementalHotkey, out var decHotkeyError) || parsedDecrementalHotkey is null))
        {
            StatusText = AppText.FormatSettingsValidationError(decHotkeyError ?? AppText.SettingsInvalidHotkeyFallback);
            return;
        }
        else if (SettingsEnableDecrementalPasteHotkey)
        {
            normalizedDecrementalHotkey = parsedDecrementalHotkey!.ToString();
        }

        var duplicates = localHotkeys
            .Where(static draft => draft.IsEnabled)
            .Select(draft => normalizedHotkeys[draft.Name])
            .Append(SettingsEnableToggleWindowHotkey ? normalizedGlobalHotkey : string.Empty)
            .Append(SettingsEnableIncrementalPasteHotkey ? normalizedIncrementalHotkey : string.Empty)
            .Append(SettingsEnableDecrementalPasteHotkey ? normalizedDecrementalHotkey : string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicates is not null)
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.FormatDuplicateHotkey(duplicates.Key));
            return;
        }

        if (!TryParseMaxClipSizeBytes(SettingsMaxClipSizeKilobytes, out var maxClipSizeBytes))
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidClipSize);
            return;
        }

        if (!TryParseOptionalPositiveInt(SettingsEnableNormalClipLifetime, SettingsNormalClipLifetimeDays, AppSettings.MinNormalClipLifetimeDays, AppSettings.MaxNormalClipLifetimeDays, out var normalClipLifetimeDays))
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidNormalLifetime);
            return;
        }

        if (!TryParseOptionalPositiveInt(SettingsEnableSensitiveClipLifetime, SettingsSensitiveClipLifetimeMinutes, AppSettings.MinSensitiveClipLifetimeMinutes, AppSettings.MaxSensitiveClipLifetimeMinutes, out var sensitiveClipLifetimeMinutes))
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidSensitiveLifetime);
            return;
        }

        if (!TryParseOptionalPositiveInt(SettingsEnableMaxLibrarySize, SettingsMaxLibrarySizeMegabytes, AppSettings.MinMaxLibrarySizeMegabytes, AppSettings.MaxMaxLibrarySizeMegabytes, out var maxLibrarySizeMegabytes))
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidMaxLibrarySize);
            return;
        }

        if (!TryParseOptionalPositiveInt(SettingsEnableMaxEntryCount, SettingsMaxEntryCount, AppSettings.MinMaxEntryCount, AppSettings.MaxMaxEntryCount, out var maxEntryCount))
        {
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidMaxEntryCount);
            return;
        }

        StorageOptions storageOptions;
        try
        {
            storageOptions = new StorageOptions
            {
                DatabasePath = SettingsDatabasePath,
                DatabasePassword = SettingsDatabasePassword,
            }.Normalize();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Storage settings validation failed: {ex.Message}");
            StatusText = AppText.FormatSettingsValidationError(AppText.SettingsInvalidDatabasePath);
            return;
        }

        SensitivityRule[] sensitivityRules;
        try
        {
            sensitivityRules = BuildSensitivityRules();
        }
        catch (ArgumentException ex)
        {
            StatusText = AppText.FormatSettingsValidationError(ex.Message);
            return;
        }

        var settings = _settingsService.Current with
        {
            EnableToggleRegexHotkey = SettingsEnableToggleRegexHotkey,
            ToggleRegexHotkey = normalizedHotkeys[nameof(AppSettings.ToggleRegexHotkey)],
            EnableToggleFavoritesHotkey = SettingsEnableToggleFavoritesHotkey,
            ToggleFavoritesHotkey = normalizedHotkeys[nameof(AppSettings.ToggleFavoritesHotkey)],
            EnableToggleSensitiveHotkey = SettingsEnableToggleSensitiveHotkey,
            ToggleSensitiveHotkey = normalizedHotkeys[nameof(AppSettings.ToggleSensitiveHotkey)],
            EnableToggleCaseSensitiveHotkey = SettingsEnableToggleCaseSensitiveHotkey,
            ToggleCaseSensitiveHotkey = normalizedHotkeys[nameof(AppSettings.ToggleCaseSensitiveHotkey)],
            EnableToggleWindowHotkey = SettingsEnableToggleWindowHotkey,
            ToggleWindowHotkey = normalizedGlobalHotkey,
            EnableToggleWildcardHotkey = SettingsEnableToggleWildcardHotkey,
            ToggleWildcardHotkey = normalizedHotkeys[nameof(AppSettings.ToggleWildcardHotkey)],
            EnableToggleWholeWordHotkey = SettingsEnableToggleWholeWordHotkey,
            ToggleWholeWordHotkey = normalizedHotkeys[nameof(AppSettings.ToggleWholeWordHotkey)],
            EnableTogglePastedHotkey = SettingsEnableTogglePastedHotkey,
            TogglePastedHotkey = normalizedHotkeys[nameof(AppSettings.TogglePastedHotkey)],
            EnableIncrementalPasteHotkey = SettingsEnableIncrementalPasteHotkey,
            IncrementalPasteHotkey = normalizedIncrementalHotkey,
            EnableDecrementalPasteHotkey = SettingsEnableDecrementalPasteHotkey,
            DecrementalPasteHotkey = normalizedDecrementalHotkey,
            ExternalEditorPath = SettingsExternalEditorPath.Trim(),
            ExternalDiffToolPath = SettingsExternalDiffToolPath.Trim(),
            EnableAi = SettingsEnableAi,
            AiBaseUrl = (SettingsAiBaseUrl ?? string.Empty).Trim(),
            AiApiKey = (SettingsAiApiKey ?? string.Empty).Trim(),
            AiModel = (SettingsAiModel ?? string.Empty).Trim(),
            MaxClipSizeBytes = maxClipSizeBytes,
            CloseToTray = SettingsCloseToTray,
            MinimizeToTray = SettingsMinimizeToTray,
            StartWithWindows = SettingsStartWithWindows,
            ThemeMode = SettingsThemeMode,
            EnableNormalClipLifetime = SettingsEnableNormalClipLifetime,
            NormalClipLifetimeDays = normalClipLifetimeDays,
            EnableSensitiveClipLifetime = SettingsEnableSensitiveClipLifetime,
            SensitiveClipLifetimeMinutes = sensitiveClipLifetimeMinutes,
            EnableMaxLibrarySize = SettingsEnableMaxLibrarySize,
            MaxLibrarySizeMegabytes = maxLibrarySizeMegabytes,
            EnableMaxEntryCount = SettingsEnableMaxEntryCount,
            MaxEntryCount = maxEntryCount,
            UseFuzzyClipSearch = UseFuzzyClipSearch,
            UseFuzzySettingsSearch = SettingsUseFuzzySearch,
        };

        await _storageOptionsService.SaveAsync(storageOptions);
        await _settingsService.SaveAsync(settings);
        if (!_isDatabaseReady)
        {
            await StartDatabaseAsync();
        }

        await _sensitivityService.SaveRulesAsync(sensitivityRules);
        await _clipStoreService.RebuildSensitivityMatchesAsync();
        await ApplyMaintenanceAndRefreshAsync();

        IsWelcomeOpen = false;
        IsSettingsOpen = false;
        StatusText = AppText.SettingsSavedStatus;
        UpdateSelectedClipPresentation();
        RaiseSelectionStateProperties();
    }

    private void LoadSettingsDraft(AppSettings settings)
    {
        SettingsEnableToggleRegexHotkey = settings.EnableToggleRegexHotkey;
        SettingsToggleRegexHotkey = settings.ToggleRegexHotkey;
        SettingsEnableToggleFavoritesHotkey = settings.EnableToggleFavoritesHotkey;
        SettingsToggleFavoritesHotkey = settings.ToggleFavoritesHotkey;
        SettingsEnableToggleSensitiveHotkey = settings.EnableToggleSensitiveHotkey;
        SettingsToggleSensitiveHotkey = settings.ToggleSensitiveHotkey;
        SettingsEnableToggleCaseSensitiveHotkey = settings.EnableToggleCaseSensitiveHotkey;
        SettingsToggleCaseSensitiveHotkey = settings.ToggleCaseSensitiveHotkey;
        SettingsEnableToggleWindowHotkey = settings.EnableToggleWindowHotkey;
        SettingsToggleWindowHotkey = settings.ToggleWindowHotkey;
        SettingsMaxClipSizeKilobytes = (settings.MaxClipSizeBytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture);
        SettingsDatabasePath = _storageOptionsService.Current.DatabasePath;
        SettingsDatabasePassword = _storageOptionsService.Current.DatabasePassword;
        SettingsCloseToTray = settings.CloseToTray;
        SettingsMinimizeToTray = settings.MinimizeToTray;
        SettingsStartWithWindows = settings.StartWithWindows;
        SettingsThemeMode = settings.ThemeMode;
        SettingsEnableNormalClipLifetime = settings.EnableNormalClipLifetime;
        SettingsNormalClipLifetimeDays = settings.NormalClipLifetimeDays.ToString(CultureInfo.InvariantCulture);
        SettingsEnableSensitiveClipLifetime = settings.EnableSensitiveClipLifetime;
        SettingsSensitiveClipLifetimeMinutes = settings.SensitiveClipLifetimeMinutes.ToString(CultureInfo.InvariantCulture);
        SettingsEnableMaxLibrarySize = settings.EnableMaxLibrarySize;
        SettingsMaxLibrarySizeMegabytes = settings.MaxLibrarySizeMegabytes.ToString(CultureInfo.InvariantCulture);
        SettingsEnableMaxEntryCount = settings.EnableMaxEntryCount;
        SettingsMaxEntryCount = settings.MaxEntryCount.ToString(CultureInfo.InvariantCulture);
        SettingsEnableToggleWildcardHotkey = settings.EnableToggleWildcardHotkey;
        SettingsToggleWildcardHotkey = settings.ToggleWildcardHotkey;
        SettingsEnableToggleWholeWordHotkey = settings.EnableToggleWholeWordHotkey;
        SettingsToggleWholeWordHotkey = settings.ToggleWholeWordHotkey;
        SettingsEnableTogglePastedHotkey = settings.EnableTogglePastedHotkey;
        SettingsTogglePastedHotkey = settings.TogglePastedHotkey;
        SettingsEnableIncrementalPasteHotkey = settings.EnableIncrementalPasteHotkey;
        SettingsIncrementalPasteHotkey = settings.IncrementalPasteHotkey;
        SettingsEnableDecrementalPasteHotkey = settings.EnableDecrementalPasteHotkey;
        SettingsDecrementalPasteHotkey = settings.DecrementalPasteHotkey;
        SettingsExternalEditorPath = settings.ExternalEditorPath;
        SettingsExternalDiffToolPath = settings.ExternalDiffToolPath;
        SettingsEnableAi = settings.EnableAi;
        SettingsAiBaseUrl = settings.AiBaseUrl;
        SettingsAiApiKey = settings.AiApiKey;
        SettingsAiModel = settings.AiModel;
        SettingsUseFuzzySearch = settings.UseFuzzySettingsSearch;
        UseFuzzyClipSearch = settings.UseFuzzyClipSearch;
        IsDatabasePasswordVisible = false;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!IsSettingsOpen)
        {
            LoadSettingsDraft(settings);
        }

        UpdateSelectedClipPresentation();
        RaiseSelectionStateProperties();
        this.RaisePropertyChanged(nameof(IsCompareAvailable));
    }

    private async Task StartDatabaseAsync()
    {
        await _databaseInitializer.InitializeAsync();
        if (!_settingsService.HasSavedSettings)
        {
            await _settingsService.InitializeAsync();
        }

        await LoadSensitivityRulesAsync();
        _isDatabaseReady = true;
        _clipboardMonitorService.Start();
        StartMaintenanceLoop();
    }

    private async Task UnlockDatabaseAsync()
    {
        var password = PasswordPromptInput?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            PasswordPromptError = "Please enter a password.";
            return;
        }

        try
        {
            var dbPath = _storageOptionsService.Current.DatabasePath;
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Password = password,
            };

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            PasswordPromptError = "Incorrect password. Please try again.";
            return;
        }

        // Password is correct — store in memory only (never persist to disk)
        _storageOptionsService.SetInMemoryPassword(password);

        PasswordPromptError = string.Empty;
        PasswordPromptInput = string.Empty;

        try
        {
            var draftSettings = _settingsService.HasSavedSettings ? _settingsService.Current : AppSettings.Default;
            LoadSettingsDraft(draftSettings);

            await StartDatabaseAsync();
            await ApplyMaintenanceAndRefreshAsync();

            IsPasswordPromptOpen = false;
        }
        catch (Exception ex)
        {
            PasswordPromptError = $"Failed to start: {ex.Message}";
        }
    }

    private static void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static async Task<string?> PickDatabasePathAsync(IStorageProvider storageProvider, string currentPath)
    {
        var suggestedPath = GetSuggestedDatabasePath(currentPath);
        var suggestedDirectoryPath = Path.GetDirectoryName(suggestedPath);
        IStorageFolder? suggestedFolder = null;
        if (!string.IsNullOrWhiteSpace(suggestedDirectoryPath) && Directory.Exists(suggestedDirectoryPath))
        {
            suggestedFolder = await storageProvider.TryGetFolderFromPathAsync(suggestedDirectoryPath);
        }

        var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = AppText.SettingsBrowseDatabasePathTitle,
            SuggestedFileName = Path.GetFileName(suggestedPath),
            DefaultExtension = "db",
            SuggestedStartLocation = suggestedFolder,
            SuggestedFileType = s_databaseFileType,
            FileTypeChoices = [s_databaseFileType],
        });

        return result?.TryGetLocalPath();
    }

    private static string GetSuggestedDatabasePath(string currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return StorageOptions.GetDefaultDatabasePath();
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(currentPath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return StorageOptions.GetDefaultDatabasePath();
        }
    }

    private async Task LoadSensitivityRulesAsync()
    {
        var rules = _isDatabaseReady
            ? await _sensitivityService.GetRulesAsync()
            : _sensitivityService.GetDefaultRules();
        ReplaceSensitivityRules(rules);
    }

    private void ReplaceSensitivityRules(IReadOnlyList<SensitivityRule> rules)
    {
        SensitivityRules.Clear();
        foreach (var rule in rules)
        {
            SensitivityRules.Add(CreateSensitivityRuleEditor(rule));
        }
    }

    private SensitivityRuleEditorViewModel CreateSensitivityRuleEditor(SensitivityRule rule)
    {
        return new SensitivityRuleEditorViewModel(RemoveSensitivityRule)
        {
            Id = rule.Id,
            Name = rule.Name,
            Pattern = rule.Pattern,
            Severity = rule.Severity,
            IsEnabled = rule.IsEnabled,
            IsBuiltIn = rule.IsBuiltIn,
        };
    }

    private void AddSensitivityRule()
    {
        SensitivityRules.Add(new SensitivityRuleEditorViewModel(RemoveSensitivityRule)
        {
            IsBuiltIn = false,
            IsEnabled = true,
            IsExpanded = true,
        });
    }

    private void RemoveSensitivityRule(SensitivityRuleEditorViewModel rule)
    {
        SensitivityRules.Remove(rule);
    }

    private SensitivityRule[] BuildSensitivityRules()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<SensitivityRule>();
        foreach (var rule in SensitivityRules)
        {
            var name = rule.Name.Trim();
            var pattern = rule.Pattern.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException(AppText.SettingsInvalidRuleName);
            }

            if (pattern.Length == 0)
            {
                throw new ArgumentException(AppText.SettingsInvalidRulePattern);
            }

            if (!names.Add(name))
            {
                throw new ArgumentException(AppText.FormatDuplicateSensitivityRule(name));
            }

            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(AppText.FormatInvalidSensitivityRule(name, ex.Message), ex);
            }

            rules.Add(new SensitivityRule
            {
                Id = rule.Id,
                Name = name,
                Pattern = pattern,
                Severity = string.IsNullOrWhiteSpace(rule.Severity) ? "warning" : rule.Severity.Trim().ToLowerInvariant(),
                IsEnabled = rule.IsEnabled,
                IsBuiltIn = rule.IsBuiltIn,
            });
        }

        return rules.ToArray();
    }

    private async Task ApplyMaintenanceAndRefreshAsync(bool forceRefresh = true)
    {
        if (!_isDatabaseReady)
        {
            return;
        }

        var maintenanceResult = await _clipStoreService.ApplyMaintenanceAsync();
        if (!forceRefresh && maintenanceResult.PurgedClipCount == 0)
        {
            return;
        }

        await LoadSensitivityRulesAsync();
        await RefreshAsync();
    }

    private void StartMaintenanceLoop()
    {
        if (_subscriptions.OfType<SerialDisposable>().Any())
        {
            return;
        }

        var maintenanceSubscription = new SerialDisposable();
        maintenanceSubscription.Disposable = Observable.Interval(TimeSpan.FromMinutes(1), RxApp.TaskpoolScheduler)
            .SelectMany(_ => Observable.FromAsync(() => ApplyMaintenanceAndRefreshAsync(false)))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { }, ex => StatusText = AppText.FormatErrorStatus(ex.Message));
        _subscriptions.Add(maintenanceSubscription);
    }

    private static bool TryParseOptionalPositiveInt(bool isEnabled, string? value, int min, int max, out int parsed)
    {
        parsed = min;
        if (!isEnabled)
        {
            return true;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
               && parsed >= min
               && parsed <= max;
    }

    private static int ParseIntOrDefault(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private string BuildStorageCapacityText()
    {
        if (!SettingsEnableMaxLibrarySize)
        {
            return AppText.UnlimitedCapacityText;
        }

        var megabytes = ParseIntOrDefault(SettingsMaxLibrarySizeMegabytes, AppSettings.DefaultMaxLibrarySizeMegabytes);
        return AppText.FormatStorageCapacity(megabytes);
    }

    private string BuildEntryCapacityText()
    {
        if (!SettingsEnableMaxEntryCount)
        {
            return AppText.UnlimitedCapacityText;
        }

        var maxEntries = ParseIntOrDefault(SettingsMaxEntryCount, AppSettings.DefaultMaxEntryCount);
        return AppText.FormatEntryCapacity(maxEntries);
    }

    private static double BuildUsagePercent(double current, double max)
    {
        if (max <= 0d)
        {
            return 0d;
        }

        return Math.Clamp(current / max * 100d, 0d, 100d);
    }

    private static bool TryParseMaxClipSizeBytes(string? value, out int maxClipSizeBytes)
    {
        maxClipSizeBytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var kilobytes)
            && !double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out kilobytes))
        {
            return false;
        }

        var bytes = (int)Math.Round(kilobytes * 1024d, MidpointRounding.AwayFromZero);
        if (bytes < AppSettings.MinMaxClipSizeBytes || bytes > AppSettings.MaxMaxClipSizeBytes)
        {
            return false;
        }

        maxClipSizeBytes = bytes;
        return true;
    }

    private static bool TryParseAvaloniaGesture(string? value, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            gesture = KeyGesture.Parse(value.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Key NormalizeKey(Key key, PhysicalKey physicalKey)
        => key != Key.None ? key : physicalKey switch
        {
            PhysicalKey.A => Key.A,
            PhysicalKey.B => Key.B,
            PhysicalKey.C => Key.C,
            PhysicalKey.D => Key.D,
            PhysicalKey.E => Key.E,
            PhysicalKey.F => Key.F,
            PhysicalKey.G => Key.G,
            PhysicalKey.H => Key.H,
            PhysicalKey.I => Key.I,
            PhysicalKey.J => Key.J,
            PhysicalKey.K => Key.K,
            PhysicalKey.L => Key.L,
            PhysicalKey.M => Key.M,
            PhysicalKey.N => Key.N,
            PhysicalKey.O => Key.O,
            PhysicalKey.P => Key.P,
            PhysicalKey.Q => Key.Q,
            PhysicalKey.R => Key.R,
            PhysicalKey.S => Key.S,
            PhysicalKey.T => Key.T,
            PhysicalKey.U => Key.U,
            PhysicalKey.V => Key.V,
            PhysicalKey.W => Key.W,
            PhysicalKey.X => Key.X,
            PhysicalKey.Y => Key.Y,
            PhysicalKey.Z => Key.Z,
            PhysicalKey.Digit0 => Key.D0,
            PhysicalKey.Digit1 => Key.D1,
            PhysicalKey.Digit2 => Key.D2,
            PhysicalKey.Digit3 => Key.D3,
            PhysicalKey.Digit4 => Key.D4,
            PhysicalKey.Digit5 => Key.D5,
            PhysicalKey.Digit6 => Key.D6,
            PhysicalKey.Digit7 => Key.D7,
            PhysicalKey.Digit8 => Key.D8,
            PhysicalKey.Digit9 => Key.D9,
            _ => Key.None,
        };

    private void RaiseRenderModeProperties()
    {
        this.RaisePropertyChanged(nameof(ShowRenderedContent));
        this.RaisePropertyChanged(nameof(ShowRawTextContent));
        this.RaisePropertyChanged(nameof(ShowSelectedTextRenderer));
        this.RaisePropertyChanged(nameof(ShowSelectedRichTextRenderer));
        this.RaisePropertyChanged(nameof(ShowSelectedFilesRenderer));
        this.RaisePropertyChanged(nameof(ShowSelectedFilesFallback));
        this.RaisePropertyChanged(nameof(ShowSelectedImageRenderer));
        this.RaisePropertyChanged(nameof(ShowSelectedImageEditor));
        this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
        this.RaisePropertyChanged(nameof(ShowCopyEditedClipButton));
        this.RaisePropertyChanged(nameof(IsSelectedClipTextEditable));
        this.RaisePropertyChanged(nameof(SelectedClipTextIsReadOnly));
        this.RaisePropertyChanged(nameof(RawContentSyntaxHint));
        this.RaisePropertyChanged(nameof(IsRenderedMode));
        this.RaisePropertyChanged(nameof(IsTextualMode));
        this.RaisePropertyChanged(nameof(IsRawMode));
    }

    private async void PersistContentDisplayModeInBackground(ContentDisplayMode mode)
    {
        try
        {
            if (!_settingsService.HasSavedSettings)
            {
                return;
            }

            if (_settingsService.Current.LastContentDisplayMode == mode)
            {
                return;
            }

            await _settingsService.SaveAsync(_settingsService.Current with { LastContentDisplayMode = mode });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to persist display mode: {ex.Message}");
        }
    }

    private void UpdateStatus(ClipSearchResult result)
    {
        _lastCapturedAtRaw = result.LastCapturedAt;
        var lastCaptured = result.LastCapturedAt is null
            ? AppText.NoCapturesYetLower
            : ClipDisplayFormatter.ToRelativeTime(result.LastCapturedAt.Value);

        MatchingClipCount = result.TotalMatchingCount;
        TotalClipCount = result.TotalClipCount;
        SensitiveClipCount = result.SensitiveClipCount;
        TotalStoredBytes = result.TotalStoredBytes;
        LastCaptureSummary = result.LastCapturedAt is null
            ? AppText.NoCapturesYet
            : AppText.FormatLastCapture(lastCaptured);

        StatusText = AppText.FormatStatusSummary(result.TotalMatchingCount, result.TotalClipCount, result.SensitiveClipCount, lastCaptured);
    }

    private void ClearClips()
    {
        foreach (var clip in Clips)
        {
            clip.PropertyChanged -= OnClipItemPropertyChanged;
            clip.Dispose();
        }

        Clips.Clear();
    }

    private void UpdateClipDisplayIndices()
    {
        for (int i = 0; i < Clips.Count; i++)
        {
            Clips[i].DisplayIndex = i + 1;
        }
    }

    private ClipItemViewModel CreateClipItemViewModel(ClipEntry clip, ISet<long>? checkedIds = null)
    {
        var item = new ClipItemViewModel(clip, CopyClipAsync, ToggleFavoriteClipAsync, DeleteClipAsync, ExportClipAsync, TogglePinClipAsync)
        {
            IsChecked = checkedIds?.Contains(clip.Id) == true
        };
        item.PropertyChanged += OnClipItemPropertyChanged;
        return item;
    }

    private void OnClipItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipItemViewModel.IsChecked))
        {
            RaiseBulkSelectionProperties();
        }
    }

    private void RaiseBulkSelectionProperties()
    {
        this.RaisePropertyChanged(nameof(HasCheckedClips));
        this.RaisePropertyChanged(nameof(CheckedClipCount));
        this.RaisePropertyChanged(nameof(CheckedClipSummaryText));
        this.RaisePropertyChanged(nameof(HasCheckedOrSelectedClip));
    }

    private void RaiseEditedClipProperties()
    {
        this.RaisePropertyChanged(nameof(ShowCopyEditedClipButton));
        this.RaisePropertyChanged(nameof(HasEditedClipChanges));
    }

    private void SyncEditedClipText()
    {
        _editedClipBaseline = GetEditedClipBaseline();
        EditedClipText = _editedClipBaseline;
    }

    private string GetEditedClipBaseline() => SelectedClip?.Clip.ContentType switch
    {
        ContentType.Text => SelectedClipRawContent,
        ContentType.RichText when _contentDisplayMode == ContentDisplayMode.Textual => SelectedClipRenderedText,
        ContentType.RichText => SelectedClipRawContent,
        _ when _contentDisplayMode == ContentDisplayMode.Raw => SelectedClipRawContent,
        _ => SelectedClipRenderedText,
    };

    private ClipItemViewModel? GetEffectiveSelectedClip() => SelectedClip ?? Clips.FirstOrDefault(static clip => clip.IsChecked) ?? Clips.FirstOrDefault();

    private ClipItemViewModel[] GetCheckedOrSelectedClips()
    {
        var checkedClips = Clips.Where(static clip => clip.IsChecked).ToArray();
        return checkedClips.Length > 0
            ? checkedClips
            : GetEffectiveSelectedClip() is { } selected
                ? [selected]
                : [];
    }

    private string BuildSelectedClipExpirationText()
    {
        if (SelectedClip is null)
        {
            return AppText.UnlimitedCapacityText;
        }

        DateTimeOffset? expiresAt = null;
        if (SelectedClip.Clip.IsSensitive && _settingsService.Current.EnableSensitiveClipLifetime)
        {
            expiresAt = SelectedClip.Clip.LastCopiedAt.AddMinutes(_settingsService.Current.SensitiveClipLifetimeMinutes);
        }
        else if (!SelectedClip.Clip.IsSensitive && _settingsService.Current.EnableNormalClipLifetime)
        {
            expiresAt = SelectedClip.Clip.LastCopiedAt.AddDays(_settingsService.Current.NormalClipLifetimeDays);
        }

        return expiresAt is null
            ? AppText.UnlimitedCapacityText
            : AppText.FormatExpiresAt(expiresAt.Value.ToLocalTime().ToString("g", AppText.CurrentCulture));
    }

    private void PublishSensitiveCopyNotificationIfNeeded(ClipItemViewModel? clip)
    {
        if (clip?.Clip.IsSensitive != true)
        {
            return;
        }

        _notificationService.Publish(new AppNotification
        {
            Title = AppText.SensitiveClipCopiedTitle,
            Message = AppText.SensitiveClipCopiedMessage,
            Level = clip.HasCriticalSeverity ? AppNotificationLevel.Error : AppNotificationLevel.Warning,
            IsPersistent = true,
            Actions =
            [
                new AppNotificationAction
                {
                    Label = AppText.OpenButtonLabel,
                    ExecuteAsync = () =>
                    {
                        SelectedClip = Clips.FirstOrDefault(current => current.Id == clip.Id) ?? clip;
                        return Task.CompletedTask;
                    }
                },
                new AppNotificationAction
                {
                    Label = AppText.DeleteButtonLabel,
                    ExecuteAsync = async () =>
                    {
                        await _clipStoreService.DeleteAsync(clip.Id);
                        await RefreshAsync();
                    }
                },
                new AppNotificationAction
                {
                    Label = AppText.UnmarkSensitiveButtonLabel,
                    ExecuteAsync = async () =>
                    {
                        await _clipStoreService.ClearSensitivityAsync(clip.Id);
                        await RefreshAsync(clip.Id);
                    }
                }
            ]
        });
    }

    private void WarnIfTargetWindowElevated()
    {
        if (!_systemInteractionService.IsTargetWindowElevated())
        {
            return;
        }

        _notificationService.PublishWarning(
            "Elevated Window Detected",
            "The target window is running as administrator. Paste (Ctrl+V) may not work. Try right-clicking and selecting Paste, or run Clipthrough as administrator.");
    }

    private void ShowNotification(AppNotification notification)
    {
        var item = new AppNotificationViewModel(notification, RemoveNotification);
        Notifications.Insert(0, item);
        while (Notifications.Count > 4)
        {
            Notifications.RemoveAt(Notifications.Count - 1);
        }

        if (!notification.IsPersistent)
        {
            var removal = Observable.Timer(TimeSpan.FromSeconds(6), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => RemoveNotification(item));
            _subscriptions.Add(removal);
        }
    }

    private void RemoveNotification(AppNotificationViewModel item)
    {
        if (Notifications.Contains(item))
        {
            Notifications.Remove(item);
        }
    }

    private static Bitmap? TryLoadImage(ClipEntry? clip, int? maxClipSizeBytes = null)
    {
        if (clip is null)
        {
            return null;
        }

        if (clip.ContentBytes is { Length: > 0 } bytes)
        {
            if (maxClipSizeBytes is { } limit && bytes.Length > limit)
            {
                return null;
            }

            return ClipBitmapFactory.TryLoad(bytes);
        }

        var trimmed = clip.Content.Trim();

        try
        {
            if (trimmed.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = trimmed.IndexOf(',');
                if (commaIndex > -1 && commaIndex < trimmed.Length - 1)
                {
                    if (maxClipSizeBytes is { } limit && Encoding.UTF8.GetByteCount(trimmed) > limit)
                    {
                        return null;
                    }

                    var bytes2 = Convert.FromBase64String(trimmed[(commaIndex + 1)..]);
                    using var stream = new MemoryStream(bytes2);
                    return new Bitmap(stream);
                }
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
            {
                return new Bitmap(uri.LocalPath);
            }

            var unquotedPath = trimmed.Trim('"');
            if (File.Exists(unquotedPath))
            {
                return new Bitmap(unquotedPath);
            }
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"Image preview loading failed for clip {clip.Id}: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"Image preview loading failed for clip {clip.Id}: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Trace.TraceWarning($"Image preview loading failed for clip {clip.Id}: {ex.Message}");
            return null;
        }

        return null;
    }

    private readonly record struct HotkeyDraft(string Name, bool IsEnabled, string HotkeyText);

}
