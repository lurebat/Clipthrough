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
    private readonly IClipAngelImportService _clipAngelImportService;
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
    private readonly IScriptingService _scriptingService;
    private readonly IOcrService _ocrService;
    private readonly IBackgroundOcrQueue _backgroundOcrQueue;
    private readonly IBackgroundJobIndicator _jobIndicator;
    private readonly Clipthrough.Services.Search.ISemanticSearchService? _semanticSearchService;
    private readonly Clipthrough.Services.Search.IEmbeddingWorker? _embeddingWorker;
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
    private int _checkedClipCount;
    private int _checkedTransformableClipCount;
    private ClipFileItemViewModel? _selectedFileItem;
    private bool _hasMoreResults;
    private bool _isBusy;
    private string _statusText = AppText.LoadingStatus;
    private bool _hasRunningJobs;
    private string _runningJobsLabel = string.Empty;
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
    private string _settingsCopyAndFavoriteHotkey = AppSettings.Default.CopyAndFavoriteHotkey;
    private bool _settingsEnableCopyAndFavoriteHotkey = AppSettings.Default.EnableCopyAndFavoriteHotkey;
    private string _settingsCopyAndSensitiveHotkey = AppSettings.Default.CopyAndSensitiveHotkey;
    private bool _settingsEnableCopyAndSensitiveHotkey = AppSettings.Default.EnableCopyAndSensitiveHotkey;
    private string _settingsCopyWithoutSavingHotkey = AppSettings.Default.CopyWithoutSavingHotkey;
    private bool _settingsEnableCopyWithoutSavingHotkey = AppSettings.Default.EnableCopyWithoutSavingHotkey;
    private string _settingsPasteAndDeleteHotkey = AppSettings.Default.PasteAndDeleteHotkey;
    private bool _settingsEnablePasteAndDeleteHotkey = AppSettings.Default.EnablePasteAndDeleteHotkey;
    private string _settingsPasteAndFavoriteHotkey = AppSettings.Default.PasteAndFavoriteHotkey;
    private bool _settingsEnablePasteAndFavoriteHotkey = AppSettings.Default.EnablePasteAndFavoriteHotkey;
    private string _settingsPasteAsPlainTextHotkey = AppSettings.Default.PasteAsPlainTextHotkey;
    private bool _settingsEnablePasteAsPlainTextHotkey = AppSettings.Default.EnablePasteAsPlainTextHotkey;
    private string _settingsExternalEditorPath = AppSettings.Default.ExternalEditorPath;
    private string _settingsExternalDiffToolPath = AppSettings.Default.ExternalDiffToolPath;
    private bool _settingsEnableAi = AppSettings.Default.EnableAi;
    private string _settingsAiBaseUrl = AppSettings.Default.AiBaseUrl;
    private string _settingsAiApiKey = AppSettings.Default.AiApiKey;
    private string _settingsAiModel = AppSettings.Default.AiModel;
    private string _settingsAiReasoningEffort = AppSettings.Default.AiReasoningEffort;
    private bool _settingsEnableAutoUpdate = AppSettings.Default.EnableAutoUpdate;
    private string _settingsUpdateFeedUrl = AppSettings.Default.UpdateFeedUrl;
    private string _settingsOcrLanguages = AppSettings.Default.OcrLanguages;
    private bool _settingsAutoOcrImageClips = AppSettings.Default.AutoOcrImageClips;
    private bool _settingsEnableRemoteApi = AppSettings.Default.EnableRemoteApi;
    private int _settingsRemoteApiPort = AppSettings.Default.RemoteApiPort;
    private string _settingsRemoteApiToken = AppSettings.Default.RemoteApiToken;

    private string _settingsRemoteApiBindAddress = AppSettings.Default.RemoteApiBindAddress;
    private string _editedClipText = string.Empty;
    private string _editedClipBaseline = string.Empty;
    private int _editedClipSelectionStart;
    private int _editedClipSelectionLength;
    private long? _checkedSelectionAnchorId;

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService, IClipSampleDataService clipSampleDataService, ISettingsService settingsService, ISystemInteractionService systemInteractionService, IStorageOptionsService storageOptionsService, ISensitivityService sensitivityService, IAppNotificationService notificationService, ISessionLogService sessionLogService, IClipExportService clipExportService, IImageEditorService imageEditorService, ISearchHistoryService searchHistoryService, IAiTransformService aiTransformService, IScriptingService scriptingService, IOcrService ocrService, IBackgroundOcrQueue backgroundOcrQueue, IBackgroundJobIndicator jobIndicator, DatabaseInitializer databaseInitializer, IClipAngelImportService? clipAngelImportService = null, Clipthrough.Services.Search.ISemanticSearchService? semanticSearchService = null, Clipthrough.Services.Search.IEmbeddingWorker? embeddingWorker = null)
    {
        _clipStoreService = clipStoreService;
        _clipAngelImportService = clipAngelImportService ?? new ClipAngelImportService(clipStoreService);
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
        _scriptingService = scriptingService;
        _ocrService = ocrService;
        _backgroundOcrQueue = backgroundOcrQueue;
        _jobIndicator = jobIndicator;
        _jobIndicator.Changed += OnJobIndicatorChanged;
        _semanticSearchService = semanticSearchService;
        _embeddingWorker = embeddingWorker;
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
        NavigateToLineageSourceCommand = ReactiveCommand.CreateFromTask(NavigateToLineageSourceAsync);
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
        JumpToTopCommand = ReactiveCommand.Create(() => { if (Clips.Count > 0) SelectedClip = Clips[0]; });
        OpenHelpCommand = ReactiveCommand.Create(OpenHelp);
        CloseSettingsCommand = ReactiveCommand.Create(CloseSettings);
        SaveSettingsCommand = ReactiveCommand.CreateFromTask(SaveSettingsAsync);
        BrowseDatabasePathCommand = ReactiveCommand.CreateFromTask<Window?>(BrowseDatabasePathAsync);
        ImportClipAngelCommand = ReactiveCommand.CreateFromTask<Window?>(ImportClipAngelAsync);
        UnlockDatabaseCommand = ReactiveCommand.CreateFromTask(UnlockDatabaseAsync);
        ExitApplicationCommand = ReactiveCommand.Create(ExitApplication);
        OpenAiPromptCommand = ReactiveCommand.Create(OpenAiPrompt);
        SubmitAiPromptCommand = ReactiveCommand.CreateFromTask(SubmitAiPromptAsync);
        CancelAiPromptCommand = ReactiveCommand.Create(CancelAiPrompt);
        ApplyAiPresetCommand = ReactiveCommand.CreateFromTask<AiPreset>(ApplyAiPresetAsync);
        InvokeAiMenuEntryCommand = ReactiveCommand.CreateFromTask<AiMenuEntry>(InvokeAiMenuEntryAsync);
        AddScriptDraftCommand = ReactiveCommand.Create(() =>
        {
            var draft = new UserScriptDraft { Name = "New script", Code = "return input;" };
            SettingsUserScriptDrafts.Add(draft);
            SelectedScriptDraft = draft;
        });
        RemoveScriptDraftCommand = ReactiveCommand.Create<UserScriptDraft>(draft =>
        {
            if (draft is null) return;
            SettingsUserScriptDrafts.Remove(draft);
            SelectedScriptDraft = SettingsUserScriptDrafts.FirstOrDefault();
        });
        AddCustomHotkeyDraftCommand = ReactiveCommand.Create(() =>
        {
            var draft = new CustomHotkeyDraft { Gesture = "Ctrl+Alt+T", Target = "builtin:UpperCase", PasteAfter = true };
            SettingsCustomHotkeyDrafts.Add(draft);
            SelectedCustomHotkeyDraft = draft;
        });
        RemoveCustomHotkeyDraftCommand = ReactiveCommand.Create<CustomHotkeyDraft>(draft =>
        {
            if (draft is null) return;
            SettingsCustomHotkeyDrafts.Remove(draft);
            SelectedCustomHotkeyDraft = SettingsCustomHotkeyDrafts.FirstOrDefault();
        });
        ApplyUserScriptCommand = ReactiveCommand.CreateFromTask<UserScript>(ApplyUserScriptAsync);
        LoadDefaultScriptsCommand = ReactiveCommand.CreateFromTask(LoadDefaultScriptsAsync);
        RunOcrOnSelectedImageCommand = ReactiveCommand.CreateFromTask(RunOcrOnSelectedImageAsync);
        RerunAllEmbeddingsCommand = ReactiveCommand.CreateFromTask(RerunAllEmbeddingsAsync);
        RefreshSemanticCoverageCommand = ReactiveCommand.CreateFromTask(RefreshSemanticCoverageAsync);
        GenerateRemoteApiTokenCommand = ReactiveCommand.Create(() =>
            SettingsRemoteApiToken = System.Guid.NewGuid().ToString("N"));
        CopyRemoteApiTokenCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!string.IsNullOrWhiteSpace(SettingsRemoteApiToken))
            {
                await _systemInteractionService.CopyTextAsync(SettingsRemoteApiToken);
                StatusText = "Remote API token copied";
            }
        });
        CopyRemoteApiDocsUrlCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _systemInteractionService.CopyTextAsync(RemoteApiDocsUrl);
            StatusText = "Swagger URL copied";
        });
        CopyRemoteApiSchemaUrlCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _systemInteractionService.CopyTextAsync(RemoteApiSchemaUrl);
            StatusText = "OpenAPI schema URL copied";
        });
        OpenRemoteApiDocsUrlCommand = ReactiveCommand.CreateFromTask(async () =>
            await _systemInteractionService.OpenUrlAsync(RemoteApiDocsUrl));
        OpenRemoteApiSchemaUrlCommand = ReactiveCommand.CreateFromTask(async () =>
            await _systemInteractionService.OpenUrlAsync(RemoteApiSchemaUrl));

        _settingsService.SettingsChanged += OnSettingsChanged;
        SyncUserScripts(_settingsService.Current);

        // Restore persisted filter toggles
        var savedFilters = _settingsService.Current;
        _showFavoritesOnly = savedFilters.LastShowFavoritesOnly;
        _showSensitiveOnly = savedFilters.LastShowSensitiveOnly;
        _showPastedOnly = savedFilters.LastShowPastedOnly;
        _useRegexSearch = savedFilters.LastUseRegexSearch;
        _caseSensitiveSearch = savedFilters.LastCaseSensitiveSearch;
        _useWildcardSearch = savedFilters.LastUseWildcardSearch;
        _wholeWordSearch = savedFilters.LastWholeWordSearch;
        if (savedFilters.LastContentTypeFilter is { } savedType)
        {
            var match = ContentTypeOptions.FirstOrDefault(o => o.Value == savedType);
            if (match is not null) _selectedContentTypeOption = match;
        }

        // Persist filter toggles on change (debounced)
        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.ShowFavoritesOnly,
                    x => x.ShowSensitiveOnly,
                    x => x.ShowPastedOnly,
                    x => x.UseRegexSearch,
                    x => x.CaseSensitiveSearch,
                    x => x.UseWildcardSearch,
                    x => x.WholeWordSearch,
                    x => x.SelectedContentTypeOption,
                    (_, _, _, _, _, _, _, _) => Unit.Default)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(500), RxSchedulers.MainThreadScheduler)
                .Subscribe(async _ =>
                {
                    try
                    {
                        await _settingsService.SaveAsync(_settingsService.Current with
                        {
                            LastShowFavoritesOnly = ShowFavoritesOnly,
                            LastShowSensitiveOnly = ShowSensitiveOnly,
                            LastShowPastedOnly = ShowPastedOnly,
                            LastUseRegexSearch = UseRegexSearch,
                            LastCaseSensitiveSearch = CaseSensitiveSearch,
                            LastUseWildcardSearch = UseWildcardSearch,
                            LastWholeWordSearch = WholeWordSearch,
                            LastContentTypeFilter = SelectedContentTypeOption.Value,
                        });
                    }
                    catch { /* non-fatal persistence */ }
                }));

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
                        x => x.UseFuzzyClipSearch,
                        x => x.UseSemanticClipSearch)
                    .Select(static _ => Unit.Default))
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(300), RxSchedulers.MainThreadScheduler)
                .InvokeCommand(RefreshCommand));

        _subscriptions.Add(
            _clipboardMonitorService.CapturedClips
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .SelectMany(clip => Observable.FromAsync(() => RefreshAsync(clip.Id)))
                .Subscribe(_ => { }, ex => StatusText = AppText.FormatErrorStatus(ex.Message)));

        _subscriptions.Add(
            _clipboardMonitorService.CapturedClips
                .Subscribe(clip =>
                {
                    if (clip.ContentType == ContentType.Image
                        && _ocrService.IsAvailable
                        && _settingsService.Current.AutoOcrImageClips)
                    {
                        _backgroundOcrQueue.Enqueue(clip.Id);
                    }
                }));

        _subscriptions.Add(
            _backgroundOcrQueue.OcrCompleted
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .SelectMany(id => Observable.FromAsync(() => RefreshAsync(id)))
                .Subscribe(_ => { }, ex => Trace.TraceError($"OCR refresh failed: {ex}")));

        _subscriptions.Add(
            _notificationService.Notifications
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ShowNotification));

        _subscriptions.Add(
            Observable.Interval(TimeSpan.FromSeconds(10), RxSchedulers.MainThreadScheduler)
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
                .ObserveOn(RxSchedulers.MainThreadScheduler)
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

    public ReactiveCommand<Unit, Unit> NavigateToLineageSourceCommand { get; }

    public ReactiveCommand<Unit, Unit> EditSelectedImageCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectAllClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectNoClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> FavoriteCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> PinCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCheckedClipsCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyEditedClipCommand { get; }

    public ReactiveCommand<TextTransformation, Unit> ApplyTextTransformationCommand { get; }

    public ReactiveCommand<UserScript, Unit> ApplyUserScriptCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadDefaultScriptsCommand { get; }

    public ReactiveCommand<Unit, Unit> RunOcrOnSelectedImageCommand { get; }

    public ReactiveCommand<Unit, Unit> RerunAllEmbeddingsCommand { get; }

    public ReactiveCommand<Unit, Unit> RefreshSemanticCoverageCommand { get; }

    public ReactiveCommand<Unit, string> GenerateRemoteApiTokenCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyRemoteApiTokenCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRemoteApiDocsUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRemoteApiSchemaUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenRemoteApiDocsUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenRemoteApiSchemaUrlCommand { get; }

    public ObservableCollection<UserScript> UserScripts { get; } = new();

    public ReactiveCommand<Unit, Unit> AddSensitivityRuleCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }

    public ReactiveCommand<Window?, Unit> BrowseDatabasePathCommand { get; }
    public ReactiveCommand<Window?, Unit> ImportClipAngelCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToTopCommand { get; }

    public ReactiveCommand<Unit, Unit> UnlockDatabaseCommand { get; }

    public ReactiveCommand<Unit, Unit> ExitApplicationCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenAiPromptCommand { get; }

    public ReactiveCommand<Unit, Unit> SubmitAiPromptCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelAiPromptCommand { get; }

    public ReactiveCommand<AiPreset, Unit> ApplyAiPresetCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<AiPreset> AiPresets { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<UserScriptDraft> SettingsUserScriptDrafts { get; } = new();

    private UserScriptDraft? _selectedScriptDraft;
    public UserScriptDraft? SelectedScriptDraft
    {
        get => _selectedScriptDraft;
        set => this.RaiseAndSetIfChanged(ref _selectedScriptDraft, value);
    }

    public ReactiveCommand<Unit, Unit> AddScriptDraftCommand { get; }
    public ReactiveCommand<UserScriptDraft, Unit> RemoveScriptDraftCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<CustomHotkeyDraft> SettingsCustomHotkeyDrafts { get; } = new();

    private CustomHotkeyDraft? _selectedCustomHotkeyDraft;
    public CustomHotkeyDraft? SelectedCustomHotkeyDraft
    {
        get => _selectedCustomHotkeyDraft;
        set => this.RaiseAndSetIfChanged(ref _selectedCustomHotkeyDraft, value);
    }

    public ReactiveCommand<Unit, Unit> AddCustomHotkeyDraftCommand { get; }
    public ReactiveCommand<CustomHotkeyDraft, Unit> RemoveCustomHotkeyDraftCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<AiMenuEntry> AiMenuEntries { get; } = new();

    public ReactiveCommand<AiMenuEntry, Unit> InvokeAiMenuEntryCommand { get; }

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

    public bool HasTransformableTarget
    {
        get
        {
            if (_checkedClipCount > 0)
            {
                return _checkedTransformableClipCount > 0;
            }
            return SelectedClip?.CanAiTransform == true;
        }
    }

    public bool HasSelectedImageClip => SelectedClip?.IsImageClip == true;

    public bool CanRunOcr => HasSelectedImageClip && _ocrService.IsAvailable;

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

    public bool HasRunningJobs
    {
        get => _hasRunningJobs;
        private set => this.RaiseAndSetIfChanged(ref _hasRunningJobs, value);
    }

    public string RunningJobsLabel
    {
        get => _runningJobsLabel;
        private set => this.RaiseAndSetIfChanged(ref _runningJobsLabel, value);
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

    private string BuildFilterTooltip(string label, bool enabled, string hotkey)
        => enabled && !string.IsNullOrWhiteSpace(hotkey) ? $"{label} ({hotkey})" : label;

    public string FavoritesFilterTooltip => BuildFilterTooltip(AppText.FavoritesFilterLabel,
        _settingsService.Current.EnableToggleFavoritesHotkey, _settingsService.Current.ToggleFavoritesHotkey);
    public string SensitiveFilterTooltip => BuildFilterTooltip(AppText.SensitiveFilterLabel,
        _settingsService.Current.EnableToggleSensitiveHotkey, _settingsService.Current.ToggleSensitiveHotkey);
    public string RegexFilterTooltip => BuildFilterTooltip(AppText.RegexFilterLabel,
        _settingsService.Current.EnableToggleRegexHotkey, _settingsService.Current.ToggleRegexHotkey);
    public string CaseSensitiveFilterTooltip => BuildFilterTooltip(AppText.CaseSensitiveFilterLabel,
        _settingsService.Current.EnableToggleCaseSensitiveHotkey, _settingsService.Current.ToggleCaseSensitiveHotkey);
    public string WildcardFilterTooltip => BuildFilterTooltip(AppText.WildcardFilterLabel,
        _settingsService.Current.EnableToggleWildcardHotkey, _settingsService.Current.ToggleWildcardHotkey);
    public string WholeWordFilterTooltip => BuildFilterTooltip(AppText.WholeWordFilterLabel,
        _settingsService.Current.EnableToggleWholeWordHotkey, _settingsService.Current.ToggleWholeWordHotkey);
    public string PastedFilterTooltip => BuildFilterTooltip(AppText.PastedFilterLabel,
        _settingsService.Current.EnableTogglePastedHotkey, _settingsService.Current.TogglePastedHotkey);

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

    public string ImageViewPreviewLabel => AppText.ImageViewPreviewLabel;

    public string ImageViewEditorLabel => AppText.ImageViewEditorLabel;

    public string ImageViewTextLabel => AppText.ImageViewTextLabel;

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

    public string SettingsClipAngelImportTitle => AppText.SettingsClipAngelImportTitle;
    public string SettingsClipAngelImportDescription => AppText.SettingsClipAngelImportDescription;
    public string SettingsClipAngelImportButtonLabel => AppText.SettingsClipAngelImportButtonLabel;
    public bool IsClipAngelImportSupported => _clipAngelImportService.IsSupported;

    private bool _isImportingClipAngel;
    public bool IsImportingClipAngel
    {
        get => _isImportingClipAngel;
        private set => this.RaiseAndSetIfChanged(ref _isImportingClipAngel, value);
    }

    private int _clipAngelImportProcessed;
    public int ClipAngelImportProcessed
    {
        get => _clipAngelImportProcessed;
        private set => this.RaiseAndSetIfChanged(ref _clipAngelImportProcessed, value);
    }

    private int _clipAngelImportTotal;
    public int ClipAngelImportTotal
    {
        get => _clipAngelImportTotal;
        private set => this.RaiseAndSetIfChanged(ref _clipAngelImportTotal, value);
    }

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

    private bool HasSelectedClipImageBytes => SelectedClip?.Clip.ContentBytes is { Length: > 0 };

    public bool ShowSelectedImagePreview => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Preview && HasSelectedClipImageBytes;

    public bool ShowSelectedImageEditor => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Editor && HasSelectedClipImageBytes;

    public bool ShowSelectedImagePlaceholder => ShowSelectedImageRenderer && !HasSelectedClipImageBytes && _imageViewMode != ImageViewMode.Text;

    public bool ShowSelectedImageOcrText => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Text;

    public bool HasSelectedClipOcrText => !string.IsNullOrWhiteSpace(SelectedClip?.Clip.OcrText);

    public bool IsSelectedClipImageOcrRunning => SelectedClip?.Clip.ContentType == ContentType.Image
        && string.Equals(SelectedClip?.Clip.OcrStatus, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsSelectedClipImageOcrPending => SelectedClip?.Clip.ContentType == ContentType.Image
        && (SelectedClip?.Clip.OcrStatus is null
            || string.Equals(SelectedClip?.Clip.OcrStatus, "pending", StringComparison.OrdinalIgnoreCase));

    public bool IsSelectedClipImageOcrFailed => SelectedClip?.Clip.ContentType == ContentType.Image
        && string.Equals(SelectedClip?.Clip.OcrStatus, "failed", StringComparison.OrdinalIgnoreCase);

    public string SelectedClipOcrText => SelectedClip?.Clip.OcrText ?? string.Empty;

    public string SelectedClipOcrStatusText
    {
        get
        {
            var clip = SelectedClip?.Clip;
            if (clip is null || clip.ContentType != ContentType.Image) return string.Empty;
            var status = clip.OcrStatus;
            if (string.IsNullOrEmpty(status)) return "No OCR yet";
            return status switch
            {
                "running" => "OCR running…",
                "pending" => "OCR queued",
                "failed" => string.IsNullOrWhiteSpace(clip.OcrError) ? "OCR failed" : $"OCR failed: {clip.OcrError}",
                "succeeded" => string.IsNullOrWhiteSpace(clip.OcrText) ? "OCR produced no text" : $"OCR: {clip.OcrText.Length} chars",
                _ => status,
            };
        }
    }

    private ImageViewMode _imageViewMode = ImageViewMode.Editor;

    public ImageViewMode SelectedImageViewMode
    {
        get => _imageViewMode;
        set
        {
            if (_imageViewMode == value)
            {
                return;
            }

            _imageViewMode = value;
            this.RaisePropertyChanged(nameof(SelectedImageViewMode));
            this.RaisePropertyChanged(nameof(IsImagePreviewMode));
            this.RaisePropertyChanged(nameof(IsImageEditorMode));
            this.RaisePropertyChanged(nameof(IsImageTextMode));
            this.RaisePropertyChanged(nameof(ShowSelectedImagePreview));
            this.RaisePropertyChanged(nameof(ShowSelectedImageEditor));
            this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
            this.RaisePropertyChanged(nameof(ShowSelectedImageOcrText));
            PersistImageViewModeInBackground(value);
        }
    }

    public bool IsImagePreviewMode
    {
        get => _imageViewMode == ImageViewMode.Preview;
        set { if (value) SelectedImageViewMode = ImageViewMode.Preview; }
    }

    public bool IsImageEditorMode
    {
        get => _imageViewMode == ImageViewMode.Editor;
        set { if (value) SelectedImageViewMode = ImageViewMode.Editor; }
    }

    public bool IsImageTextMode
    {
        get => _imageViewMode == ImageViewMode.Text;
        set { if (value) SelectedImageViewMode = ImageViewMode.Text; }
    }

    public bool HasSelectedClipFileItems => SelectedClipFiles.Count > 0;

    public bool HasCheckedClips => _checkedClipCount > 0;

    public int CheckedClipCount => _checkedClipCount;

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

    public int EditedClipSelectionStart
    {
        get => _editedClipSelectionStart;
        set => this.RaiseAndSetIfChanged(ref _editedClipSelectionStart, value);
    }

    public int EditedClipSelectionLength
    {
        get => _editedClipSelectionLength;
        set => this.RaiseAndSetIfChanged(ref _editedClipSelectionLength, value);
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

    public bool HasSelectedClipMultipleCopies => SelectedClip?.HasMultipleCopies == true;

    public string SelectedClipByteSizeText => SelectedClip?.ByteSizeDisplay ?? AppText.FormatByteCount(0);

    public string SelectedClipExpiresAtText => BuildSelectedClipExpirationText();

    public string SelectedClipImageResolutionText => SelectedClip?.ImageResolutionDisplay ?? AppText.NotAvailable;

    public bool ShowSelectedImageResolutionCard => SelectedClip?.Clip.ContentType == ContentType.Image;

    public string SelectedClipSensitivityText => SelectedClip?.SensitivitySummary ?? AppText.NoClipSelected;

    public string SelectedClipWindowTitleText => SelectedClip?.SourceWindowTitle ?? string.Empty;

    public bool ShowSelectedClipWindowTitle => SelectedClip?.HasSourceWindowTitle == true;

    public bool HasSelectedClipLineage => SelectedClip?.Clip.SourceClipId is not null;

    public string SelectedClipLineageText
    {
        get
        {
            var clip = SelectedClip?.Clip;
            if (clip?.SourceClipId is not { } sourceId)
            {
                return string.Empty;
            }
            var kind = clip.TransformKind;
            if (string.IsNullOrWhiteSpace(kind))
            {
                return $"From clip #{sourceId}";
            }
            var pretty = kind.Contains(':') ? kind.Split(':', 2)[1] : kind;
            var prefix = kind.StartsWith("ai:", StringComparison.Ordinal) ? "AI"
                : kind.StartsWith("script:", StringComparison.Ordinal) ? "script"
                : "transform";
            return $"From clip #{sourceId} via {prefix}: {pretty}";
        }
    }

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

    private string _settingsAiImageModel = AppSettings.Default.AiImageModel;

    public string SettingsAiImageModel
    {
        get => _settingsAiImageModel;
        set => this.RaiseAndSetIfChanged(ref _settingsAiImageModel, value);
    }

    public string SettingsAiReasoningEffort
    {
        get => _settingsAiReasoningEffort;
        set => this.RaiseAndSetIfChanged(ref _settingsAiReasoningEffort, value);
    }

    public System.Collections.Generic.IReadOnlyList<string> AiReasoningEffortOptions { get; } = new[] { "", "none", "minimal", "low", "medium", "high" };

    public bool SettingsEnableAutoUpdate
    {
        get => _settingsEnableAutoUpdate;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableAutoUpdate, value);
    }

    public string SettingsUpdateFeedUrl
    {
        get => _settingsUpdateFeedUrl;
        set => this.RaiseAndSetIfChanged(ref _settingsUpdateFeedUrl, value);
    }

    public string SettingsOcrLanguages
    {
        get => _settingsOcrLanguages;
        set => this.RaiseAndSetIfChanged(ref _settingsOcrLanguages, value);
    }

    public bool SettingsAutoOcrImageClips
    {
        get => _settingsAutoOcrImageClips;
        set => this.RaiseAndSetIfChanged(ref _settingsAutoOcrImageClips, value);
    }

    public bool SettingsEnableRemoteApi
    {
        get => _settingsEnableRemoteApi;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableRemoteApi, value);
    }

    public int SettingsRemoteApiPort
    {
        get => _settingsRemoteApiPort;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsRemoteApiPort, value);
            this.RaisePropertyChanged(nameof(RemoteApiDocsUrl));
            this.RaisePropertyChanged(nameof(RemoteApiSchemaUrl));
        }
    }

    public string SettingsRemoteApiToken
    {
        get => _settingsRemoteApiToken;
        set => this.RaiseAndSetIfChanged(ref _settingsRemoteApiToken, value);
    }

    private bool _isRemoteApiTokenRevealed;
    public bool IsRemoteApiTokenRevealed
    {
        get => _isRemoteApiTokenRevealed;
        set => this.RaiseAndSetIfChanged(ref _isRemoteApiTokenRevealed, value);
    }

    public string SettingsRemoteApiBindAddress
    {
        get => _settingsRemoteApiBindAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsRemoteApiBindAddress, value);
            this.RaisePropertyChanged(nameof(RemoteApiBindAddressIsNonLoopback));
            this.RaisePropertyChanged(nameof(RemoteApiDocsUrl));
            this.RaisePropertyChanged(nameof(RemoteApiSchemaUrl));
        }
    }

    public bool RemoteApiBindAddressIsNonLoopback
    {
        get
        {
            var v = (_settingsRemoteApiBindAddress ?? string.Empty).Trim();
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
            var v = (_settingsRemoteApiBindAddress ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(v)) return "127.0.0.1";
            if (v.Equals("0.0.0.0", StringComparison.Ordinal) || v.Equals("loopback", StringComparison.OrdinalIgnoreCase))
                return "127.0.0.1";
            if (v.Equals("::", StringComparison.Ordinal)) return "[::1]";
            if (v.Contains(':') && !v.StartsWith("[", StringComparison.Ordinal)) return $"[{v}]";
            return v;
        }
    }

    public string RemoteApiDocsUrl => $"http://{RemoteApiUrlHost}:{_settingsRemoteApiPort}/docs";
    public string RemoteApiSchemaUrl => $"http://{RemoteApiUrlHost}:{_settingsRemoteApiPort}/openapi/v1.json";

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

    public string SettingsCopyAndFavoriteHotkey
    {
        get => _settingsCopyAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsCopyAndFavoriteHotkey, value);
    }

    public bool SettingsEnableCopyAndFavoriteHotkey
    {
        get => _settingsEnableCopyAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableCopyAndFavoriteHotkey, value);
    }

    public string SettingsCopyAndSensitiveHotkey
    {
        get => _settingsCopyAndSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsCopyAndSensitiveHotkey, value);
    }

    public bool SettingsEnableCopyAndSensitiveHotkey
    {
        get => _settingsEnableCopyAndSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableCopyAndSensitiveHotkey, value);
    }

    public string SettingsCopyWithoutSavingHotkey
    {
        get => _settingsCopyWithoutSavingHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsCopyWithoutSavingHotkey, value);
    }

    public bool SettingsEnableCopyWithoutSavingHotkey
    {
        get => _settingsEnableCopyWithoutSavingHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableCopyWithoutSavingHotkey, value);
    }

    public string SettingsPasteAndDeleteHotkey
    {
        get => _settingsPasteAndDeleteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsPasteAndDeleteHotkey, value);
    }

    public bool SettingsEnablePasteAndDeleteHotkey
    {
        get => _settingsEnablePasteAndDeleteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnablePasteAndDeleteHotkey, value);
    }

    public string SettingsPasteAndFavoriteHotkey
    {
        get => _settingsPasteAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsPasteAndFavoriteHotkey, value);
    }

    public bool SettingsEnablePasteAndFavoriteHotkey
    {
        get => _settingsEnablePasteAndFavoriteHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnablePasteAndFavoriteHotkey, value);
    }

    public string SettingsPasteAsPlainTextHotkey
    {
        get => _settingsPasteAsPlainTextHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsPasteAsPlainTextHotkey, value);
    }

    public bool SettingsEnablePasteAsPlainTextHotkey
    {
        get => _settingsEnablePasteAsPlainTextHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnablePasteAsPlainTextHotkey, value);
    }

    public string SettingsCopyAndFavoriteHotkeyLabel => AppText.SettingsCopyAndFavoriteHotkeyLabel;
    public string SettingsCopyAndSensitiveHotkeyLabel => AppText.SettingsCopyAndSensitiveHotkeyLabel;
    public string SettingsCopyWithoutSavingHotkeyLabel => AppText.SettingsCopyWithoutSavingHotkeyLabel;
    public string SettingsPasteAndDeleteHotkeyLabel => AppText.SettingsPasteAndDeleteHotkeyLabel;
    public string SettingsPasteAndFavoriteHotkeyLabel => AppText.SettingsPasteAndFavoriteHotkeyLabel;
    public string SettingsPasteAsPlainTextHotkeyLabel => AppText.SettingsPasteAsPlainTextHotkeyLabel;

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

    private bool _useSemanticClipSearch = AppSettings.Default.UseSemanticClipSearch;

    public bool UseSemanticClipSearch
    {
        get => _useSemanticClipSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _useSemanticClipSearch, value);
            this.RaisePropertyChanged(nameof(IsSemanticCoverageVisible));
        }
    }

    private bool _isSettingsSectionBehaviorExpanded = true;
    private bool _isSettingsSectionLocalHotkeysExpanded = true;
    private bool _isSettingsSectionGlobalHotkeyExpanded = true;
    private bool _isSettingsSectionStorageExpanded = true;
    private bool _isSettingsSectionToolsExpanded = true;
    private bool _isSettingsSectionRetentionExpanded = true;
    private bool _isSettingsSectionCapacityExpanded = true;
    private bool _isSettingsSectionSensitivityExpanded = true;
    private bool _isSettingsSectionAiExpanded;
    private bool _isSettingsSectionUpdatesExpanded;
    private bool _isSettingsSectionOcrExpanded;
    private bool _isSettingsSectionRemoteApiExpanded;
    private bool _isSettingsSectionUserScriptsExpanded;
    private bool _isSettingsSectionSemanticExpanded;

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

    public bool IsSettingsSectionAiExpanded
    {
        get => _isSettingsSectionAiExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionAiExpanded, value);
    }

    public bool IsSettingsSectionUpdatesExpanded
    {
        get => _isSettingsSectionUpdatesExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionUpdatesExpanded, value);
    }

    public bool IsSettingsSectionOcrExpanded
    {
        get => _isSettingsSectionOcrExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionOcrExpanded, value);
    }

    public bool IsSettingsSectionRemoteApiExpanded
    {
        get => _isSettingsSectionRemoteApiExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionRemoteApiExpanded, value);
    }

    public bool IsSettingsSectionUserScriptsExpanded
    {
        get => _isSettingsSectionUserScriptsExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionUserScriptsExpanded, value);
    }

    public bool IsSettingsSectionSemanticExpanded
    {
        get => _isSettingsSectionSemanticExpanded;
        set => this.RaiseAndSetIfChanged(ref _isSettingsSectionSemanticExpanded, value);
    }

    // Keywords searched by SettingsFilter. When filter is empty the section shows.
    // When non-empty, the section shows only if its keyword blob contains the filter.
    private static readonly string _behaviorKeywords = "theme dark light tray minimize close start windows startup behavior appearance";
    private static readonly string _localHotkeyKeywords = "hotkey shortcut local regex favorite sensitive case wildcard whole word pasted toggle";
    private static readonly string _globalHotkeyKeywords = "hotkey shortcut global toggle window show hide incremental decremental paste";
    private static readonly string _storageKeywords = "storage database path password encryption sqlite file location clipangel import legacy migration";
    private static readonly string _toolsKeywords = "tools external editor diff winmerge beyond compare vscode meld kdiff";
    private static readonly string _retentionKeywords = "retention lifetime expiry expire clips days normal sensitive minutes age";
    private static readonly string _capacityKeywords = "capacity size library entries count limit max megabytes clip kb kilobytes";
    private static readonly string _sensitivityKeywords = "sensitivity rules pattern regex severity warn block name enabled";
    private static readonly string _aiKeywords = "ai openai chatgpt gpt model api key base url prompt transform";
    private static readonly string _updatesKeywords = "update updates auto-update velopack feed url release version";
    private static readonly string _ocrKeywords = "ocr image text extract recognition language bcp-47 windows.media.ocr";
    private static readonly string _remoteApiKeywords = "remote api http server kestrel bearer token port bind loopback swagger openapi mcp";
    private static readonly string _userScriptsKeywords = "script scripts user roslyn csharp c# code custom transform";
    private static readonly string _semanticKeywords = "semantic embedding embeddings similarity vector search meaning ai ml rerun reembed";

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
    public bool IsSettingsSectionAiVisible => MatchesFilter(_aiKeywords);
    public bool IsSettingsSectionUpdatesVisible => MatchesFilter(_updatesKeywords);
    public bool IsSettingsSectionOcrVisible => MatchesFilter(_ocrKeywords);
    public bool IsSettingsSectionRemoteApiVisible => MatchesFilter(_remoteApiKeywords);
    public bool IsSettingsSectionUserScriptsVisible => MatchesFilter(_userScriptsKeywords);
    public bool IsSettingsSectionSemanticVisible => MatchesFilter(_semanticKeywords);

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
        this.RaisePropertyChanged(nameof(IsSettingsSectionAiVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionUpdatesVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionOcrVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionRemoteApiVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionUserScriptsVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionSemanticVisible));

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
            IsSettingsSectionAiExpanded = IsSettingsSectionAiVisible;
            IsSettingsSectionUpdatesExpanded = IsSettingsSectionUpdatesVisible;
            IsSettingsSectionOcrExpanded = IsSettingsSectionOcrVisible;
            IsSettingsSectionRemoteApiExpanded = IsSettingsSectionRemoteApiVisible;
            IsSettingsSectionUserScriptsExpanded = IsSettingsSectionUserScriptsVisible;
            IsSettingsSectionSemanticExpanded = IsSettingsSectionSemanticVisible;
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
        _jobIndicator.Changed -= OnJobIndicatorChanged;
        SelectedClipFiles.Clear();
        ClearClips();
        SessionLogs.Dispose();
        _subscriptions.Dispose();
    }

    private void OnJobIndicatorChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var labels = _jobIndicator.ActiveLabels;
            HasRunningJobs = labels.Count > 0;
            RunningJobsLabel = labels.Count switch
            {
                0 => string.Empty,
                1 => labels[0],
                _ => $"{labels[0]} (+{labels.Count - 1} more)",
            };
        });
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
            _imageViewMode = draftSettings.LastImageViewMode;
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
            result = await ApplySemanticFusionAsync(filters, result);
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

    private async Task<ClipSearchResult> ApplySemanticFusionAsync(ClipSearchFilters filters, ClipSearchResult ftsResult)
    {
        if (_semanticSearchService is null)
        {
            return ftsResult;
        }
        if (!UseSemanticClipSearch)
        {
            return ftsResult;
        }
        if (string.IsNullOrWhiteSpace(filters.SearchText))
        {
            return ftsResult;
        }
        // Exact-mode gating: regex/wildcard/whole-word are precise operators; semantic would only dilute.
        if (filters.UseRegex || filters.UseWildcard || filters.WholeWord)
        {
            return ftsResult;
        }
        if (!_semanticSearchService.IsReady)
        {
            return ftsResult;
        }

        var topK = Math.Max(filters.Limit * 2, 50);
        IReadOnlyList<(long ClipId, float Score)> semantic;
        try
        {
            semantic = await _semanticSearchService.QueryAsync(filters.SearchText, topK);
        }
        catch
        {
            return ftsResult;
        }
        if (semantic.Count == 0)
        {
            return ftsResult;
        }

        var ftsIds = new HashSet<long>(ftsResult.Items.Select(c => c.Id));
        var missingIds = new List<long>();
        foreach (var hit in semantic)
        {
            if (!ftsIds.Contains(hit.ClipId))
            {
                missingIds.Add(hit.ClipId);
            }
        }

        IReadOnlyList<ClipEntry> extraClips = missingIds.Count > 0
            ? await _clipStoreService.GetByIdsAsync(missingIds)
            : Array.Empty<ClipEntry>();

        // Respect non-text filters on semantic-only additions (FTS results already honor them).
        var extraFiltered = extraClips.Where(c => MatchesNonTextFilters(c, filters)).ToList();

        var allClips = new Dictionary<long, ClipEntry>();
        foreach (var c in ftsResult.Items) allClips[c.Id] = c;
        foreach (var c in extraFiltered) allClips[c.Id] = c;

        var ftsRank = new Dictionary<long, int>();
        for (var i = 0; i < ftsResult.Items.Count; i++) ftsRank[ftsResult.Items[i].Id] = i;
        var semRank = new Dictionary<long, int>();
        for (var i = 0; i < semantic.Count; i++) semRank[semantic[i].ClipId] = i;

        const double rrfK = 60.0;
        var fused = allClips.Values
            .Select(c =>
            {
                double score = 0;
                if (ftsRank.TryGetValue(c.Id, out var r1)) score += 1.0 / (rrfK + r1);
                if (semRank.TryGetValue(c.Id, out var r2)) score += 1.0 / (rrfK + r2);
                return (Clip: c, Score: score);
            })
            // Keep pinned entries on top (mirrors FTS ORDER BY pinned_at).
            .OrderByDescending(t => t.Clip.PinnedAt.HasValue)
            .ThenByDescending(t => t.Clip.PinnedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(t => t.Score)
            .Take(filters.Limit)
            .Select(t => t.Clip)
            .ToList();

        return new ClipSearchResult
        {
            Items = fused,
            TotalMatchingCount = Math.Max(ftsResult.TotalMatchingCount, fused.Count + (extraFiltered.Count - Math.Min(extraFiltered.Count, Math.Max(0, fused.Count - ftsResult.Items.Count)))),
            TotalClipCount = ftsResult.TotalClipCount,
            SensitiveClipCount = ftsResult.SensitiveClipCount,
            TotalStoredBytes = ftsResult.TotalStoredBytes,
            LastCapturedAt = ftsResult.LastCapturedAt,
        };
    }

    private static bool MatchesNonTextFilters(ClipEntry clip, ClipSearchFilters filters)
    {
        if (filters.ContentType.HasValue && clip.ContentType != filters.ContentType.Value) return false;
        if (filters.FavoritesOnly && !clip.IsFavorite) return false;
        if (filters.SensitiveOnly && !clip.IsSensitive) return false;
        if (filters.PastedOnly && !clip.IsPasted) return false;
        return true;
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

    private static string ExtractExecutablePath(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }
        var trimmed = template.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 0 ? trimmed.Substring(1, end - 1) : trimmed.Substring(1);
        }
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed.Substring(0, space) : trimmed;
    }

    private async Task OpenInEditorAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        var editorPath = _settingsService.Current.ExternalEditorPath;
        if (string.IsNullOrWhiteSpace(editorPath))
        {
            _notificationService.PublishWarning(
                "No external editor configured",
                "Set an editor path in Settings → Tools, or the clip will open with the OS default.");
        }
        else if (!System.IO.File.Exists(ExtractExecutablePath(editorPath)))
        {
            _notificationService.PublishError(
                "External editor not found",
                $"'{ExtractExecutablePath(editorPath)}' does not exist. Update the path in Settings → Tools.");
            return;
        }

        var exportResult = await _clipExportService.ExportAsync(SelectedClip.Clip);
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

    private async Task NavigateToLineageSourceAsync()
    {
        if (SelectedClip?.Clip.SourceClipId is not { } sourceId)
        {
            return;
        }

        var existing = Clips.FirstOrDefault(c => c.Clip.Id == sourceId);
        if (existing is not null)
        {
            SelectedClip = existing;
            return;
        }

        try
        {
            var source = await _clipStoreService.GetByIdAsync(sourceId);
            if (source is null)
            {
                StatusText = $"Clip #{sourceId} no longer exists.";
                return;
            }
            await RefreshAsync(sourceId);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load clip #{sourceId}: {ex.Message}";
        }
    }

    private async Task CompareClipsAsync()
    {
        var checkedClips = Clips.Where(static c => c.IsChecked).Take(2).ToList();
        if (checkedClips.Count < 2)
        {
            StatusText = AppText.CompareNeedsTwoClipsStatus;
            _notificationService.PublishWarning(
                "Check two clips to compare",
                "Use the checkboxes on two clips in the list, then open Compare again.");
            return;
        }

        var diffToolPath = _settingsService.Current.ExternalDiffToolPath;
        if (string.IsNullOrWhiteSpace(diffToolPath))
        {
            StatusText = AppText.CompareNeedsDiffToolStatus;
            _notificationService.PublishWarning(
                "No diff tool configured",
                "Set a diff tool path in Settings → Tools (e.g. WinMerge, Beyond Compare, VS Code).");
            return;
        }
        if (!System.IO.File.Exists(ExtractExecutablePath(diffToolPath)))
        {
            _notificationService.PublishError(
                "Diff tool not found",
                $"'{ExtractExecutablePath(diffToolPath)}' does not exist. Update the path in Settings → Tools.");
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

        var text = EditedClipText ?? string.Empty;
        var contentType = SelectedClip.Clip.ContentType;
        var isRich = contentType == ContentType.RichText;

        // Put on clipboard (suppressed so we don't race our own capture)
        _clipboardMonitorService.SuppressNext();
        if (isRich && _contentDisplayMode == ContentDisplayMode.Raw)
        {
            var renderedText = ClipDisplayFormatter.RenderRichContent(text);
            await _systemInteractionService.CopyRichContentAsync(text, renderedText, SelectedClip.Clip.ContentFormat);
        }
        else
        {
            await _systemInteractionService.CopyTextAsync(text);
        }

        // Also persist a brand-new clip entry so "Copy as new" is visible in history
        ClipEntry? captured = null;
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            captured = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = bytes,
                ContentText = text,
                ContentType = isRich ? ContentType.RichText : ContentType.Text,
                ContentFormat = isRich ? SelectedClip.Clip.ContentFormat : ClipContentFormat.PlainText,
                SourceApp = SelectedClip.SourceApp,
                SourceAppPath = SelectedClip.Clip.SourceAppPath,
                SourceAppIconBytes = SelectedClip.Clip.SourceAppIconBytes,
                SourceWindowTitle = SelectedClip.Clip.SourceWindowTitle,
                IncrementExistingCopyCount = false,
            });
        }

        _editedClipBaseline = text;
        RaiseEditedClipProperties();
        PublishSensitiveCopyNotificationIfNeeded(SelectedClip);
        if (captured is not null)
        {
            await RefreshAsync(captured.Id);
        }
        StatusText = AppText.EditedClipCopiedStatus;
    }

    public async Task CommitEditedClipOnFocusLossAsync() => await CommitEditedClipOnSelectionChangeAsync();

    private async Task ApplyTextTransformationAsync(TextTransformation transformation)
    {
        if (transformation == TextTransformation.None)
        {
            return;
        }

        await ApplyTransformToTargetsAsync(
            (source, _) => Task.FromResult(TextTransformationService.Apply(transformation, source)),
            $"transformation",
            multiSummary: count => $"Applied transformation to {count} clips",
            transformKind: $"builtin:{transformation}");
    }

    private async Task ApplyTransformationToSingleClipAsync(ClipItemViewModel clip, TextTransformation transformation)
    {
        if (clip is null || transformation == TextTransformation.None)
        {
            return;
        }

        if (clip.Clip.ContentType != ContentType.Text && clip.Clip.ContentType != ContentType.RichText)
        {
            return;
        }

        var source = clip.Clip.Content ?? string.Empty;
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        var result = TextTransformationService.Apply(transformation, source);
        if (string.Equals(result, source, StringComparison.Ordinal))
        {
            StatusText = "Transformation produced no change";
            return;
        }

        var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
        var captured = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = textBytes,
            ContentText = result,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = clip.SourceApp,
            SourceAppPath = clip.Clip.SourceAppPath,
            SourceAppIconBytes = clip.Clip.SourceAppIconBytes,
            SourceWindowTitle = clip.Clip.SourceWindowTitle,
            IncrementExistingCopyCount = false,
            SourceClipId = clip.Clip.Id,
            TransformKind = $"builtin:{transformation}",
        });
        StatusText = AppText.EditedClipCopiedStatus;
        await RefreshAsync(captured?.Id);
    }

    private async Task ApplyUserScriptAsync(UserScript? script)
    {
        if (script is null || string.IsNullOrWhiteSpace(script.Code))
        {
            return;
        }

        var previous = StatusText;
        StatusText = $"Running script '{script.Name}'…";
        try
        {
            await _jobIndicator.TrackAsync($"Script: {script.Name}", () => ApplyTransformToTargetsAsync(
                (source, ct) => Task.Run(() => _scriptingService.EvaluateAsync(script.Code, source, ct), ct),
                $"script '{script.Name}'",
                multiSummary: count => $"Applied '{script.Name}' to {count} clips",
                transformKind: $"script:{script.Name}"));
        }
        catch (Exception ex)
        {
            StatusText = $"Script '{script.Name}' failed: {ex.Message}";
            _notificationService.PublishError($"Script '{script.Name}' failed", ex.Message);
            return;
        }

        if (string.Equals(StatusText, $"Running script '{script.Name}'…", StringComparison.Ordinal))
        {
            StatusText = previous;
        }
    }

    private async Task ApplyTransformToTargetsAsync(
        Func<string, CancellationToken, Task<string>> transform,
        string singleLabel,
        Func<int, string> multiSummary,
        string? transformKind = null)
    {
        var checkedClips = Clips.Where(static c => c.IsChecked).ToList();
        var useSelectionSlice = checkedClips.Count == 0
            && SelectedClip is not null
            && EditedClipSelectionLength > 0
            && EditedClipSelectionStart >= 0
            && EditedClipSelectionStart + EditedClipSelectionLength <= (EditedClipText?.Length ?? 0);

        var targets = checkedClips.Count > 0
            ? checkedClips
            : SelectedClip is not null ? new List<ClipItemViewModel> { SelectedClip } : new List<ClipItemViewModel>();

        var transformed = 0;
        long? lastCreatedId = null;
        foreach (var target in targets)
        {
            if (target.Clip.ContentType != ContentType.Text && target.Clip.ContentType != ContentType.RichText)
            {
                continue;
            }

            string source;
            string result;
            if (useSelectionSlice && ReferenceEquals(target, SelectedClip))
            {
                var full = EditedClipText ?? string.Empty;
                var slice = full.Substring(EditedClipSelectionStart, EditedClipSelectionLength);
                var transformedSlice = await transform(slice, CancellationToken.None);
                if (string.Equals(slice, transformedSlice, StringComparison.Ordinal))
                {
                    continue;
                }
                source = full;
                result = full.Substring(0, EditedClipSelectionStart)
                    + transformedSlice
                    + full.Substring(EditedClipSelectionStart + EditedClipSelectionLength);
            }
            else
            {
                source = target.Clip.Content ?? string.Empty;
                if (string.IsNullOrEmpty(source))
                {
                    continue;
                }
                result = await transform(source, CancellationToken.None);
                if (string.Equals(result, source, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
            var captured = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = textBytes,
                ContentText = result,
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = target.SourceApp,
                SourceAppPath = target.Clip.SourceAppPath,
                SourceAppIconBytes = target.Clip.SourceAppIconBytes,
                SourceWindowTitle = target.Clip.SourceWindowTitle,
                IncrementExistingCopyCount = false,
                SourceClipId = target.Clip.Id,
                TransformKind = transformKind,
            });
            if (captured is not null)
            {
                lastCreatedId = captured.Id;
            }
            transformed++;
        }

        if (transformed > 0)
        {
            StatusText = transformed == 1
                ? (useSelectionSlice ? $"Applied {singleLabel} to selection" : AppText.EditedClipCopiedStatus)
                : multiSummary(transformed);
            await RefreshAsync(lastCreatedId);
        }
    }

    // Old single-clip script body replaced above; keep old method signature for legacy callers (none left).

    private async Task LoadDefaultScriptsAsync()
    {
        var existing = _settingsService.Current.UserScripts?.ToList() ?? new List<UserScript>();
        var names = new HashSet<string>(existing.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var def in ScriptingService.GetDefaultScripts())
        {
            if (names.Add(def.Name))
            {
                existing.Add(def);
            }
        }

        await _settingsService.SaveAsync(_settingsService.Current with { UserScripts = existing });
        StatusText = "Loaded default scripts";
    }

    private async Task RunOcrOnSelectedImageAsync()
    {
        var clip = SelectedClip;
        if (clip is null || clip.Clip.ContentType != ContentType.Image || clip.Clip.ContentBytes is null)
        {
            StatusText = "Select an image clip first";
            return;
        }

        if (!_ocrService.IsAvailable)
        {
            StatusText = "No Windows OCR languages installed. Add one in Windows Settings → Time & Language → Language.";
            return;
        }

        await _clipStoreService.MarkOcrForRerunAsync(clip.Clip.Id);
        StatusText = "Queued OCR…";
        _backgroundOcrQueue.Enqueue(clip.Clip.Id);
    }

    private string _semanticCoverageText = string.Empty;
    public string SemanticCoverageText
    {
        get => _semanticCoverageText;
        private set => this.RaiseAndSetIfChanged(ref _semanticCoverageText, value);
    }

    public bool IsSemanticCoverageVisible => _embeddingWorker is not null && UseSemanticClipSearch && !string.IsNullOrEmpty(SemanticCoverageText);

    private async Task RefreshSemanticCoverageAsync()
    {
        if (_embeddingWorker is null)
        {
            SemanticCoverageText = string.Empty;
            this.RaisePropertyChanged(nameof(IsSemanticCoverageVisible));
            return;
        }
        try
        {
            var coverage = await _embeddingWorker.GetCoverageAsync();
            var eligible = coverage.EligibleTotal;
            if (eligible <= 0)
            {
                SemanticCoverageText = "No clips to embed yet";
            }
            else
            {
                var pct = (int)Math.Round(100.0 * coverage.Embedded / eligible);
                var suffix = coverage.Failed > 0 ? $" · {coverage.Failed} failed" : string.Empty;
                SemanticCoverageText = $"Semantic: {coverage.Embedded}/{eligible} ({pct}%){suffix}";
            }
        }
        catch
        {
            SemanticCoverageText = string.Empty;
        }
        this.RaisePropertyChanged(nameof(IsSemanticCoverageVisible));
    }

    private async Task RerunAllEmbeddingsAsync()
    {
        if (_embeddingWorker is null)
        {
            StatusText = "Embedding worker not available";
            return;
        }
        try
        {
            await _embeddingWorker.RerunAllAsync();
            _embeddingWorker.Poke();
            StatusText = "Queued all clips for re-embedding";
            await RefreshSemanticCoverageAsync();
        }
        catch (Exception ex)
        {
            StatusText = AppText.FormatErrorStatus(ex.Message);
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
            DetachClip(vm);
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
        this.RaisePropertyChanged(nameof(HasSelectedClipMultipleCopies));
        this.RaisePropertyChanged(nameof(HasSelectedClipLineage));
        this.RaisePropertyChanged(nameof(SelectedClipLineageText));
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
        this.RaisePropertyChanged(nameof(HasTransformableTarget));
        this.RaisePropertyChanged(nameof(HasSelectedImageClip));
        this.RaisePropertyChanged(nameof(CanRunOcr));
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

    private Task SubmitAiPromptAsync() => SubmitAiPromptAsync(transformKind: "ai:custom", presetLabel: null);

    private Task SubmitAiPromptAsync(string transformKind, string? presetLabel)
    {
        if (IsAiPromptBusy)
        {
            return Task.CompletedTask;
        }
        var prompt = (AiPromptInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            AiPromptError = "Please enter a prompt.";
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        }

        var useSelectionSlice = checkedClips.Count == 0
            && SelectedClip is not null
            && ReferenceEquals(targets[0], SelectedClip)
            && EditedClipSelectionLength > 0
            && EditedClipSelectionStart >= 0
            && EditedClipSelectionStart + EditedClipSelectionLength <= (EditedClipText?.Length ?? 0);
        var sliceStart = EditedClipSelectionStart;
        var sliceLength = EditedClipSelectionLength;
        var fullEditedText = EditedClipText ?? string.Empty;
        var selectedClipRef = SelectedClip;

        IsAiPromptOpen = false;
        AiPromptError = string.Empty;
        AiPromptInput = string.Empty;
        StatusText = presetLabel is null ? "AI transform running…" : $"Running AI preset '{presetLabel}'…";

        var label = presetLabel is null
            ? $"AI: {Shorten(prompt, 40)}"
            : $"AI: {presetLabel}";

        _ = _jobIndicator.TrackAsync(label, () => RunAiPromptAsync(
            prompt,
            targets,
            useSelectionSlice,
            sliceStart,
            sliceLength,
            fullEditedText,
            selectedClipRef,
            transformKind,
            presetLabel));

        return Task.CompletedTask;
    }

    private async Task RunAiPromptAsync(
        string prompt,
        List<ClipItemViewModel> targets,
        bool useSelectionSlice,
        int sliceStart,
        int sliceLength,
        string fullEditedText,
        ClipItemViewModel? selectedClipRef,
        string transformKind,
        string? presetLabel)
    {
        long? lastCreatedId = null;
        var produced = 0;
        Exception? failure = null;

        foreach (var target in targets)
        {
            string source;
            string result;
            try
            {
                if (useSelectionSlice && ReferenceEquals(target, selectedClipRef))
                {
                    var slice = fullEditedText.Substring(sliceStart, sliceLength);
                    var transformedSlice = await _aiTransformService.TransformAsync(prompt, slice);
                    if (string.IsNullOrEmpty(transformedSlice) || string.Equals(slice, transformedSlice, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    source = fullEditedText;
                    result = fullEditedText.Substring(0, sliceStart)
                        + transformedSlice
                        + fullEditedText.Substring(sliceStart + sliceLength);
                }
                else
                {
                    source = target.Clip.Content ?? string.Empty;
                    result = await _aiTransformService.TransformAsync(prompt, source);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            if (string.IsNullOrEmpty(result) || string.Equals(result, source, StringComparison.Ordinal))
            {
                continue;
            }

            var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
            var captured = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = textBytes,
                ContentText = result,
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = target.SourceApp,
                SourceAppPath = target.Clip.SourceAppPath,
                SourceAppIconBytes = target.Clip.SourceAppIconBytes,
                SourceWindowTitle = target.Clip.SourceWindowTitle,
                IncrementExistingCopyCount = false,
                SourceClipId = target.Clip.Id,
                TransformKind = transformKind,
            });
            if (captured is not null)
            {
                lastCreatedId = captured.Id;
            }
            produced++;
        }

        if (failure is not null)
        {
            var title = presetLabel is null ? "AI transform failed" : $"AI preset '{presetLabel}' failed";
            _notificationService.PublishError(title, failure.Message);
            StatusText = failure.Message;
            return;
        }

        if (produced > 0)
        {
            StatusText = produced == 1
                ? "AI transform produced a new clip."
                : $"AI transform produced {produced} new clips.";
            await RefreshAsync(lastCreatedId);
        }
        else
        {
            var message = "AI transform returned no new content. Check the provider or refine the prompt.";
            StatusText = message;
            _notificationService.PublishWarning(
                presetLabel is null ? "AI transform" : $"AI preset '{presetLabel}'",
                message);
        }
    }

    private static string Shorten(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }
        return value.Substring(0, max) + "…";
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

    private async Task ImportClipAngelAsync(Window? window)
    {
        if (window?.StorageProvider is null)
            return;

        if (!_clipAngelImportService.IsSupported)
        {
            StatusText = AppText.ClipAngelImportUnsupported;
            return;
        }

        IStorageFolder? startFolder = null;
        try
        {
            var defaultDir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\ClipAngel");
            if (Directory.Exists(defaultDir))
                startFolder = await window.StorageProvider.TryGetFolderFromPathAsync(defaultDir);
        }
        catch { /* best-effort */ }

        var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppText.SettingsClipAngelImportPickerTitle,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = [s_databaseFileType],
        });

        if (picked is null || picked.Count == 0)
            return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        StatusText = AppText.ClipAngelImportRunning;
        IsBusy = true;
        IsImportingClipAngel = true;
        ClipAngelImportProcessed = 0;
        ClipAngelImportTotal = 0;
        try
        {
            var progress = new Progress<ClipAngelImportProgress>(p =>
            {
                ClipAngelImportProcessed = p.Processed;
                ClipAngelImportTotal = p.Total;
                StatusText = AppText.FormatClipAngelImportProgress(p.Processed, p.Total);
            });
            var result = await _clipAngelImportService.ImportAsync(path!, progress);
            var msg = AppText.FormatClipAngelImportSuccess(result.Imported, result.Skipped, result.Failed);
            StatusText = msg;
            _notificationService.PublishInfo(AppText.SettingsClipAngelImportTitle, msg);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = AppText.FormatClipAngelImportError(ex.Message);
            _notificationService.PublishError(AppText.SettingsClipAngelImportTitle, AppText.FormatClipAngelImportError(ex.Message));
        }
        finally
        {
            IsBusy = false;
            IsImportingClipAngel = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        var previousAutoOcr = _settingsService.Current.AutoOcrImageClips;
        var previousOcrLanguages = (_settingsService.Current.OcrLanguages ?? string.Empty).Trim();

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

        var extendedHotkeys = new[]
        {
            ("copy-and-favorite", SettingsEnableCopyAndFavoriteHotkey, SettingsCopyAndFavoriteHotkey),
            ("copy-and-sensitive", SettingsEnableCopyAndSensitiveHotkey, SettingsCopyAndSensitiveHotkey),
            ("copy-without-saving", SettingsEnableCopyWithoutSavingHotkey, SettingsCopyWithoutSavingHotkey),
            ("paste-and-delete", SettingsEnablePasteAndDeleteHotkey, SettingsPasteAndDeleteHotkey),
            ("paste-and-favorite", SettingsEnablePasteAndFavoriteHotkey, SettingsPasteAndFavoriteHotkey),
            ("paste-as-plain-text", SettingsEnablePasteAsPlainTextHotkey, SettingsPasteAsPlainTextHotkey),
        };
        var normalizedExtended = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, enabled, raw) in extendedHotkeys)
        {
            var norm = (raw ?? string.Empty).Trim();
            if (enabled)
            {
                if (!HotkeyGesture.TryParse(norm, out var parsed, out var err) || parsed is null)
                {
                    StatusText = AppText.FormatSettingsValidationError(err ?? AppText.SettingsInvalidHotkeyFallback);
                    return;
                }
                norm = parsed.ToString();
            }
            normalizedExtended[id] = norm;
        }

        var duplicates = localHotkeys
            .Where(static draft => draft.IsEnabled)
            .Select(draft => normalizedHotkeys[draft.Name])
            .Append(SettingsEnableToggleWindowHotkey ? normalizedGlobalHotkey : string.Empty)
            .Append(SettingsEnableIncrementalPasteHotkey ? normalizedIncrementalHotkey : string.Empty)
            .Append(SettingsEnableDecrementalPasteHotkey ? normalizedDecrementalHotkey : string.Empty)
            .Concat(extendedHotkeys.Where(h => h.Item2).Select(h => normalizedExtended[h.Item1]))
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
            EnableCopyAndFavoriteHotkey = SettingsEnableCopyAndFavoriteHotkey,
            CopyAndFavoriteHotkey = normalizedExtended["copy-and-favorite"],
            EnableCopyAndSensitiveHotkey = SettingsEnableCopyAndSensitiveHotkey,
            CopyAndSensitiveHotkey = normalizedExtended["copy-and-sensitive"],
            EnableCopyWithoutSavingHotkey = SettingsEnableCopyWithoutSavingHotkey,
            CopyWithoutSavingHotkey = normalizedExtended["copy-without-saving"],
            EnablePasteAndDeleteHotkey = SettingsEnablePasteAndDeleteHotkey,
            PasteAndDeleteHotkey = normalizedExtended["paste-and-delete"],
            EnablePasteAndFavoriteHotkey = SettingsEnablePasteAndFavoriteHotkey,
            PasteAndFavoriteHotkey = normalizedExtended["paste-and-favorite"],
            EnablePasteAsPlainTextHotkey = SettingsEnablePasteAsPlainTextHotkey,
            PasteAsPlainTextHotkey = normalizedExtended["paste-as-plain-text"],
            ExternalEditorPath = SettingsExternalEditorPath.Trim(),
            ExternalDiffToolPath = SettingsExternalDiffToolPath.Trim(),
            EnableAi = SettingsEnableAi,
            AiBaseUrl = (SettingsAiBaseUrl ?? string.Empty).Trim(),
            AiApiKey = (SettingsAiApiKey ?? string.Empty).Trim(),
            AiModel = (SettingsAiModel ?? string.Empty).Trim(),
            AiImageModel = (SettingsAiImageModel ?? string.Empty).Trim(),
            AiReasoningEffort = (SettingsAiReasoningEffort ?? string.Empty).Trim(),
            EnableAutoUpdate = SettingsEnableAutoUpdate,
            UpdateFeedUrl = (SettingsUpdateFeedUrl ?? string.Empty).Trim(),
            OcrLanguages = (SettingsOcrLanguages ?? string.Empty).Trim(),
            AutoOcrImageClips = SettingsAutoOcrImageClips,
            EnableRemoteApi = SettingsEnableRemoteApi,
            RemoteApiPort = SettingsRemoteApiPort,
            RemoteApiToken = (SettingsRemoteApiToken ?? string.Empty).Trim(),
            RemoteApiBindAddress = (SettingsRemoteApiBindAddress ?? string.Empty).Trim(),
            UserScripts = SettingsUserScriptDrafts
                .Select(s => new UserScript { Name = s.Name.Trim(), Code = s.Code })
                .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Code))
                .ToList(),
            CustomHotkeys = SettingsCustomHotkeyDrafts
                .Select(d => d.ToBinding())
                .Where(b => !string.IsNullOrWhiteSpace(b.Gesture) && !string.IsNullOrWhiteSpace(b.Target))
                .ToList(),
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
            UseSemanticClipSearch = UseSemanticClipSearch,
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

        if (_isDatabaseReady && _ocrService.IsAvailable)
        {
            var nowAutoOcr = settings.AutoOcrImageClips;
            var nowOcrLanguages = (settings.OcrLanguages ?? string.Empty).Trim();
            var languagesChanged = !string.Equals(nowOcrLanguages, previousOcrLanguages, StringComparison.OrdinalIgnoreCase);
            if (nowAutoOcr && (!previousAutoOcr || languagesChanged))
            {
                if (languagesChanged)
                {
                    await _clipStoreService.MarkAllSucceededForRerunAsync();
                }
                _ = Task.Run(() => _backgroundOcrQueue.EnqueueBacklogAsync());
            }
        }

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
        SettingsEnableCopyAndFavoriteHotkey = settings.EnableCopyAndFavoriteHotkey;
        SettingsCopyAndFavoriteHotkey = settings.CopyAndFavoriteHotkey;
        SettingsEnableCopyAndSensitiveHotkey = settings.EnableCopyAndSensitiveHotkey;
        SettingsCopyAndSensitiveHotkey = settings.CopyAndSensitiveHotkey;
        SettingsEnableCopyWithoutSavingHotkey = settings.EnableCopyWithoutSavingHotkey;
        SettingsCopyWithoutSavingHotkey = settings.CopyWithoutSavingHotkey;
        SettingsEnablePasteAndDeleteHotkey = settings.EnablePasteAndDeleteHotkey;
        SettingsPasteAndDeleteHotkey = settings.PasteAndDeleteHotkey;
        SettingsEnablePasteAndFavoriteHotkey = settings.EnablePasteAndFavoriteHotkey;
        SettingsPasteAndFavoriteHotkey = settings.PasteAndFavoriteHotkey;
        SettingsEnablePasteAsPlainTextHotkey = settings.EnablePasteAsPlainTextHotkey;
        SettingsPasteAsPlainTextHotkey = settings.PasteAsPlainTextHotkey;
        SettingsExternalEditorPath = settings.ExternalEditorPath;
        SettingsExternalDiffToolPath = settings.ExternalDiffToolPath;
        SettingsEnableAi = settings.EnableAi;
        SettingsAiBaseUrl = settings.AiBaseUrl;
        SettingsAiApiKey = settings.AiApiKey;
        SettingsAiModel = settings.AiModel;
        SettingsAiImageModel = settings.AiImageModel;
        SettingsAiReasoningEffort = settings.AiReasoningEffort;
        SettingsEnableAutoUpdate = settings.EnableAutoUpdate;
        SettingsUpdateFeedUrl = settings.UpdateFeedUrl;
        SettingsOcrLanguages = settings.OcrLanguages;
        SettingsAutoOcrImageClips = settings.AutoOcrImageClips;
        SettingsEnableRemoteApi = settings.EnableRemoteApi;
        SettingsRemoteApiPort = settings.RemoteApiPort;
        SettingsRemoteApiToken = settings.RemoteApiToken;
        SettingsRemoteApiBindAddress = settings.RemoteApiBindAddress;
        SettingsUserScriptDrafts.Clear();
        foreach (var s in settings.UserScripts)
        {
            SettingsUserScriptDrafts.Add(new UserScriptDraft { Name = s.Name, Code = s.Code });
        }
        SelectedScriptDraft = SettingsUserScriptDrafts.FirstOrDefault();
        SettingsCustomHotkeyDrafts.Clear();
        foreach (var h in settings.CustomHotkeys)
        {
            SettingsCustomHotkeyDrafts.Add(CustomHotkeyDraft.From(h));
        }
        SelectedCustomHotkeyDraft = SettingsCustomHotkeyDrafts.FirstOrDefault();
        SettingsUseFuzzySearch = settings.UseFuzzySettingsSearch;
        UseFuzzyClipSearch = settings.UseFuzzyClipSearch;
        UseSemanticClipSearch = settings.UseSemanticClipSearch;
        IsDatabasePasswordVisible = false;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!IsSettingsOpen)
        {
            LoadSettingsDraft(settings);
        }

        SyncUserScripts(settings);
        SyncAiPresets(settings);
        UpdateSelectedClipPresentation();
        RaiseSelectionStateProperties();
        this.RaisePropertyChanged(nameof(IsCompareAvailable));
        this.RaisePropertyChanged(nameof(FavoritesFilterTooltip));
        this.RaisePropertyChanged(nameof(SensitiveFilterTooltip));
        this.RaisePropertyChanged(nameof(RegexFilterTooltip));
        this.RaisePropertyChanged(nameof(CaseSensitiveFilterTooltip));
        this.RaisePropertyChanged(nameof(WildcardFilterTooltip));
        this.RaisePropertyChanged(nameof(WholeWordFilterTooltip));
        this.RaisePropertyChanged(nameof(PastedFilterTooltip));
    }

    private void SyncUserScripts(AppSettings settings)
    {
        UserScripts.Clear();
        foreach (var s in settings.UserScripts)
        {
            UserScripts.Add(s);
        }
    }

    private void SyncAiPresets(AppSettings settings)
    {
        AiPresets.Clear();
        foreach (var p in settings.AiPresets)
        {
            AiPresets.Add(p);
        }

        AiMenuEntries.Clear();
        AiMenuEntries.Add(new AiMenuEntry("Custom prompt…", null, true, AiPresetKind.TextToText));
        foreach (var p in settings.AiPresets)
        {
            var label = p.Kind switch
            {
                AiPresetKind.ImageToText => $"🖼→📝 {p.Name}",
                AiPresetKind.ImageToImage => $"🖼→🖼 {p.Name}",
                _ => p.Name,
            };
            AiMenuEntries.Add(new AiMenuEntry(label, p, false, p.Kind));
        }
    }

    private async Task InvokeAiMenuEntryAsync(AiMenuEntry? entry)
    {
        if (entry is null) return;
        if (entry.IsCustomPrompt)
        {
            OpenAiPrompt();
            return;
        }
        if (entry.Preset is { } preset)
        {
            await ApplyAiPresetAsync(preset).ConfigureAwait(false);
        }
    }

    private async Task StartDatabaseAsync()
    {
        await _databaseInitializer.InitializeAsync();
        if (!_settingsService.HasSavedSettings)
        {
            await _settingsService.InitializeAsync();
        }

        await EnsureDefaultScriptsLoadedAsync();

        await LoadSensitivityRulesAsync();
        _isDatabaseReady = true;
        _clipboardMonitorService.Start();
        _backgroundOcrQueue.Start();
        _ = Task.Run(() => _backgroundOcrQueue.EnqueueBacklogAsync());
        StartMaintenanceLoop();
        _ = RefreshSemanticCoverageAsync();
        if (_embeddingWorker is not null)
        {
            _subscriptions.Add(_embeddingWorker.BatchCompleted
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe((int count) => { _ = RefreshSemanticCoverageAsync(); }));
        }
    }

    private async Task EnsureDefaultScriptsLoadedAsync()
    {
        try
        {
            var current = _settingsService.Current;
            AppSettings next = current;
            bool changed = false;

            if (current.UserScripts?.Count == 0)
            {
                var defaults = ScriptingService.GetDefaultScripts().ToList();
                if (defaults.Count > 0)
                {
                    next = next with { UserScripts = defaults };
                    changed = true;
                }
            }

            if (current.AiPresets?.Count == 0)
            {
                var presets = new List<AiPreset>
                {
                    new() { Name = "Fix grammar & spelling", Prompt = "Fix any grammar or spelling mistakes in the text. Preserve the original tone, formatting, and meaning. Return only the corrected text." },
                    new() { Name = "Rewrite formally", Prompt = "Rewrite the following text in a formal, professional tone suitable for business communication. Keep the meaning unchanged." },
                    new() { Name = "Summarize", Prompt = "Summarize the following text into 2-4 concise bullet points that capture the main ideas." },
                    new() { Name = "Explain simply", Prompt = "Explain the following text in plain language a non-expert could understand, without losing key details." },
                    new() { Name = "Translate to English", Prompt = "Translate the following text to natural, fluent English. Preserve meaning, tone, and formatting. If already in English, return it unchanged." },
                    new() { Name = "Format JSON", Prompt = "If the input contains JSON, return it pretty-printed with 2-space indentation and stable key ordering. Otherwise return it unchanged." },
                };
                presets.AddRange(GetDefaultImagePresets());
                next = next with { AiPresets = presets };
                changed = true;
            }
            else if (current.AiPresets is { Count: > 0 } existing
                     && !existing.Any(p => p.Kind != AiPresetKind.TextToText))
            {
                var merged = existing.Concat(GetDefaultImagePresets()).ToList();
                next = next with { AiPresets = merged };
                changed = true;
            }

            if (changed)
            {
                await _settingsService.SaveAsync(next);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to seed defaults: {ex.Message}");
        }
    }

    private static IEnumerable<AiPreset> GetDefaultImagePresets() => new[]
    {
        new AiPreset { Name = "Describe image", Prompt = "Describe this image in a detailed paragraph. Mention notable objects, people, text, colors, and overall mood.", Kind = AiPresetKind.ImageToText },
        new AiPreset { Name = "Extract text (AI OCR)", Prompt = "Extract all visible text from this image verbatim. Preserve original line breaks. Return only the text, no commentary.", Kind = AiPresetKind.ImageToText },
        new AiPreset { Name = "Summarize screenshot", Prompt = "This is a screenshot. Summarize what the user is seeing in 3-5 bullet points: app/context, main content, notable UI state.", Kind = AiPresetKind.ImageToText },
        new AiPreset { Name = "Identify objects", Prompt = "List the distinct objects, people, or UI elements visible in this image. One per line.", Kind = AiPresetKind.ImageToText },
        new AiPreset { Name = "Remove background", Prompt = "Remove the background, keeping only the main subject. Output a clean cut-out on a transparent background.", Kind = AiPresetKind.ImageToImage },
        new AiPreset { Name = "Colorize", Prompt = "Colorize this image with realistic, natural colors while preserving all details.", Kind = AiPresetKind.ImageToImage },
        new AiPreset { Name = "Turn into sketch", Prompt = "Convert this image into a clean black-and-white pencil sketch.", Kind = AiPresetKind.ImageToImage },
        new AiPreset { Name = "Enhance / denoise", Prompt = "Enhance this image: denoise, sharpen details, improve lighting and contrast. Do not alter content or composition.", Kind = AiPresetKind.ImageToImage },
    };

    private async Task ApplyAiPresetAsync(AiPreset? preset)
    {
        if (preset is null || string.IsNullOrWhiteSpace(preset.Prompt))
        {
            return;
        }
        switch (preset.Kind)
        {
            case AiPresetKind.ImageToText:
                await RunImagePresetAsync(preset, toImage: false).ConfigureAwait(false);
                return;
            case AiPresetKind.ImageToImage:
                await RunImagePresetAsync(preset, toImage: true).ConfigureAwait(false);
                return;
            default:
                AiPromptInput = preset.Prompt;
                AiPromptError = string.Empty;
                await SubmitAiPromptAsync(transformKind: $"ai:{preset.Name}", presetLabel: preset.Name);
                return;
        }
    }

    private async Task RunImagePresetAsync(AiPreset preset, bool toImage)
    {
        if (!_aiTransformService.IsConfigured)
        {
            StatusText = "AI is not configured. Enable it in Settings → AI and set an API key (or OPENAI_API_KEY env var).";
            return;
        }

        var clip = GetEffectiveSelectedClip();
        if (clip is null || clip.Clip.ContentType != ContentType.Image || clip.Clip.ContentBytes is not { Length: > 0 } imageBytes)
        {
            _notificationService.PublishWarning(
                $"AI preset '{preset.Name}'",
                "Select an image clip first.");
            StatusText = "AI image preset needs an image clip selected.";
            return;
        }

        var mediaType = ResolveImageMediaType(clip.Clip.ContentFormat);
        var label = $"AI: {preset.Name}";
        StatusText = $"Running AI preset '{preset.Name}'…";

        _ = _jobIndicator.TrackAsync(label, async () =>
        {
            try
            {
                if (toImage)
                {
                    var result = await _aiTransformService.EditImageAsync(preset.Prompt, imageBytes, mediaType).ConfigureAwait(false);
                    if (result is not { Length: > 0 })
                    {
                        StatusText = "AI image edit returned no data.";
                        return;
                    }
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await CopyEditedImageAsync(result));
                }
                else
                {
                    var text = await _aiTransformService.DescribeImageAsync(preset.Prompt, imageBytes, mediaType).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        StatusText = "AI image description was empty.";
                        return;
                    }

                    var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
                    var captured = await _clipStoreService.CaptureAsync(new ClipCaptureRequest
                    {
                        ContentBytes = textBytes,
                        ContentText = text,
                        ContentType = ContentType.Text,
                        ContentFormat = ClipContentFormat.PlainText,
                        SourceApp = clip.SourceApp,
                        SourceAppPath = clip.Clip.SourceAppPath,
                        SourceAppIconBytes = clip.Clip.SourceAppIconBytes,
                        SourceWindowTitle = clip.Clip.SourceWindowTitle,
                        IncrementExistingCopyCount = false,
                        SourceClipId = clip.Clip.Id,
                        TransformKind = $"ai:{preset.Name}",
                    });

                    StatusText = $"AI preset '{preset.Name}' produced a new clip.";
                    await RefreshAsync(captured?.Id);
                }
            }
            catch (Exception ex)
            {
                _notificationService.PublishError($"AI preset '{preset.Name}' failed", ex.Message);
                StatusText = ex.Message;
            }
        });
    }

    private static string ResolveImageMediaType(ClipContentFormat format) => "image/png";

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
        maintenanceSubscription.Disposable = Observable.Interval(TimeSpan.FromMinutes(1), RxSchedulers.TaskpoolScheduler)
            .SelectMany(_ => Observable.FromAsync(() => ApplyMaintenanceAndRefreshAsync(false)))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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
        this.RaisePropertyChanged(nameof(ShowSelectedImagePreview));
        this.RaisePropertyChanged(nameof(ShowSelectedImageEditor));
        this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
        this.RaisePropertyChanged(nameof(ShowSelectedImageOcrText));
        this.RaisePropertyChanged(nameof(IsImagePreviewMode));
        this.RaisePropertyChanged(nameof(IsImageEditorMode));
        this.RaisePropertyChanged(nameof(IsImageTextMode));
        this.RaisePropertyChanged(nameof(HasSelectedClipOcrText));
        this.RaisePropertyChanged(nameof(IsSelectedClipImageOcrRunning));
        this.RaisePropertyChanged(nameof(IsSelectedClipImageOcrPending));
        this.RaisePropertyChanged(nameof(IsSelectedClipImageOcrFailed));
        this.RaisePropertyChanged(nameof(SelectedClipOcrText));
        this.RaisePropertyChanged(nameof(SelectedClipOcrStatusText));
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

    private async void PersistImageViewModeInBackground(ImageViewMode mode)
    {
        try
        {
            if (!_settingsService.HasSavedSettings)
            {
                return;
            }

            if (_settingsService.Current.LastImageViewMode == mode)
            {
                return;
            }

            await _settingsService.SaveAsync(_settingsService.Current with { LastImageViewMode = mode });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to persist image view mode: {ex.Message}");
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
        _checkedClipCount = 0;
        _checkedTransformableClipCount = 0;
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
        var item = new ClipItemViewModel(
            clip,
            CopyClipAsync,
            ToggleFavoriteClipAsync,
            DeleteClipAsync,
            ExportClipAsync,
            TogglePinClipAsync,
            ApplyTransformationToSingleClipAsync)
        {
            IsChecked = checkedIds?.Contains(clip.Id) == true
        };
        item.PropertyChanged += OnClipItemPropertyChanged;
        if (item.IsChecked)
        {
            _checkedClipCount++;
            if (item.CanTransform) _checkedTransformableClipCount++;
        }
        return item;
    }

    private void DetachClip(ClipItemViewModel clip)
    {
        clip.PropertyChanged -= OnClipItemPropertyChanged;
        if (clip.IsChecked)
        {
            _checkedClipCount = Math.Max(0, _checkedClipCount - 1);
            if (clip.CanTransform)
            {
                _checkedTransformableClipCount = Math.Max(0, _checkedTransformableClipCount - 1);
            }
        }
    }

    private void OnClipItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipItemViewModel.IsChecked))
        {
            if (sender is ClipItemViewModel clip)
            {
                if (clip.IsChecked)
                {
                    _checkedClipCount++;
                    if (clip.CanTransform) _checkedTransformableClipCount++;
                }
                else
                {
                    _checkedClipCount = Math.Max(0, _checkedClipCount - 1);
                    if (clip.CanTransform)
                    {
                        _checkedTransformableClipCount = Math.Max(0, _checkedTransformableClipCount - 1);
                    }
                }
            }
            RaiseBulkSelectionProperties();
        }
    }

    private void RaiseBulkSelectionProperties()
    {
        this.RaisePropertyChanged(nameof(HasCheckedClips));
        this.RaisePropertyChanged(nameof(CheckedClipCount));
        this.RaisePropertyChanged(nameof(CheckedClipSummaryText));
        this.RaisePropertyChanged(nameof(HasCheckedOrSelectedClip));
        this.RaisePropertyChanged(nameof(HasTransformableTarget));
    }

    private void RaiseEditedClipProperties()
    {
        this.RaisePropertyChanged(nameof(ShowCopyEditedClipButton));
        this.RaisePropertyChanged(nameof(HasEditedClipChanges));
    }

    private void SyncEditedClipText()
    {
        _editedClipBaseline = GetEditedClipBaseline();
        EditedClipSelectionStart = 0;
        EditedClipSelectionLength = 0;
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
            var removal = Observable.Timer(TimeSpan.FromSeconds(6), RxSchedulers.MainThreadScheduler)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
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
