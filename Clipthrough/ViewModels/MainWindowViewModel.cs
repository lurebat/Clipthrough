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
using System.Reflection;
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
    private readonly IDragDropService _dragDropService;
    private readonly IImageEditorService _imageEditorService;
    private readonly ISearchHistoryService _searchHistoryService;
    private readonly IAiTransformService _aiTransformService;
    private readonly IScriptingService _scriptingService;
    private readonly IUpdateService _updateService;
    private readonly IOcrService _ocrService;
    private readonly IBackgroundOcrQueue _backgroundOcrQueue;
    private readonly IBackgroundJobIndicator _jobIndicator;
    private readonly Clipthrough.Services.Search.ISemanticSearchService? _semanticSearchService;
    private readonly Clipthrough.Services.Search.IEmbeddingWorker? _embeddingWorker;
    private readonly ICopilotAuthService? _copilotAuthService;
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly IDatabaseBackupService _databaseBackupService;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly Dictionary<long, (ClipEntry Clip, CancellationTokenSource Cts)> _pendingDeletes = new();
    private readonly object _refreshQueueLock = new();

    private DateTimeOffset? _lastCapturedAtRaw;
    private bool _suppressEditAutoSave;
    private string _searchText = string.Empty;
    private ContentTypeOption _selectedContentTypeOption = new(null);
    private readonly System.Collections.Generic.HashSet<ContentType> _selectedContentTypes = new();
    private bool _showFavoritesOnly;
    private bool _showSensitiveOnly;
    private bool _useRegexSearch;
    private bool _caseSensitiveSearch;
    private bool _useWildcardSearch;
    private bool _wholeWordSearch;
    private bool _showPastedOnly;
    private ClipSortOptionItem _selectedSortOption = null!;
    private ClipItemViewModel? _selectedClip;
    private int _checkedClipCount;
    private int _checkedTransformableClipCount;
    private ClipFileItemViewModel? _selectedFileItem;
    private bool _hasMoreResults;
    private bool _isBusy;
    private bool _isCapturing;
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
    private string _selectedClipRawContent = AppText.PreviewSelectRawContent;
    private string _selectedClipImageHint = AppText.PreviewSelectImage;
    private bool _isStartupInProgress;
    private bool _isDatabaseReady;
    private bool _isStarted;
    private bool _isLoadingDatabase;
    private string _startupErrorTitle = string.Empty;
    private string _startupErrorMessage = string.Empty;
    private string _integrityCheckStatus = string.Empty;
    private bool _areBackgroundServicesStarted;
    private bool _hasQueuedRefresh;
    // Tracks whether the main window is currently visible. When false, optimistic
    // clip-list mutations and the throttled background refresh are skipped to
    // keep the UI thread idle so the popup snaps open quickly on the next show.
    // See ApplyCapturedClipOptimistically / ApplyUpdatedClipOptimistically / PerformRefreshAsync.
    private bool _isMainWindowVisible;
    // Set true whenever a clip change (capture/update/OCR/maintenance) is observed
    // while the window is hidden, or when a refresh result was discarded because
    // the window became hidden mid-apply. On the next show we trigger one refresh.
    private bool _isClipListStale;
    // When the popup is reopened, mirror the typical clipboard-manager UX of
    // always highlighting the newest captured clip rather than preserving the
    // previous selection. Set by SetMainWindowVisible(true) and consumed by
    // ApplyRefreshResult.
    private bool _selectNewestOnNextRefresh;
    private int _recentSearchNavigationIndex = -1;
    private bool _isNavigatingSearchHistory;
    private bool _isSearchBoxFocused;
    private bool _isSettingsOpen;
    private bool _isWelcomeOpen;
    private bool _isPasswordPromptOpen;
    private bool _isAiPromptOpen;
    private AiPresetKind _aiPromptKind = AiPresetKind.TextToText;
    private string _aiPromptInput = string.Empty;
    private string _aiPromptError = string.Empty;
    private bool _isAiPromptBusy;
    private string _passwordPromptInput = string.Empty;
    private string _passwordPromptError = string.Empty;
    private bool _isPasswordPromptPasswordVisible;
    private bool _isDatabasePasswordVisible;
    private bool _settingsRememberDatabasePassword;
    private Task _queuedRefreshTask = Task.CompletedTask;
    private long? _queuedRefreshPreferredSelectionId;
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
    private string _settingsDatabasePasswordConfirm = StorageOptions.Default.DatabasePassword;
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
    private string _settingsToggleFuzzyHotkey = AppSettings.Default.ToggleFuzzyHotkey;
    private bool _settingsEnableToggleFuzzyHotkey = AppSettings.Default.EnableToggleFuzzyHotkey;
    private string _settingsToggleSemanticHotkey = AppSettings.Default.ToggleSemanticHotkey;
    private bool _settingsEnableToggleSemanticHotkey = AppSettings.Default.EnableToggleSemanticHotkey;
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
    private string _settingsExternalImageEditorPath = AppSettings.Default.ExternalImageEditorPath;
    private string _settingsExternalDiffToolPath = AppSettings.Default.ExternalDiffToolPath;
    private bool _settingsEnableAi = AppSettings.Default.EnableAi;
    private string _settingsAiBaseUrl = AppSettings.Default.AiBaseUrl;
    private string _settingsAiApiKey = AppSettings.Default.AiApiKey;
    private string _settingsAiModel = AppSettings.Default.AiModel;
    private string _settingsAiReasoningEffort = AppSettings.Default.AiReasoningEffort;
    private bool _settingsEnableAutoUpdate = AppSettings.Default.EnableAutoUpdate;
    private bool _settingsAutoApplyUpdatesOnStartup = AppSettings.Default.AutoApplyUpdatesOnStartup;
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

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService, IClipSampleDataService clipSampleDataService, ISettingsService settingsService, ISystemInteractionService systemInteractionService, IStorageOptionsService storageOptionsService, ISensitivityService sensitivityService, IAppNotificationService notificationService, ISessionLogService sessionLogService, IClipExportService clipExportService, IImageEditorService imageEditorService, ISearchHistoryService searchHistoryService, IAiTransformService aiTransformService, IScriptingService scriptingService, IOcrService ocrService, IBackgroundOcrQueue backgroundOcrQueue, IBackgroundJobIndicator jobIndicator, DatabaseInitializer databaseInitializer, IClipAngelImportService? clipAngelImportService = null, Clipthrough.Services.Search.ISemanticSearchService? semanticSearchService = null, Clipthrough.Services.Search.IEmbeddingWorker? embeddingWorker = null, ICopilotAuthService? copilotAuthService = null, IUpdateService? updateService = null, IDatabaseBackupService? databaseBackupService = null, IDragDropService? dragDropService = null)
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
        _dragDropService = dragDropService ?? new DragDropService();
        _imageEditorService = imageEditorService;
        _searchHistoryService = searchHistoryService;
        _aiTransformService = aiTransformService;
        _scriptingService = scriptingService;
        _updateService = updateService ?? new UpdateService(settingsService);
        _ocrService = ocrService;
        _backgroundOcrQueue = backgroundOcrQueue;
        _jobIndicator = jobIndicator;
        _jobIndicator.Changed += OnJobIndicatorChanged;
        _semanticSearchService = semanticSearchService;
        _embeddingWorker = embeddingWorker;
        _copilotAuthService = copilotAuthService;
        if (_copilotAuthService is not null)
        {
            _copilotAuthService.SignedInChanged += OnCopilotSignedInChanged;
        }
        _databaseInitializer = databaseInitializer;
        _databaseBackupService = databaseBackupService ?? new DatabaseBackupService(storageOptionsService, null, null, null);
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
        SortOptions =
        [
            new ClipSortOptionItem(ClipSortOption.MostRecent),
            new ClipSortOptionItem(ClipSortOption.OldestFirst),
            new ClipSortOptionItem(ClipSortOption.MostPasted),
            new ClipSortOptionItem(ClipSortOption.Alphabetical),
            new ClipSortOptionItem(ClipSortOption.LargestFirst),
            new ClipSortOptionItem(ClipSortOption.BestMatching),
        ];
        _selectedSortOption = SortOptions[0];
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync, this.WhenAnyValue(x => x.HasMoreResults, x => x.IsBusy, static (hasMore, isBusy) => hasMore && !isBusy));

        var hasSelection = this.WhenAnyValue(x => x.SelectedClip).Select(static clip => clip is not null);
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync, hasSelection);
        TogglePinCommand = ReactiveCommand.CreateFromTask(TogglePinAsync, hasSelection);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasSelection);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);
        PasteSelectedCommand = ReactiveCommand.CreateFromTask(PasteSelectedAsync, hasSelection);
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
        OpenAboutCommand = ReactiveCommand.Create(OpenAbout);
        CheckForUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdateAsync);
        OpenLogsFolderCommand = ReactiveCommand.CreateFromTask(OpenLogsFolderAsync);
        OpenDatabaseFolderCommand = ReactiveCommand.CreateFromTask(OpenDatabaseFolderAsync);
        RunIntegrityCheckCommand = ReactiveCommand.CreateFromTask(RunIntegrityCheckAsync);
        RefreshBackupsCommand = ReactiveCommand.Create(RefreshBackups);
        RestoreBackupCommand = ReactiveCommand.CreateFromTask<Window?>(RestoreBackupAsync);
        CloseSettingsCommand = ReactiveCommand.Create(CloseSettings);
        SaveSettingsCommand = ReactiveCommand.CreateFromTask(SaveSettingsAsync);
        BrowseDatabasePathCommand = ReactiveCommand.CreateFromTask<Window?>(BrowseDatabasePathAsync);
        ImportClipAngelCommand = ReactiveCommand.CreateFromTask<Window?>(ImportClipAngelAsync);
        UnlockDatabaseCommand = ReactiveCommand.CreateFromTask(UnlockDatabaseAsync);
        ExitApplicationCommand = ReactiveCommand.Create(ExitApplication);
        OpenAiPromptCommand = ReactiveCommand.Create(OpenAiPrompt);
        CopilotSignInCommand = ReactiveCommand.CreateFromTask(CopilotSignInAsync);
        CopilotSignOutCommand = ReactiveCommand.Create(CopilotSignOut);
        CopyCopilotUserCodeCommand = ReactiveCommand.CreateFromTask(CopyCopilotUserCodeAsync);
        SubmitAiPromptCommand = ReactiveCommand.CreateFromTask(SubmitAiPromptAsync);
        CancelAiPromptCommand = ReactiveCommand.Create(CancelAiPrompt);
        ApplyAiPresetCommand = ReactiveCommand.CreateFromTask<AiPreset>(ApplyAiPresetAsync);
        InvokeAiMenuEntryCommand = ReactiveCommand.CreateFromTask<AiMenuEntry>(InvokeAiMenuEntryAsync);
        AddScriptDraftCommand = ReactiveCommand.Create(() =>
        {
            var draft = new UserScriptDraft { Name = "New script", Code = "return input;" };
            SettingsUserScriptDrafts.Add(draft);
            SelectedScriptDraft = draft;
            RebuildCustomHotkeyTargetSuggestions();
        });
        RemoveScriptDraftCommand = ReactiveCommand.Create<UserScriptDraft>(draft =>
        {
            if (draft is null) return;
            SettingsUserScriptDrafts.Remove(draft);
            SelectedScriptDraft = SettingsUserScriptDrafts.FirstOrDefault();
            RebuildCustomHotkeyTargetSuggestions();
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

        CheckForUpdatesNowCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesNowAsync);
        RestartAndInstallUpdateCommand = ReactiveCommand.Create(RestartAndInstallUpdate);

        _settingsService.SettingsChanged += OnSettingsChanged;
        SyncUserScripts(_settingsService.Current);

        ApplyPersistedFilters(_settingsService.Current, notify: false);

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
                    x => x.UseFuzzyClipSearch,
                    x => x.UseSemanticClipSearch,
                    x => x.SelectedContentTypeOption,
                    (_, _, _, _, _, _, _, _, _, _) => Unit.Default)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(500), RxSchedulers.MainThreadScheduler)
                .Subscribe(async _ =>
                {
                    try
                    {
                        await PersistCurrentFilterStateAsync();
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning($"Filter state save failed: {ex.Message}");
                    }
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
                .Subscribe(ApplyCapturedClipOptimistically, ex => ReportError("Captured-clip subscription", ex)));

        _subscriptions.Add(
            _clipboardMonitorService.UpdatedClips
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ApplyUpdatedClipOptimistically, ex => ReportError("Updated-clip subscription", ex)));

        _subscriptions.Add(
            _clipboardMonitorService.UpdatedClips
                .Throttle(TimeSpan.FromMilliseconds(250), RxSchedulers.MainThreadScheduler)
                .SelectMany(clip =>
                {
                    if (!_isMainWindowVisible)
                    {
                        // Hidden — mark the list stale; the next show will pick
                        // up the changes via a single authoritative refresh.
                        _isClipListStale = true;
                        return Observable.Return(System.Reactive.Unit.Default);
                    }
                    return Observable.FromAsync(() => RefreshAsync())
                        .Select(_ => System.Reactive.Unit.Default);
                })
                .Subscribe(_ => { }, ex => ReportError("Throttled clip refresh", ex)));

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
                .Throttle(TimeSpan.FromMilliseconds(250), RxSchedulers.MainThreadScheduler)
                .SelectMany(_ =>
                {
                    if (!_isMainWindowVisible)
                    {
                        _isClipListStale = true;
                        return Observable.Return(System.Reactive.Unit.Default);
                    }
                    return Observable.FromAsync(() => RefreshAsync())
                        .Select(_ => System.Reactive.Unit.Default);
                })
                .Subscribe(_ => { }, ex => Trace.TraceError($"OCR refresh failed: {ex}")));

        _subscriptions.Add(
            _notificationService.Notifications
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ShowNotification));

        // Show the capture indicator only when a capture is slow enough to
        // matter. Throttle leading-edge false→true transitions by 200 ms so
        // ordinary fast captures don't flash the UI; once shown, keep it
        // visible for at least 400 ms so the user can see it.
        _subscriptions.Add(
            _clipboardMonitorService.CaptureBusy
                .DistinctUntilChanged()
                .Select(busy => busy
                    ? Observable.Return(true).Delay(TimeSpan.FromMilliseconds(200), RxSchedulers.MainThreadScheduler)
                    : Observable.Return(false).Delay(TimeSpan.FromMilliseconds(150), RxSchedulers.MainThreadScheduler))
                .Switch()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(busy => IsCapturing = busy,
                    ex => Trace.TraceError($"Capture-busy subscription failed: {ex}")));

        _subscriptions.Add(
            Observable.Interval(TimeSpan.FromSeconds(10), RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RefreshLastCaptureSummary()));

        _subscriptions.Add(
            RefreshCommand.ThrownExceptions
                .Merge(LoadMoreCommand.ThrownExceptions)
                .Merge(ToggleFavoriteCommand.ThrownExceptions)
                .Merge(TogglePinCommand.ThrownExceptions)
                .Merge(CopySelectedCommand.ThrownExceptions)
                .Merge(PasteSelectedCommand.ThrownExceptions)
                .Merge(ExportSelectedCommand.ThrownExceptions)
                .Merge(OpenInEditorCommand.ThrownExceptions)
                .Merge(CompareClipsCommand.ThrownExceptions)
                .Merge(EditSelectedImageCommand.ThrownExceptions)
                .Merge(DeleteSelectedCommand.ThrownExceptions)
                .Merge(FavoriteCheckedClipsCommand.ThrownExceptions)
                .Merge(PinCheckedClipsCommand.ThrownExceptions)
                .Merge(DeleteCheckedClipsCommand.ThrownExceptions)
                .Merge(CopyEditedClipCommand.ThrownExceptions)
                .Merge(ApplyTextTransformationCommand.ThrownExceptions)
                .Merge(CheckForUpdateCommand.ThrownExceptions)
                .Merge(SaveSettingsCommand.ThrownExceptions)
                .Merge(BrowseDatabasePathCommand.ThrownExceptions)
                .Merge(UnlockDatabaseCommand.ThrownExceptions)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ex => ReportError("Command", ex)));
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public ObservableCollection<string> RecentSearches { get; } = [];

    public ObservableCollection<string> FilteredRecentSearches { get; } = [];

    public bool IsSearchSuggestionsOpen => _isSearchBoxFocused && FilteredRecentSearches.Count > 0;

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

    public ReactiveCommand<Unit, Unit> PasteSelectedCommand { get; }

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

    public ReactiveCommand<Unit, Unit> RunOcrOnSelectedImageCommand { get; }

    public ReactiveCommand<Unit, Unit> RerunAllEmbeddingsCommand { get; }

    public ReactiveCommand<Unit, Unit> RefreshSemanticCoverageCommand { get; }

    public ReactiveCommand<Unit, string> GenerateRemoteApiTokenCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyRemoteApiTokenCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRemoteApiDocsUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRemoteApiSchemaUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenRemoteApiDocsUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenRemoteApiSchemaUrlCommand { get; }

    /// <summary>
    /// User-initiated update check. Surfaces "checking…", success, "no update",
    /// or error in <see cref="StatusText"/>; if an update is found and
    /// downloaded, publishes the same notification the background check uses.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CheckForUpdatesNowCommand { get; }

    /// <summary>
    /// User-initiated "restart now to install the downloaded update". No-op
    /// when nothing is pending.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RestartAndInstallUpdateCommand { get; }

    public ObservableCollection<UserScript> UserScripts { get; } = new();

    public ObservableCollection<UserScript> VisibleUserScripts { get; } = new();

    public ReactiveCommand<Unit, Unit> AddSensitivityRuleCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenAboutCommand { get; }

    public ReactiveCommand<Unit, Unit> CheckForUpdateCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenLogsFolderCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenDatabaseFolderCommand { get; }

    public ReactiveCommand<Unit, Unit> RunIntegrityCheckCommand { get; }

    public ReactiveCommand<Unit, Unit> RefreshBackupsCommand { get; }

    public ReactiveCommand<Window?, Unit> RestoreBackupCommand { get; }

    public ObservableCollection<DatabaseBackupItem> Backups { get; } = new();

    private DatabaseBackupItem? _selectedBackup;
    public DatabaseBackupItem? SelectedBackup
    {
        get => _selectedBackup;
        set => this.RaiseAndSetIfChanged(ref _selectedBackup, value);
    }

    private string _backupRestoreStatus = string.Empty;
    public string BackupRestoreStatus
    {
        get => _backupRestoreStatus;
        private set => this.RaiseAndSetIfChanged(ref _backupRestoreStatus, value);
    }

    public ReactiveCommand<Unit, Unit> CloseSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }

    public ReactiveCommand<Window?, Unit> BrowseDatabasePathCommand { get; }
    public ReactiveCommand<Window?, Unit> ImportClipAngelCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToTopCommand { get; }

    public ReactiveCommand<Unit, Unit> UnlockDatabaseCommand { get; }

    public ReactiveCommand<Unit, Unit> ExitApplicationCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenAiPromptCommand { get; }

    public ReactiveCommand<Unit, Unit> CopilotSignInCommand { get; }

    public ReactiveCommand<Unit, Unit> CopilotSignOutCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyCopilotUserCodeCommand { get; }

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

    private List<string> _customHotkeyTargetSuggestions = new();
    public List<string> CustomHotkeyTargetSuggestions
    {
        get => _customHotkeyTargetSuggestions;
        private set => this.RaiseAndSetIfChanged(ref _customHotkeyTargetSuggestions, value);
    }

    public System.Collections.ObjectModel.ObservableCollection<AiMenuEntry> AiMenuEntries { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<AiMenuEntry> VisibleAiMenuEntries { get; } = new();

    public bool IsAiMenuVisible => _aiTransformService.IsConfigured && VisibleAiMenuEntries.Count > 0;

    public bool HasVisibleUserScripts => VisibleUserScripts.Count > 0;

    public ReactiveCommand<AiMenuEntry, Unit> InvokeAiMenuEntryCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            if (!_isNavigatingSearchHistory)
            {
                _recentSearchNavigationIndex = -1;
            }
            RefreshFilteredRecentSearches();
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

    public ClipSortOptionItem SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSortOption, value);
            _ = RefreshAsync();
        }
    }

    public IReadOnlyList<ClipSortOptionItem> SortOptions { get; }

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
            value = NormalizeContentDisplayMode(value);
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

    public void CycleSelectedViewerMode(bool reverse = false)
    {
        if (ShowSelectedImageRenderer)
        {
            var modes = new[]
            {
                ImageViewMode.Preview,
                ImageViewMode.Editor,
                ImageViewMode.Text,
            };
            var index = Array.IndexOf(modes, _imageViewMode);
            if (index < 0)
            {
                index = 0;
            }

            SelectedImageViewMode = modes[(index + (reverse ? modes.Length - 1 : 1)) % modes.Length];
            return;
        }

        if (!IsDisplayModeApplicable)
        {
            return;
        }

        var index2 = Array.IndexOf(DisplayModeOptions, _contentDisplayMode);
        if (index2 < 0)
        {
            index2 = 0;
        }

        SelectedContentDisplayMode = DisplayModeOptions[(index2 + (reverse ? DisplayModeOptions.Length - 1 : 1)) % DisplayModeOptions.Length];
    }

    public bool TrySelectContentTypeByShortcut(int shortcutNumber)
    {
        // 1 = All (clear set); 2 = Text, 3 = Image, 4 = RichText, 5 = Files.
        switch (shortcutNumber)
        {
            case 1: IsAllTypeSelected = true; return true;
            case 2: IsTextTypeSelected = !IsTextTypeSelected; return true;
            case 3: IsImageTypeSelected = !IsImageTypeSelected; return true;
            case 4: IsRichTextTypeSelected = !IsRichTextTypeSelected; return true;
            case 5: IsFilesTypeSelected = !IsFilesTypeSelected; return true;
            default: return false;
        }
    }

    public bool IsAllTypeSelected
    {
        get => _selectedContentTypes.Count == 0;
        set
        {
            if (value && _selectedContentTypes.Count > 0)
            {
                _selectedContentTypes.Clear();
                OnContentTypeSelectionChanged();
            }
            else if (!value && _selectedContentTypes.Count == 0)
            {
                // User clicked "All" while it was already on — treat as a
                // selection of every type by leaving the empty set (which
                // already means "all"). Trigger a UI refresh so the toggle
                // doesn't appear stuck unchecked.
                this.RaisePropertyChanged(nameof(IsAllTypeSelected));
            }
        }
    }

    public bool IsTextTypeSelected
    {
        get => _selectedContentTypes.Contains(ContentType.Text);
        set => ToggleContentType(ContentType.Text, value);
    }

    public bool IsImageTypeSelected
    {
        get => _selectedContentTypes.Contains(ContentType.Image);
        set => ToggleContentType(ContentType.Image, value);
    }

    public bool IsRichTextTypeSelected
    {
        get => _selectedContentTypes.Contains(ContentType.RichText);
        set => ToggleContentType(ContentType.RichText, value);
    }

    public bool IsFilesTypeSelected
    {
        get => _selectedContentTypes.Contains(ContentType.Files);
        set => ToggleContentType(ContentType.Files, value);
    }

    private void ToggleContentType(ContentType type, bool include)
    {
        var changed = include
            ? _selectedContentTypes.Add(type)
            : _selectedContentTypes.Remove(type);
        if (changed)
        {
            OnContentTypeSelectionChanged();
        }
    }

    private void OnContentTypeSelectionChanged()
    {
        // Keep the legacy single-value option in sync so any remaining
        // consumer (e.g. ActiveFilterSummary, persisted-state save) reads
        // something sensible.
        _selectedContentTypeOption = _selectedContentTypes.Count == 1
            ? ContentTypeOptions.FirstOrDefault(o => o.Value == _selectedContentTypes.First()) ?? ContentTypeOptions[0]
            : ContentTypeOptions[0];
        this.RaisePropertyChanged(nameof(SelectedContentTypeOption));
        RaiseContentTypeToggleProperties();
        RaiseFilterStateProperties();
    }

    public bool HasCheckedOrSelectedClip => HasCheckedClips || HasSelectedClip;

    public bool HasTransformableTarget
    {
        get
        {
            if (_checkedClipCount > 0)
            {
                if (_checkedTransformableClipCount > 0)
                {
                    return true;
                }
                // No text targets among the checked clips, but image clips
                // are also "transformable" via the AI submenu.
                return _aiTransformService.IsConfigured && GetCheckedOrSelectedClips().Any(static clip =>
                    clip.IsImageClip && clip.Clip.ContentBytes is { Length: > 0 });
            }
            return SelectedClip?.CanTransform == true
                   || (_aiTransformService.IsConfigured
                       && SelectedClip?.IsImageClip == true
                       && SelectedClip.Clip.ContentBytes is { Length: > 0 });
        }
    }

    public bool HasTextTransformTarget => GetCheckedOrSelectedClips().Any(static clip => clip.CanTransform);

    public bool HasImageTransformTarget => _aiTransformService.IsConfigured && GetCheckedOrSelectedClips().Any(static clip =>
        clip.IsImageClip && clip.Clip.ContentBytes is { Length: > 0 });

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
            // Selection drives which AI entries / user scripts are eligible
            // (text vs. image). Without this the visible-entries collection
            // stays stuck on whatever the previous selection allowed.
            RefreshVisibleTransformMenus();
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
            this.RaisePropertyChanged(nameof(IsBusyOrCapturing));
        }
    }

    /// <summary>
    /// True while a clipboard capture is being processed (COM read +
    /// DB write + enrichment). Driven by the clipboard monitor service and
    /// debounced so it only flips visible for captures slower than a few
    /// hundred milliseconds — short captures don't flash the indicator.
    /// </summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCapturing, value);
            this.RaisePropertyChanged(nameof(IsBusyOrCapturing));
        }
    }

    public bool IsBusyOrCapturing => IsBusy || IsCapturing;

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public bool IsLoadingDatabase
    {
        get => _isLoadingDatabase;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingDatabase, value);
    }

    public string StartupErrorTitle
    {
        get => _startupErrorTitle;
        private set => this.RaiseAndSetIfChanged(ref _startupErrorTitle, value);
    }

    public string StartupErrorMessage
    {
        get => _startupErrorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _startupErrorMessage, value);
            this.RaisePropertyChanged(nameof(HasStartupError));
        }
    }

    public bool HasStartupError => !string.IsNullOrEmpty(_startupErrorMessage);

    public string IntegrityCheckStatus
    {
        get => _integrityCheckStatus;
        private set => this.RaiseAndSetIfChanged(ref _integrityCheckStatus, value);
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

    public string WindowTitle => $"{AppText.WindowTitle} {GetDisplayVersion()}";

    public string HeroTitle => AppText.WindowTitle;

    public string SearchWatermark => AppText.SearchWatermark;

    public string ClipboardHistoryCaptionText => AppText.ClipboardHistoryCaption;

    private static string GetDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString()
            : informationalVersion.Split('+', 2)[0];

        return string.IsNullOrWhiteSpace(version) ? string.Empty : $"v{version}";
    }

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
    public string FuzzyFilterTooltip => BuildFilterTooltip("Fuzzy: tolerate small typos and match prefixes",
        _settingsService.Current.EnableToggleFuzzyHotkey, _settingsService.Current.ToggleFuzzyHotkey);
    public string SemanticFilterTooltip => BuildFilterTooltip("Semantic: rank by meaning (ignored with regex/wildcard/whole-word)",
        _settingsService.Current.EnableToggleSemanticHotkey, _settingsService.Current.ToggleSemanticHotkey);

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

    public string SettingsDatabasePasswordWarningText => AppText.SettingsDatabasePasswordWarning;

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
    public string SettingsExternalImageEditorPathLabel => AppText.SettingsExternalImageEditorPathLabel;
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

    public bool ShowSelectedFilesTextual => HasSelectedClip
        && SelectedClip?.Clip.ContentType == ContentType.Files
        && _contentDisplayMode == ContentDisplayMode.Textual
        && HasSelectedClipFileItems;

    public bool ShowSelectedFilesTextualFallback => HasSelectedClip
        && SelectedClip?.Clip.ContentType == ContentType.Files
        && _contentDisplayMode == ContentDisplayMode.Textual
        && !HasSelectedClipFileItems;

    public bool ShowSelectedImageRenderer => HasSelectedClip && SelectedClip?.Clip.ContentType == ContentType.Image;

    private bool HasSelectedClipImageBytes => SelectedClip?.Clip.ContentBytes is { Length: > 0 };

    public bool ShowSelectedImagePreview => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Preview && HasSelectedClipImageBytes;

    public bool ShowSelectedImageEditor => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Editor && HasSelectedClipImageBytes;

    public bool ShowSelectedImagePlaceholder => ShowSelectedImageRenderer && !HasSelectedClipImageBytes && _imageViewMode != ImageViewMode.Text;

    public bool ShowSelectedImageOcrText => ShowSelectedImageRenderer && _imageViewMode == ImageViewMode.Text;

    public bool ShowSelectedImageOcrTextBox =>
        ShowSelectedImageOcrText && HasSelectedClipOcrText && !IsSelectedClipImageOcrRunning;

    public bool ShowSelectedImageOcrEmptyState =>
        ShowSelectedImageOcrText
        && !HasSelectedClipOcrText
        && !IsSelectedClipImageOcrRunning;

    public bool ShowSelectedImageOcrBusy =>
        ShowSelectedImageOcrText && IsSelectedClipImageOcrRunning;

    public bool CanRunOcrOnEmptyState => ShowSelectedImageOcrEmptyState && CanRunOcr;

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
            this.RaisePropertyChanged(nameof(ShowSelectedImageOcrTextBox));
            this.RaisePropertyChanged(nameof(ShowSelectedImageOcrEmptyState));
            this.RaisePropertyChanged(nameof(ShowSelectedImageOcrBusy));
            this.RaisePropertyChanged(nameof(CanRunOcrOnEmptyState));
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

    public bool CanEditSelectedRichTextInRenderedMode =>
        SelectedClip?.Clip.ContentType == ContentType.RichText
        && SelectedClip?.Clip.ContentFormat == ClipContentFormat.Html
        && _contentDisplayMode == ContentDisplayMode.Rendered;

    public bool SelectedClipTextIsReadOnly => !IsSelectedClipTextEditable;

    public bool SelectedClipRenderedContentIsReadOnly => !CanEditSelectedRichTextInRenderedMode;

    public bool ShowCopyEditedClipButton
        => (IsSelectedClipTextEditable
            && (ShowSelectedTextRenderer || ShowSelectedRichTextRenderer || ShowRawTextContent))
           || CanEditSelectedRichTextInRenderedMode;

    public bool HasEditedClipChanges => (IsSelectedClipTextEditable || CanEditSelectedRichTextInRenderedMode)
        && !string.Equals(_editedClipText, _editedClipBaseline, StringComparison.Ordinal);

    public string SelectedClipRenderedText => _selectedClipRenderedText;

    public string SelectedClipRawContent => _selectedClipRawContent;

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

            if (_selectedContentTypes.Count > 0)
            {
                var labels = _selectedContentTypes
                    .Select(t => ContentTypeOptions.FirstOrDefault(o => o.Value == t)?.Label)
                    .Where(static l => !string.IsNullOrEmpty(l))!
                    .Cast<string>()
                    .ToList();
                if (labels.Count > 0)
                {
                    parts.Add(string.Join(", ", labels));
                }
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

    public string AiPromptTitle => _aiPromptKind switch
    {
        AiPresetKind.ImageToText => "Describe what you want to extract from the image",
        AiPresetKind.ImageToImage => "Describe how you want to change the image",
        _ => "Describe what you want",
    };

    public string AiPromptDescription => _aiPromptKind switch
    {
        AiPresetKind.ImageToText => "The selected image clip will be sent to your AI provider. The response text is saved as a new clip.",
        AiPresetKind.ImageToImage => "The selected image clip will be sent to your AI provider. The generated image is copied back as a new clip.",
        _ => "The selected or checked text clips will be sent to your AI provider. The result is saved as a new clip.",
    };

    public string AiPromptPlaceholder => _aiPromptKind switch
    {
        AiPresetKind.ImageToText => "e.g. Extract all visible text, or Describe the UI and the main warning",
        AiPresetKind.ImageToImage => "e.g. Remove the background, or Clean up and sharpen this screenshot",
        _ => "e.g. Rewrite this as a formal email, or Convert to JSON",
    };

    public string AiPromptApplyLabel => _aiPromptKind == AiPresetKind.ImageToImage ? "Generate" : "Apply";

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

    private Models.AiProvider _settingsAiProvider = AppSettings.Default.AiProvider;

    public Models.AiProvider SettingsAiProvider
    {
        get => _settingsAiProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsAiProvider, value);
            this.RaisePropertyChanged(nameof(IsOpenAiSettingsVisible));
            this.RaisePropertyChanged(nameof(IsCopilotSettingsVisible));
        }
    }

    public Models.AiProvider[] AiProviderOptions { get; } = Enum.GetValues<Models.AiProvider>();

    public bool IsOpenAiSettingsVisible => SettingsAiProvider == Models.AiProvider.OpenAi;
    public bool IsCopilotSettingsVisible => SettingsAiProvider == Models.AiProvider.Copilot;

    private string _copilotSignInStatus = string.Empty;

    public string CopilotSignInStatus
    {
        get => _copilotSignInStatus;
        private set => this.RaiseAndSetIfChanged(ref _copilotSignInStatus, value);
    }

    private string _copilotUserCode = string.Empty;

    /// <summary>
    /// The short device-flow user code (e.g. ABCD-1234) the user must paste
    /// into github.com/login/device. Empty when no sign-in is in progress.
    /// Bound to a selectable, copyable surface in Settings.
    /// </summary>
    public string CopilotUserCode
    {
        get => _copilotUserCode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _copilotUserCode, value);
            this.RaisePropertyChanged(nameof(HasCopilotUserCode));
        }
    }

    public bool HasCopilotUserCode => !string.IsNullOrWhiteSpace(_copilotUserCode);

    private string _copilotVerificationUri = string.Empty;

    public string CopilotVerificationUri
    {
        get => _copilotVerificationUri;
        private set => this.RaiseAndSetIfChanged(ref _copilotVerificationUri, value);
    }

    private bool _isCopilotSigningIn;

    public bool IsCopilotSigningIn
    {
        get => _isCopilotSigningIn;
        private set => this.RaiseAndSetIfChanged(ref _isCopilotSigningIn, value);
    }

    public bool IsCopilotSignedIn => _copilotAuthService?.IsSignedIn == true;

    public bool SettingsEnableAutoUpdate
    {
        get => _settingsEnableAutoUpdate;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableAutoUpdate, value);
    }

    public bool SettingsAutoApplyUpdatesOnStartup
    {
        get => _settingsAutoApplyUpdatesOnStartup;
        set => this.RaiseAndSetIfChanged(ref _settingsAutoApplyUpdatesOnStartup, value);
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

    public string SettingsToggleFuzzyHotkey
    {
        get => _settingsToggleFuzzyHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleFuzzyHotkey, value);
    }

    public bool SettingsEnableToggleFuzzyHotkey
    {
        get => _settingsEnableToggleFuzzyHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleFuzzyHotkey, value);
    }

    public string SettingsToggleSemanticHotkey
    {
        get => _settingsToggleSemanticHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleSemanticHotkey, value);
    }

    public bool SettingsEnableToggleSemanticHotkey
    {
        get => _settingsEnableToggleSemanticHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsEnableToggleSemanticHotkey, value);
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

    private bool _settingsEnableSemanticSearch = AppSettings.Default.EnableSemanticSearch;

    public bool SettingsEnableSemanticSearch
    {
        get => _settingsEnableSemanticSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsEnableSemanticSearch, value);
            this.RaisePropertyChanged(nameof(IsSemanticSearchEnabled));
        }
    }

    public bool IsSemanticSearchEnabled => _settingsService.Current.EnableSemanticSearch;

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
    private static readonly string _semanticKeywords = "semantic embedding embeddings similarity vector search meaning ai ml rerun reembed sort relevance date proximity";

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

    public string SettingsExternalImageEditorPath
    {
        get => _settingsExternalImageEditorPath;
        set => this.RaiseAndSetIfChanged(ref _settingsExternalImageEditorPath, value);
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
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsDatabasePassword, value);
            this.RaisePropertyChanged(nameof(IsPasswordMismatchVisible));
            this.RaisePropertyChanged(nameof(IsPendingPlaintextEncryptionPasswordChange));
            this.RaisePropertyChanged(nameof(IsRememberPasswordWarningVisible));
        }
    }

    public string SettingsDatabasePasswordConfirm
    {
        get => _settingsDatabasePasswordConfirm;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsDatabasePasswordConfirm, value);
            this.RaisePropertyChanged(nameof(IsPasswordMismatchVisible));
        }
    }

    public bool IsPasswordMismatchVisible =>
        !string.IsNullOrEmpty(SettingsDatabasePassword)
        && !string.Equals(SettingsDatabasePassword, SettingsDatabasePasswordConfirm, StringComparison.Ordinal);

    /// <summary>
    /// True when Save is about to persist a non-empty encryption password as
    /// plaintext to disk (i.e. the user enabled "Remember password" and the
    /// stored value would change). Drives the plaintext-storage confirmation
    /// dialog.
    /// </summary>
    public bool IsPendingPlaintextEncryptionPasswordChange =>
        SettingsRememberDatabasePassword
        && !string.IsNullOrEmpty(SettingsDatabasePassword)
        && (!_storageOptionsService.Current.RememberPassword
            || !string.Equals(SettingsDatabasePassword, _storageOptionsService.Current.DatabasePassword, StringComparison.Ordinal));

    public bool SettingsRememberDatabasePassword
    {
        get => _settingsRememberDatabasePassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _settingsRememberDatabasePassword, value);
            this.RaisePropertyChanged(nameof(IsPendingPlaintextEncryptionPasswordChange));
            this.RaisePropertyChanged(nameof(IsRememberPasswordWarningVisible));
        }
    }

    /// <summary>
    /// True when the inline plaintext-storage warning under the password field
    /// should be visible: user typed a password and ticked "Remember".
    /// </summary>
    public bool IsRememberPasswordWarningVisible =>
        SettingsRememberDatabasePassword && !string.IsNullOrEmpty(SettingsDatabasePassword);

    public bool StorageDatabaseExists => _storageOptionsService.DatabaseExists;

    public IStorageOptionsService GetStorageOptionsService() => _storageOptionsService;

    public void NotifyStorageOptionsChanged()
    {
        SettingsDatabasePassword = _storageOptionsService.Current.DatabasePassword;
        SettingsDatabasePasswordConfirm = _storageOptionsService.Current.DatabasePassword;
        SettingsRememberDatabasePassword = _storageOptionsService.Current.RememberPassword;
        StatusText = "Database re-encrypted.";
        this.RaisePropertyChanged(nameof(StorageDatabaseExists));
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
        FlushCurrentFilterState();
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
            ApplyPersistedFilters(draftSettings, notify: true);
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
                var preset = CommandLineOptions.PresetDatabasePassword;
                if (!string.IsNullOrEmpty(preset))
                {
                    Trace.TraceInformation("Using database password supplied via --password command-line argument.");
                    _storageOptionsService.SetInMemoryPassword(preset);
                }
                else if (!string.IsNullOrEmpty(_storageOptionsService.Current.DatabasePassword)
                    && StorageOptionsService.CanOpenWithPassword(
                        _storageOptionsService.Current.DatabasePath,
                        _storageOptionsService.Current.DatabasePassword))
                {
                    // Persisted ("Remember password" was on) and verified — skip the prompt.
                    Trace.TraceInformation("Database auto-unlocked with the persisted password.");
                }
                else
                {
                    IsPasswordPromptOpen = true;
                    StatusText = "Enter your database password to continue.";
                    _isStarted = true;
                    return;
                }
            }

            IsLoadingDatabase = true;
            StatusText = "Loading clipboard library\u2026";
            _isStarted = true;

            _ = StartDatabaseInBackgroundAsync();
        }
        finally
        {
            _isStartupInProgress = false;
        }
    }

    private async Task StartDatabaseInBackgroundAsync()
    {
        try
        {
            await Task.Run(async () => await StartDatabaseAsync().ConfigureAwait(false)).ConfigureAwait(false);
            await RefreshAsync();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoadingDatabase = false;
                IsWelcomeOpen = false;
            });

            StartBackgroundServices();
            _ = ApplyMaintenanceAndRefreshAsync(forceRefresh: false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Database startup failed: {ex}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoadingDatabase = false;
                StartupErrorTitle = AppText.StartupErrorTitle;
                StartupErrorMessage = ex.Message;
                StatusText = AppText.FormatErrorStatus(ex.Message);
            });
        }
    }

    public void ReportStartupFailure(Exception ex)
    {
        Trace.TraceError($"Startup failure: {ex}");
        StartupErrorTitle = AppText.StartupErrorTitle;
        StartupErrorMessage = ex.Message;
        StatusText = AppText.FormatErrorStatus(ex.Message);
    }

    private async Task OpenLogsFolderAsync()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(Diagnostics.TraceConfiguration.LogFilePath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                await _systemInteractionService.OpenPathAsync(folder);
            }
        }
        catch (Exception ex)
        {
            ReportError("Open logs folder", ex);
        }
    }

    private async Task OpenDatabaseFolderAsync()
    {
        try
        {
            var dbPath = _storageOptionsService.Current.DatabasePath;
            var folder = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                await _systemInteractionService.OpenPathAsync(folder);
            }
        }
        catch (Exception ex)
        {
            ReportError("Open database folder", ex);
        }
    }

    private async Task RunIntegrityCheckAsync()
    {
        IntegrityCheckStatus = "Running…";
        try
        {
            var problems = await Task.Run(() =>
            {
                var found = new System.Collections.Generic.List<string>();
                var dbPath = _storageOptionsService.Current.DatabasePath;
                var password = _storageOptionsService.Current.DatabasePassword;
                var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                };
                if (!string.IsNullOrEmpty(password))
                {
                    builder.Password = password;
                }
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = reader.GetString(0);
                    if (!string.Equals(row, "ok", StringComparison.Ordinal))
                    {
                        found.Add(row);
                    }
                }
                return found;
            });

            if (problems.Count == 0)
            {
                IntegrityCheckStatus = "Integrity OK";
            }
            else
            {
                var summary = string.Join("; ", problems.Take(3));
                if (problems.Count > 3)
                {
                    summary += $" (+{problems.Count - 3} more)";
                }
                IntegrityCheckStatus = $"Problems found: {summary}";
                Trace.TraceError($"Integrity check found problems: {string.Join("; ", problems)}");
            }
        }
        catch (Exception ex)
        {
            IntegrityCheckStatus = $"Check failed: {ex.Message}";
            Trace.TraceError($"Integrity check failed: {ex}");
        }
    }

    public void RefreshBackups()
    {
        try
        {
            Backups.Clear();
            foreach (var info in _databaseBackupService.ListBackups())
            {
                Backups.Add(new DatabaseBackupItem(info));
            }
            BackupRestoreStatus = Backups.Count switch
            {
                0 => "No backups yet (one is created per day).",
                1 => "1 backup available",
                _ => $"{Backups.Count} backups available",
            };
        }
        catch (Exception ex)
        {
            BackupRestoreStatus = $"Listing failed: {ex.Message}";
            Trace.TraceError($"List backups failed: {ex}");
        }
    }

    private async Task RestoreBackupAsync(Window? owner)
    {
        var target = SelectedBackup;
        if (target is null)
        {
            return;
        }

        var confirmed = owner is null
            ? true
            : await Clipthrough.Views.ConfirmDialog.ShowAsync(
                owner,
                "Restore from backup?",
                $"Replace the current database with the snapshot from {target.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}?\n\nThe current database will be renamed with a .before-restore-* suffix so the swap is reversible. The application will exit afterwards; please restart it to load the restored data.",
                "Restore",
                "Cancel");
        if (!confirmed)
        {
            return;
        }

        try
        {
            BackupRestoreStatus = "Stopping background services…";

            // Stop everything that holds a SqliteConnection open so the file
            // moves below don't race against an active writer.
            _clipboardMonitorService.Stop();
            await _backgroundOcrQueue.StopAsync();
            if (_embeddingWorker is not null)
            {
                await _embeddingWorker.StopAsync();
            }

            BackupRestoreStatus = "Restoring…";
            await _databaseBackupService.RestoreAsync(target.Path);

            BackupRestoreStatus = "Restored. The app will exit — restart to load the restored data.";
            _notificationService.PublishInfo("Database restored", "Restart Clipthrough to load the restored snapshot.");

            // Defer the shutdown so the user sees the status update first.
            _ = Task.Delay(TimeSpan.FromMilliseconds(800)).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown(0);
                    }
                }));
        }
        catch (Exception ex)
        {
            BackupRestoreStatus = $"Restore failed: {ex.Message}";
            Trace.TraceError($"Restore from backup failed: {ex}");
            _notificationService.PublishError("Restore failed", ex.Message);
        }
    }

    /// <summary>
    /// Standard error reporter used by Rx subscriptions and async catch blocks.
    /// Always traces the full exception (so the session log captures the type and
    /// stack) and then surfaces a short message in <see cref="StatusText"/>. Use
    /// this in preference to setting <see cref="StatusText"/> directly from a
    /// catch — otherwise the error is invisible to anyone reading the log.
    /// </summary>
    private void ReportError(string context, Exception ex)
    {
        Trace.TraceError($"{context} failed: {ex}");
        StatusText = AppText.FormatErrorStatus(ex.Message);
    }

    private Task RefreshAsync() => QueueRefreshAsync(null);

    private Task RefreshAsync(long? preferredSelectionId) => QueueRefreshAsync(preferredSelectionId);

    private Task QueueRefreshAsync(long? preferredSelectionId)
    {
        lock (_refreshQueueLock)
        {
            _hasQueuedRefresh = true;
            if (preferredSelectionId.HasValue)
            {
                _queuedRefreshPreferredSelectionId = preferredSelectionId;
            }

            if (_queuedRefreshTask.IsCompleted)
            {
                _queuedRefreshTask = ProcessQueuedRefreshesAsync();
            }

            return _queuedRefreshTask;
        }
    }

    private async Task ProcessQueuedRefreshesAsync()
    {
        while (true)
        {
            long? preferredSelectionId;
            lock (_refreshQueueLock)
            {
                if (!_hasQueuedRefresh)
                {
                    _queuedRefreshTask = Task.CompletedTask;
                    return;
                }

                _hasQueuedRefresh = false;
                preferredSelectionId = _queuedRefreshPreferredSelectionId;
                _queuedRefreshPreferredSelectionId = null;
            }

            await PerformRefreshAsync(preferredSelectionId).ConfigureAwait(false);
        }
    }

    private async Task PerformRefreshAsync(long? preferredSelectionId)
    {
        if (!_isDatabaseReady)
        {
            return;
        }

        var sw = CommandLineOptions.LogPopupTimings ? Stopwatch.StartNew() : null;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = true);
        try
        {
            var request = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => new RefreshRequest(BuildFilters(offset: 0), UseSemanticClipSearch));
            if (sw is not null) Trace.TraceInformation($"[refresh-timing] built-request @ {sw.ElapsedMilliseconds}ms search='{request.Filters.SearchText}' semantic={request.UseSemanticClipSearch}");
            var result = await SearchClipsAsync(request.Filters, request.UseSemanticClipSearch).ConfigureAwait(false);
            if (sw is not null) Trace.TraceInformation($"[refresh-timing] db-search-complete @ {sw.ElapsedMilliseconds}ms items={result.Items.Count} total={result.TotalMatchingCount}");

            // Apply the refresh result regardless of visibility. With
            // optimistic captures running continuously, ApplyRefreshResult's
            // incremental diff is cheap; keeping the list current while hidden
            // is what makes the next popup show instant.
            var applied = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyRefreshResult(result, preferredSelectionId);
                _isClipListStale = false;
                return true;
            });

            if (sw is not null) Trace.TraceInformation($"[refresh-timing] ui-applied={applied} @ {sw.ElapsedMilliseconds}ms");

            if (applied && !string.IsNullOrWhiteSpace(request.Filters.SearchText))
            {
                await _searchHistoryService.SaveSearchAsync(request.Filters.SearchText).ConfigureAwait(false);
            }
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            if (sw is not null)
            {
                sw.Stop();
                Trace.TraceInformation($"[refresh-timing] total {sw.ElapsedMilliseconds}ms");
            }
        }
    }

    /// <summary>
    /// Called by the application shell whenever the main window's visibility
    /// changes. While hidden, optimistic clip-list mutations and the throttled
    /// refresh are bypassed; on transition back to visible we trigger one
    /// authoritative refresh if anything changed in the meantime.
    /// </summary>
    public void SetMainWindowVisible(bool isVisible)
    {
        if (_isMainWindowVisible == isVisible)
        {
            return;
        }

        var wasVisible = _isMainWindowVisible;
        _isMainWindowVisible = isVisible;
        if (CommandLineOptions.LogPopupTimings)
        {
            Trace.TraceInformation($"[popup-timing] visibility={isVisible} stale={_isClipListStale}");
        }

        if (isVisible && !wasVisible)
        {
            // Match the typical clipboard-manager UX: every fresh popup
            // surfaces the newest clip as the active selection, regardless of
            // what was selected last time.
            _selectNewestOnNextRefresh = true;

            if (_isDatabaseReady)
            {
                if (_isClipListStale)
                {
                    // Refresh clears the stale flag once it successfully
                    // applies. Selection is realigned to newest by the
                    // _selectNewestOnNextRefresh flag.
                    _ = RefreshAsync();
                }
                else
                {
                    // No data change while hidden, but still re-select newest
                    // so the user always lands on the most recent clip.
                    SelectedClip = GetDefaultAutoSelectedClip();
                    _selectNewestOnNextRefresh = false;
                }
            }
        }
    }

    private Task<ClipSearchResult> SearchClipsAsync(ClipSearchFilters filters, bool useSemanticClipSearch)
        => Task.Run(async () =>
        {
            var result = await _clipStoreService.SearchAsync(filters).ConfigureAwait(false);
            return await ApplySemanticFusionAsync(filters, result, useSemanticClipSearch).ConfigureAwait(false);
        });

    private async Task<ClipSearchResult> ApplySemanticFusionAsync(ClipSearchFilters filters, ClipSearchResult ftsResult, bool useSemanticClipSearch)
    {
        if (_semanticSearchService is null)
        {
            return ftsResult;
        }
        if (!useSemanticClipSearch)
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
        catch (Exception ex)
        {
            Trace.TraceWarning($"Semantic search query failed; returning FTS-only result: {ex.Message}");
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
        if (filters.ContentTypes is { Count: > 0 } types && !types.Contains(clip.ContentType)) return false;
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
            var request = new RefreshRequest(BuildFilters(_currentOffset), UseSemanticClipSearch);
            var result = await SearchClipsAsync(request.Filters, request.UseSemanticClipSearch).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
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
            });
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
        _ = await TryCopySelectedAsync();
    }

    private async Task PasteSelectedAsync()
    {
        // The view-side ExecutePasteSelectedAndHide drives the full paste sequence
        // (copy → restore focus → hide → delay → SendInput). This command is only
        // called from global hotkey paste paths that don't go through the view.
        if (!await TryCopySelectedAsync())
        {
            return;
        }

        await Task.Delay(150);
        _systemInteractionService.SimulatePasteKeystroke();
    }

    /// <summary>
    /// Copies the selected clip to the clipboard. Returns true on success.
    /// Called by the view's paste-and-hide path so it can control the exact
    /// ordering of copy → focus-restore → hide → delay → SendInput.
    /// </summary>
    internal async Task<bool> TryCopySelectedForPasteAsync() => await TryCopySelectedAsync();

    private async Task<bool> TryCopySelectedAsync()
    {
        try
        {
            var clip = GetEffectiveSelectedClip();
            if (clip is null)
            {
                return false;
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
                return true;
            }

            if (clip.Clip.ContentType == ContentType.RichText)
            {
                await _systemInteractionService.CopyRichContentAsync(clip.FullContent, SelectedClipRenderedText, clip.Clip.ContentFormat);
                StatusText = AppText.FormatCopiedClip(clip.DisplayContentType.ToLower(AppText.CurrentCulture));
                PublishSensitiveCopyNotificationIfNeeded(clip);
                TrackPasteInBackground(clip.Clip.Id);
                return true;
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
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy selected failed: {ex}");
            StatusText = $"Copy failed: {ex.Message}";
            return false;
        }
    }

    internal async Task CopySelectedAsPlainTextAsync()
    {
        var clip = GetEffectiveSelectedClip();
        if (clip is null)
        {
            return;
        }

        try
        {
            _clipboardMonitorService.SuppressNext();
            await _systemInteractionService.CopyTextAsync(clip.FullContent);
            StatusText = AppText.FormatCopiedClip("plain text");
            TrackPasteInBackground(clip.Clip.Id);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy as plain text failed: {ex}");
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private async void TrackPasteInBackground(long clipId)
    {
        try
        {
            await Task.Run(() => _clipStoreService.MarkPastedAsync(clipId));
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

        var editorPath = ResolveExternalEditorPath(SelectedClip);
        if (!string.IsNullOrWhiteSpace(editorPath) && !System.IO.File.Exists(ExtractExecutablePath(editorPath)))
        {
            _notificationService.PublishError(
                "Configured editor not found",
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
            Trace.TraceWarning($"OpenUrl failed: {ex}");
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
            Trace.TraceWarning($"CopySelectedClipWindowTitle failed: {ex}");
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
            var source = await Task.Run(() => _clipStoreService.GetByIdAsync(sourceId));
            if (source is null)
            {
                StatusText = $"Clip #{sourceId} no longer exists.";
                return;
            }
            await RefreshAsync(sourceId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"NavigateToLineageSource({sourceId}) failed: {ex}");
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

        var capturedClip = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = editedBytes,
            ContentText = string.IsNullOrWhiteSpace(clip.FullContent) ? null : clip.FullContent,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = AppText.WindowTitle,
            SourceAppPath = Environment.ProcessPath,
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
        }));

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
            await Task.Run(() => _clipStoreService.SetFavoriteAsync(clip.Id, nextIsFavorite));
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
            await Task.Run(() => _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned));
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
        await Task.Run(() => _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned));
        clip.SetPinnedState(nextIsPinned);
        await RefreshAsync(clip.Id);
    }

    private async Task TogglePinClipAsync(ClipItemViewModel clip)
    {
        var nextIsPinned = !clip.IsPinned;
        await Task.Run(() => _clipStoreService.SetPinnedAsync(clip.Id, nextIsPinned));
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
        if (SelectedClip is null || (!IsSelectedClipTextEditable && !CanEditSelectedRichTextInRenderedMode))
        {
            return;
        }

        var text = EditedClipText ?? string.Empty;
        var contentType = SelectedClip.Clip.ContentType;
        var isRich = contentType == ContentType.RichText;
        var copyAsRichContent = isRich
            && (_contentDisplayMode == ContentDisplayMode.Raw || CanEditSelectedRichTextInRenderedMode);

        // Put on clipboard (suppressed so we don't race our own capture)
        _clipboardMonitorService.SuppressNext();
        if (copyAsRichContent)
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
            captured = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
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
            }));
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

        var producesHtml = transformation == TextTransformation.BoxTableToHtml;
        var label = GetTextTransformationLabel(transformation);
        StatusText = $"Applying {label}…";
        try
        {
            await ApplyTransformToTargetsAsync(
                (source, _) => Task.FromResult(TextTransformationService.Apply(transformation, source)),
                label,
                multiSummary: count => $"Applied {label} to {count} clips",
                transformKind: $"builtin:{transformation}",
                outputFormat: producesHtml ? ClipContentFormat.Html : ClipContentFormat.PlainText,
                noChangeNotificationTitle: producesHtml ? "Text table → HTML made no changes" : null,
                noChangeNotificationMessage: producesHtml
                    ? "The selected text did not contain a supported table, so nothing was changed."
                    : null);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Apply text transformation '{label}' failed: {ex}");
            StatusText = $"Failed to apply {label}: {ex.Message}";
            _notificationService.PublishError($"Failed to apply {label}", ex.Message);
        }
    }

    private async Task ApplyTransformationToSingleClipAsync(ClipItemViewModel clip, TextTransformation transformation)
    {
        if (clip is null || transformation == TextTransformation.None)
        {
            return;
        }

        if (!clip.CanTransform)
        {
            StatusText = "Only text and file clips can be transformed";
            return;
        }

        var source = GetTransformSourceText(clip);
        if (string.IsNullOrEmpty(source))
        {
            StatusText = "Selected clip has no text or file paths to transform";
            return;
        }

        var label = GetTextTransformationLabel(transformation);
        StatusText = $"Applying {label}…";
        try
        {
            var result = TextTransformationService.Apply(transformation, source);
            if (string.Equals(result, source, StringComparison.Ordinal))
            {
                StatusText = $"{label} produced no change";
                if (transformation == TextTransformation.BoxTableToHtml)
                {
                    _notificationService.PublishWarning(
                        "Text table → HTML made no changes",
                        "The selected text did not contain a supported table, so nothing was changed.");
                }
                return;
            }

            var textBytes = System.Text.Encoding.UTF8.GetBytes(result);
            var isHtml = transformation == TextTransformation.BoxTableToHtml;
            var captured = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = textBytes,
                ContentText = result,
                ContentType = isHtml ? ContentType.RichText : ContentType.Text,
                ContentFormat = isHtml ? ClipContentFormat.Html : ClipContentFormat.PlainText,
                SourceApp = clip.SourceApp,
                SourceAppPath = clip.Clip.SourceAppPath,
                SourceAppIconBytes = clip.Clip.SourceAppIconBytes,
                SourceWindowTitle = clip.Clip.SourceWindowTitle,
                IncrementExistingCopyCount = false,
                SourceClipId = clip.Clip.Id,
                TransformKind = $"builtin:{transformation}",
                SkipPostInsertMaintenance = true,
            }));
            var copyFailure = await CopyTransformResultToClipboardAsync(result, isHtml ? ClipContentFormat.Html : ClipContentFormat.PlainText);
            if (copyFailure is null)
            {
                StatusText = "Transformed and copied to clipboard";
            }
            else
            {
                StatusText = $"Transformed, but copy failed: {copyFailure}";
                _notificationService.PublishWarning("Transform copy failed", copyFailure);
            }
            await RefreshAsync(captured?.Id);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ApplyTransformationToSingleClip '{label}' failed: {ex}");
            StatusText = $"Failed to apply {label}: {ex.Message}";
            _notificationService.PublishError($"Failed to apply {label}", ex.Message);
        }
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
            Trace.TraceWarning($"User script '{script.Name}' failed: {ex}");
            StatusText = $"Script '{script.Name}' failed: {ex.Message}";
            _notificationService.PublishError($"Script '{script.Name}' failed", ex.Message);
            return;
        }

        if (string.Equals(StatusText, $"Running script '{script.Name}'…", StringComparison.Ordinal))
        {
            StatusText = previous;
        }
    }

    private async Task<string?> CopyTransformResultToClipboardAsync(string result, ClipContentFormat format)
    {
        if (string.IsNullOrEmpty(result))
        {
            return "Transform result was empty.";
        }

        try
        {
            _clipboardMonitorService.SuppressNext();
            if (format == ClipContentFormat.Html)
            {
                var plain = ClipDisplayFormatter.RenderRichContent(result);
                await _systemInteractionService.CopyRichContentAsync(result, plain, ClipContentFormat.Html);
            }
            else
            {
                await _systemInteractionService.CopyTextAsync(result);
            }

            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Auto-copy of transform result failed: {ex.Message}");
            return ex.Message;
        }
    }

    private static string GetTransformSourceText(ClipItemViewModel clip)
    {
        if (clip.Clip.ContentType != ContentType.Files)
        {
            return clip.Clip.Content ?? string.Empty;
        }

        var fileItems = ClipDisplayFormatter.BuildFileItems(clip.Clip.Content);
        return fileItems.Count == 0
            ? clip.Clip.Content ?? string.Empty
            : string.Join(Environment.NewLine, fileItems);
    }

    private async Task ApplyTransformToTargetsAsync(
        Func<string, CancellationToken, Task<string>> transform,
        string singleLabel,
        Func<int, string> multiSummary,
        string? transformKind = null,
        ClipContentFormat outputFormat = ClipContentFormat.PlainText,
        string? noChangeNotificationTitle = null,
        string? noChangeNotificationMessage = null)
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
        string? lastResult = null;
        foreach (var target in targets)
        {
            if (!target.CanTransform)
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
                source = GetTransformSourceText(target);
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
            var isHtml = outputFormat == ClipContentFormat.Html;
            var captured = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = textBytes,
                ContentText = result,
                ContentType = isHtml ? ContentType.RichText : ContentType.Text,
                ContentFormat = outputFormat,
                SourceApp = target.SourceApp,
                SourceAppPath = target.Clip.SourceAppPath,
                SourceAppIconBytes = target.Clip.SourceAppIconBytes,
                SourceWindowTitle = target.Clip.SourceWindowTitle,
                IncrementExistingCopyCount = false,
                SourceClipId = target.Clip.Id,
                TransformKind = transformKind,
                SkipPostInsertMaintenance = true,
            }));
            if (captured is not null)
            {
                lastCreatedId = captured.Id;
                lastResult = result;
            }
            transformed++;
        }

        if (transformed > 0)
        {
            if (transformed == 1 && lastCreatedId is not null && lastResult is not null)
            {
                var copyFailure = await CopyTransformResultToClipboardAsync(lastResult, outputFormat);
                if (copyFailure is null)
                {
                    StatusText = useSelectionSlice
                        ? $"Applied {singleLabel} to selection and copied"
                        : "Transformed and copied to clipboard";
                }
                else
                {
                    StatusText = $"Applied {singleLabel}, but copy failed: {copyFailure}";
                    _notificationService.PublishWarning("Transform copy failed", copyFailure);
                }
            }
            else
            {
                StatusText = transformed == 1
                    ? (useSelectionSlice ? $"Applied {singleLabel} to selection" : AppText.EditedClipCopiedStatus)
                    : multiSummary(transformed);
            }
            await RefreshAsync(lastCreatedId);
        }
        else
        {
            StatusText = $"No text or file clips changed by {singleLabel}";
            if (noChangeNotificationTitle is not null && noChangeNotificationMessage is not null)
            {
                _notificationService.PublishWarning(noChangeNotificationTitle, noChangeNotificationMessage);
            }
        }
    }

    private static string GetTextTransformationLabel(TextTransformation transformation)
        => transformation switch
        {
            TextTransformation.BoxTableToHtml => "text table → HTML",
            _ => "transformation",
        };

    // Old single-clip script body replaced above; keep old method signature for legacy callers (none left).

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

        await Task.Run(() => _clipStoreService.MarkOcrForRerunAsync(clip.Clip.Id));
        StatusText = "Queued OCR…";
        _backgroundOcrQueue.Enqueue(clip.Clip.Id);
    }

    private string _semanticCoverageText = string.Empty;
    public string SemanticCoverageText
    {
        get => _semanticCoverageText;
        private set => this.RaiseAndSetIfChanged(ref _semanticCoverageText, value);
    }

    public bool IsSemanticCoverageVisible => _embeddingWorker is not null && !string.IsNullOrEmpty(SemanticCoverageText);

    private string _ocrCoverageText = string.Empty;
    public string OcrCoverageText
    {
        get => _ocrCoverageText;
        private set => this.RaiseAndSetIfChanged(ref _ocrCoverageText, value);
    }

    public bool IsOcrCoverageVisible => _ocrService.IsAvailable && !string.IsNullOrEmpty(OcrCoverageText);

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
            var coverage = await Task.Run(() => _embeddingWorker.GetCoverageAsync());
            var eligible = coverage.EligibleTotal;
            if (eligible <= 0)
            {
                SemanticCoverageText = "No clips to embed yet";
            }
            else
            {
                var pct = Math.Clamp((int)Math.Round(100.0 * coverage.Embedded / eligible), 0, 100);
                var suffixParts = new List<string>();
                if (coverage.Pending > 0)
                {
                    suffixParts.Add($"{coverage.Pending} queued");
                }
                if (coverage.Failed > 0)
                {
                    suffixParts.Add($"{coverage.Failed} failed");
                }

                var suffix = suffixParts.Count > 0
                    ? $" · {string.Join(" · ", suffixParts)}"
                    : string.Empty;
                SemanticCoverageText = $"Semantic: {coverage.Embedded}/{eligible} ({pct}%){suffix}";
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Semantic coverage refresh failed: {ex.Message}");
            SemanticCoverageText = string.Empty;
        }
        this.RaisePropertyChanged(nameof(IsSemanticCoverageVisible));
    }

    private async Task RefreshOcrCoverageAsync()
    {
        if (!_ocrService.IsAvailable)
        {
            OcrCoverageText = string.Empty;
            this.RaisePropertyChanged(nameof(IsOcrCoverageVisible));
            return;
        }

        try
        {
            var coverage = await Task.Run(() => _clipStoreService.GetOcrCoverageAsync());
            if (coverage.EligibleTotal <= 0)
            {
                OcrCoverageText = "No images to OCR yet";
            }
            else
            {
                var pct = (int)Math.Round(100.0 * coverage.Succeeded / coverage.EligibleTotal);
                var suffixParts = new List<string>();
                if (coverage.Pending > 0)
                {
                    suffixParts.Add($"{coverage.Pending} queued");
                }
                if (coverage.Running > 0)
                {
                    suffixParts.Add($"{coverage.Running} running");
                }
                if (coverage.Failed > 0)
                {
                    suffixParts.Add($"{coverage.Failed} failed");
                }

                var suffix = suffixParts.Count > 0
                    ? $" · {string.Join(" · ", suffixParts)}"
                    : string.Empty;
                OcrCoverageText = $"OCR: {coverage.Succeeded}/{coverage.EligibleTotal} ({pct}%){suffix}";
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"OCR coverage refresh failed: {ex.Message}");
            OcrCoverageText = string.Empty;
        }

        this.RaisePropertyChanged(nameof(IsOcrCoverageVisible));
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
            ReportError("Requeue all for embedding", ex);
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

        try
        {
            await Task.Run(() => _clipStoreService.CaptureAsync(request));
            _editedClipBaseline = _editedClipText;
        }
        catch (Exception ex)
        {
            ReportError("Auto-save edited clip", ex);
        }
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
        await Task.Run(() => _clipStoreService.SetFavoriteAsync(clip.Id, nextIsFavorite));
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
                await Task.Run(() => _clipStoreService.DeleteAsync(id));
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
        ContentTypes = _selectedContentTypes.Count == 0
            ? null
            : _selectedContentTypes.ToArray(),
        FavoritesOnly = ShowFavoritesOnly,
        SensitiveOnly = ShowSensitiveOnly,
        PastedOnly = ShowPastedOnly,
        UseRegex = UseRegexSearch,
        CaseSensitive = CaseSensitiveSearch,
        UseWildcard = UseWildcardSearch,
        WholeWord = WholeWordSearch,
        UseFuzzy = UseFuzzyClipSearch && !UseRegexSearch && !UseWildcardSearch && !WholeWordSearch,
        SortOption = SelectedSortOption.Value,
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
        var sw = CommandLineOptions.LogPopupTimings ? Stopwatch.StartNew() : null;
        var forceSelectNewest = _selectNewestOnNextRefresh;
        _selectNewestOnNextRefresh = false;
        var previousSelectionId = forceSelectNewest ? null : (preferredSelectionId ?? SelectedClip?.Id);
        var checkedIds = Clips
            .Where(static clip => clip.IsChecked)
            .Select(static clip => clip.Id)
            .ToHashSet();
        var initialCount = Clips.Count;

        _suppressEditAutoSave = true;
        try
        {
            var stats = ApplyRefreshResultIncremental(result.Items, checkedIds);
            if (sw is not null)
            {
                Trace.TraceInformation(
                    $"[refresh-timing] apply-diff added={stats.Added} removed={stats.Removed} replaced={stats.Replaced} moved={stats.Moved} fullRebuild={stats.FullRebuild} forceNewest={forceSelectNewest} (before={initialCount}, after={Clips.Count}) @ {sw.ElapsedMilliseconds}ms");
            }

            _currentOffset = result.Items.Count;
            HasMoreResults = Clips.Count < result.TotalMatchingCount;
            this.RaisePropertyChanged(nameof(HasNoClips));
            SelectedClip = previousSelectionId is null
                ? GetDefaultAutoSelectedClip()
                : Clips.FirstOrDefault(clip => clip.Id == previousSelectionId) ?? GetDefaultAutoSelectedClip();
        }
        finally
        {
            _suppressEditAutoSave = false;
        }

        RaiseBulkSelectionProperties();
        UpdateStatus(result);
        UpdateClipDisplayIndices();

        if (sw is not null)
        {
            Trace.TraceInformation($"[refresh-timing] apply-complete @ {sw.ElapsedMilliseconds}ms");
        }
    }

    private readonly record struct ApplyDiffStats(int Added, int Removed, int Replaced, int Moved, bool FullRebuild);

    /// <summary>
    /// Reconciles <see cref="Clips"/> with <paramref name="newItems"/> by doing
    /// the minimum number of ObservableCollection mutations — recreating only
    /// VMs whose underlying ClipEntry changed, moving existing ones, and
    /// disposing removed ones. Falls back to a full rebuild if the diff is so
    /// large that incremental edits would cost more than a rebuild.
    /// </summary>
    private ApplyDiffStats ApplyRefreshResultIncremental(IReadOnlyList<ClipEntry> newItems, HashSet<long> checkedIds)
    {
        if (Clips.Count == 0)
        {
            foreach (var clip in newItems)
            {
                Clips.Add(CreateClipItemViewModel(clip, checkedIds));
            }
            return new ApplyDiffStats(Added: newItems.Count, Removed: 0, Replaced: 0, Moved: 0, FullRebuild: true);
        }

        var newById = new Dictionary<long, ClipEntry>(newItems.Count);
        foreach (var entry in newItems)
        {
            newById[entry.Id] = entry;
        }

        var existingById = new Dictionary<long, ClipItemViewModel>(Clips.Count);
        foreach (var existing in Clips)
        {
            existingById[existing.Id] = existing;
        }

        var added = 0;
        var removed = 0;
        foreach (var id in existingById.Keys)
        {
            if (!newById.ContainsKey(id)) removed++;
        }
        foreach (var id in newById.Keys)
        {
            if (!existingById.ContainsKey(id)) added++;
        }
        var changedEntry = 0;
        foreach (var existing in Clips)
        {
            if (newById.TryGetValue(existing.Id, out var newEntry) && !ClipsAreMateriallyEqual(existing.Clip, newEntry))
            {
                changedEntry++;
            }
        }

        // If most of the list is changing (filter/sort/search swap, large
        // reorder) the cumulative cost of individual Move/Insert/Remove
        // notifications can exceed a full rebuild. Threshold tuned to the
        // common case where 1-5 clips change at a time.
        var diffSize = added + removed + changedEntry;
        var fullRebuildThreshold = Math.Max(50, newItems.Count / 2);
        if (diffSize > fullRebuildThreshold)
        {
            ClearClips();
            foreach (var clip in newItems)
            {
                Clips.Add(CreateClipItemViewModel(clip, checkedIds));
            }
            return new ApplyDiffStats(Added: added, Removed: removed, Replaced: changedEntry, Moved: 0, FullRebuild: true);
        }

        var movedCount = 0;
        var replacedCount = 0;

        // Step 1: drop VMs whose id is no longer in the result. Iterate
        // backwards so RemoveAt does not shift the indices we still need.
        for (var i = Clips.Count - 1; i >= 0; i--)
        {
            if (!newById.ContainsKey(Clips[i].Id))
            {
                DetachAndDisposeClip(Clips[i]);
                Clips.RemoveAt(i);
            }
        }

        // Step 2: walk the target list, reconciling each slot. Same-id items
        // whose ClipEntry instance changed (icon/sensitivity/content update)
        // are replaced wholesale because ClipItemViewModel caches several
        // computed display values at construction time.
        for (var targetIndex = 0; targetIndex < newItems.Count; targetIndex++)
        {
            var newEntry = newItems[targetIndex];

            if (targetIndex >= Clips.Count)
            {
                Clips.Insert(targetIndex, CreateClipItemViewModel(newEntry, checkedIds));
                continue;
            }

            var atIndex = Clips[targetIndex];
            if (atIndex.Id == newEntry.Id)
            {
                if (!ClipsAreMateriallyEqual(atIndex.Clip, newEntry))
                {
                    var wasChecked = atIndex.IsChecked || checkedIds.Contains(newEntry.Id);
                    DetachAndDisposeClip(atIndex);
                    Clips.RemoveAt(targetIndex);
                    Clips.Insert(targetIndex, CreateClipItemViewModel(
                        newEntry,
                        wasChecked ? new HashSet<long> { newEntry.Id } : checkedIds));
                    replacedCount++;
                }
                continue;
            }

            // Search forward for the id; if present, move/replace.
            var sourceIndex = -1;
            for (var i = targetIndex + 1; i < Clips.Count; i++)
            {
                if (Clips[i].Id == newEntry.Id)
                {
                    sourceIndex = i;
                    break;
                }
            }

            if (sourceIndex >= 0)
            {
                var existing = Clips[sourceIndex];
                if (!ClipsAreMateriallyEqual(existing.Clip, newEntry))
                {
                    var wasChecked = existing.IsChecked || checkedIds.Contains(newEntry.Id);
                    DetachAndDisposeClip(existing);
                    Clips.RemoveAt(sourceIndex);
                    Clips.Insert(targetIndex, CreateClipItemViewModel(
                        newEntry,
                        wasChecked ? new HashSet<long> { newEntry.Id } : checkedIds));
                    replacedCount++;
                }
                else
                {
                    Clips.Move(sourceIndex, targetIndex);
                    movedCount++;
                }
            }
            else
            {
                Clips.Insert(targetIndex, CreateClipItemViewModel(newEntry, checkedIds));
            }
        }

        // Step 3: trim trailing extras (defensive — should be unreachable
        // because Step 1 removed every id not in the new result).
        while (Clips.Count > newItems.Count)
        {
            var last = Clips[^1];
            DetachAndDisposeClip(last);
            Clips.RemoveAt(Clips.Count - 1);
        }

        return new ApplyDiffStats(Added: added, Removed: removed, Replaced: replacedCount, Moved: movedCount, FullRebuild: false);
    }

    private void DetachAndDisposeClip(ClipItemViewModel item)
    {
        DetachClip(item);
        item.Dispose();
    }

    /// <summary>
    /// Compares two <see cref="ClipEntry"/> instances by the fields that
    /// actually drive the row's rendered appearance. SearchAsync returns fresh
    /// ClipEntry references every time, so reference equality is useless for
    /// incremental diffing — but a quick scalar comparison lets us skip the
    /// expensive rebuild when the row's data hasn't changed.
    /// </summary>
    private static bool ClipsAreMateriallyEqual(ClipEntry a, ClipEntry b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != b.Id) return false;
        if (a.IsFavorite != b.IsFavorite) return false;
        if (a.IsSensitive != b.IsSensitive) return false;
        if (a.IsPasted != b.IsPasted) return false;
        if (a.PasteCount != b.PasteCount) return false;
        if (a.CopyCount != b.CopyCount) return false;
        if (a.PinnedAt != b.PinnedAt) return false;
        if (a.LastCopiedAt != b.LastCopiedAt) return false;
        if (a.ByteSize != b.ByteSize) return false;
        if (a.ContentType != b.ContentType) return false;
        if (a.ContentFormat != b.ContentFormat) return false;
        if (a.OcrStatus != b.OcrStatus) return false;
        if (!string.Equals(a.Hash, b.Hash, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.SourceApp, b.SourceApp, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.SourceWindowTitle, b.SourceWindowTitle, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.SourceUrl, b.SourceUrl, StringComparison.Ordinal)) return false;
        // Icon bytes can transition from null -> populated during enrichment.
        var aHasIcon = a.SourceAppIconBytes is { Length: > 0 };
        var bHasIcon = b.SourceAppIconBytes is { Length: > 0 };
        if (aHasIcon != bHasIcon) return false;
        // Sensitivity matches collection size is a cheap proxy for change.
        if ((a.SensitivityMatches?.Count ?? 0) != (b.SensitivityMatches?.Count ?? 0)) return false;
        return true;
    }

    private void ApplyCapturedClipOptimistically(ClipEntry clip)
    {
        // Keep the Clips collection current even while the popup is hidden so
        // the next show is instant. We do skip the per-insert SelectedClip
        // assignment in the hidden case: SetMainWindowVisible re-selects the
        // newest clip on show, so paying for the SelectedClip property storm
        // on every capture would be wasted work.
        UpsertClipItem(clip, targetIndex: 0, select: _isMainWindowVisible);
    }

    private void ApplyUpdatedClipOptimistically(ClipEntry clip)
    {
        var existingIndex = IndexOfClip(clip.Id);
        if (existingIndex < 0)
        {
            return;
        }

        UpsertClipItem(clip, existingIndex, select: _isMainWindowVisible && SelectedClip?.Id == clip.Id);
    }

    private void UpsertClipItem(ClipEntry clip, int targetIndex, bool select)
    {
        var existingIndex = IndexOfClip(clip.Id);
        var wasChecked = existingIndex >= 0 && Clips[existingIndex].IsChecked;
        if (existingIndex >= 0)
        {
            DetachAndDisposeClip(Clips[existingIndex]);
            Clips.RemoveAt(existingIndex);
            if (existingIndex < targetIndex)
            {
                targetIndex--;
            }
        }

        targetIndex = Math.Clamp(targetIndex, 0, Clips.Count);
        var checkedIds = wasChecked ? new HashSet<long> { clip.Id } : null;
        var item = CreateClipItemViewModel(clip, checkedIds);
        Clips.Insert(targetIndex, item);
        _currentOffset = Clips.Count;
        HasMoreResults = HasMoreResults || Clips.Count > _currentOffset;
        this.RaisePropertyChanged(nameof(HasNoClips));
        RaiseBulkSelectionProperties();
        UpdateClipDisplayIndices();
        RefreshLastCaptureSummary();

        if (select)
        {
            SelectedClip = item;
        }
    }

    public ClipItemViewModel? GetDefaultAutoSelectedClip()
    {
        if (Clips.Count == 0)
        {
            return null;
        }

        return Clips.FirstOrDefault(static clip => !clip.IsPinned) ?? Clips[0];
    }

    private void UpdateSelectedClipPresentation()
    {
        var rawContent = SelectedClip?.FullContent ?? ClipDisplayFormatter.GetRawContentDisplay(null);
        var fileItems = ClipDisplayFormatter.BuildFileItems(rawContent);
        ReplaceSelectedClipFiles(fileItems);
        _selectedClipRawContent = rawContent;
        _selectedClipRenderedText = ClipDisplayFormatter.BuildRenderedText(SelectedClip?.Clip, fileItems);
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
        this.RaisePropertyChanged(nameof(HasTextTransformTarget));
        this.RaisePropertyChanged(nameof(HasImageTransformTarget));
        this.RaisePropertyChanged(nameof(HasSelectedImageClip));
        this.RaisePropertyChanged(nameof(CanRunOcr));
        RefreshVisibleTransformMenus();
        RaiseRenderModeProperties();
        RaiseEditedClipProperties();
    }

    private void RaiseFilterStateProperties()
    {
        this.RaisePropertyChanged(nameof(ActiveFilterSummary));
        this.RaisePropertyChanged(nameof(EmptyListMessage));
    }

    private void ApplyPersistedFilters(AppSettings settings, bool notify)
    {
        _showFavoritesOnly = settings.LastShowFavoritesOnly;
        _showSensitiveOnly = settings.LastShowSensitiveOnly;
        _showPastedOnly = settings.LastShowPastedOnly;
        _useRegexSearch = settings.LastUseRegexSearch;
        _caseSensitiveSearch = settings.LastCaseSensitiveSearch;
        _useWildcardSearch = settings.LastUseWildcardSearch;
        _wholeWordSearch = settings.LastWholeWordSearch;
        _useFuzzyClipSearch = settings.LastUseFuzzyClipSearch;
        _useSemanticClipSearch = settings.LastUseSemanticClipSearch;
        _selectedContentTypes.Clear();
        if (settings.LastContentTypeFilters is { Count: > 0 } persistedTypes)
        {
            foreach (var t in persistedTypes)
            {
                _selectedContentTypes.Add(t);
            }
        }
        else if (settings.LastContentTypeFilter is { } legacy)
        {
            _selectedContentTypes.Add(legacy);
        }
        _selectedContentTypeOption = _selectedContentTypes.Count == 1
            ? ContentTypeOptions.FirstOrDefault(o => o.Value == _selectedContentTypes.First()) ?? ContentTypeOptions[0]
            : ContentTypeOptions[0];

        if (!notify)
        {
            return;
        }

        this.RaisePropertyChanged(nameof(ShowFavoritesOnly));
        this.RaisePropertyChanged(nameof(ShowSensitiveOnly));
        this.RaisePropertyChanged(nameof(ShowPastedOnly));
        this.RaisePropertyChanged(nameof(UseRegexSearch));
        this.RaisePropertyChanged(nameof(CaseSensitiveSearch));
        this.RaisePropertyChanged(nameof(UseWildcardSearch));
        this.RaisePropertyChanged(nameof(WholeWordSearch));
        this.RaisePropertyChanged(nameof(UseFuzzyClipSearch));
        this.RaisePropertyChanged(nameof(UseSemanticClipSearch));
        this.RaisePropertyChanged(nameof(SelectedContentTypeOption));
        RaiseFilterStateProperties();
        RaiseContentTypeToggleProperties();
    }

    private AppSettings BuildPersistedFilterSettings()
        => _settingsService.Current with
        {
            LastShowFavoritesOnly = ShowFavoritesOnly,
            LastShowSensitiveOnly = ShowSensitiveOnly,
            LastShowPastedOnly = ShowPastedOnly,
            LastUseRegexSearch = UseRegexSearch,
            LastCaseSensitiveSearch = CaseSensitiveSearch,
            LastUseWildcardSearch = UseWildcardSearch,
            LastWholeWordSearch = WholeWordSearch,
            LastUseFuzzyClipSearch = UseFuzzyClipSearch,
            LastUseSemanticClipSearch = UseSemanticClipSearch,
            LastContentTypeFilter = null,
            LastContentTypeFilters = _selectedContentTypes.ToArray(),
        };

    private Task PersistCurrentFilterStateAsync()
        => _settingsService.SaveAsync(BuildPersistedFilterSettings());

    private void FlushCurrentFilterState()
    {
        _ = FlushCurrentFilterStateSafeAsync();
    }

    private async Task FlushCurrentFilterStateSafeAsync()
    {
        try
        {
            await PersistCurrentFilterStateAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Filter state flush failed: {ex.Message}");
        }
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
            var searches = await Task.Run(() => _searchHistoryService.GetRecentSearchesAsync());
            RecentSearches.Clear();
            foreach (var search in searches)
            {
                RecentSearches.Add(search);
            }
            RefreshFilteredRecentSearches();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to load search history: {ex.Message}");
        }
    }

    public void SetSearchBoxFocused(bool isFocused)
    {
        if (_isSearchBoxFocused == isFocused)
        {
            return;
        }

        _isSearchBoxFocused = isFocused;
        RefreshFilteredRecentSearches();
    }

    public void ApplySearchSuggestion(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return;
        }

        _isNavigatingSearchHistory = true;
        try
        {
            SearchText = suggestion;
        }
        finally
        {
            _isNavigatingSearchHistory = false;
        }

        _recentSearchNavigationIndex = RecentSearches.IndexOf(suggestion);
        SetSearchBoxFocused(false);
    }

    private void RefreshFilteredRecentSearches()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        FilteredRecentSearches.Clear();
        if (!string.IsNullOrEmpty(query))
        {
            var matches = RecentSearches
                .Where(search => search.Contains(query, StringComparison.OrdinalIgnoreCase));
            foreach (var match in matches.Take(8))
            {
                FilteredRecentSearches.Add(match);
            }
        }

        this.RaisePropertyChanged(nameof(IsSearchSuggestionsOpen));
    }

    public async Task NavigateSearchHistoryAsync(int delta)
    {
        if (RecentSearches.Count == 0)
        {
            await LoadRecentSearchesAsync();
        }

        if (RecentSearches.Count == 0)
        {
            return;
        }

        if (_recentSearchNavigationIndex == 0 && delta < 0)
        {
            _recentSearchNavigationIndex = -1;
            await ClearSearchFilterAsync(forceRefresh: true);
            return;
        }

        if (_recentSearchNavigationIndex == RecentSearches.Count - 1 && delta > 0)
        {
            _recentSearchNavigationIndex = -1;
            await ClearSearchFilterAsync(forceRefresh: true);
            return;
        }

        var next = _recentSearchNavigationIndex < 0
            ? (delta < 0 ? 0 : RecentSearches.Count - 1)
            : Math.Clamp(_recentSearchNavigationIndex + delta, 0, RecentSearches.Count - 1);

        _recentSearchNavigationIndex = next;
        _isNavigatingSearchHistory = true;
        try
        {
            SearchText = RecentSearches[next];
        }
        finally
        {
            _isNavigatingSearchHistory = false;
        }
    }

    public async Task ClearSearchFilterAsync(bool forceRefresh = false)
    {
        var hadSearchText = !string.IsNullOrEmpty(SearchText);
        _recentSearchNavigationIndex = -1;
        if (hadSearchText)
        {
            SearchText = string.Empty;
        }

        if (forceRefresh || (!hadSearchText && Clips.Count == 0))
        {
            await RefreshAsync();
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
            || TryHandleShortcut(e, _settingsService.Current.EnableTogglePastedHotkey, _settingsService.Current.TogglePastedHotkey, () => ShowPastedOnly = !ShowPastedOnly)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleFuzzyHotkey, _settingsService.Current.ToggleFuzzyHotkey, () => UseFuzzyClipSearch = !UseFuzzyClipSearch)
            || TryHandleShortcut(e, _settingsService.Current.EnableToggleSemanticHotkey, _settingsService.Current.ToggleSemanticHotkey, () => UseSemanticClipSearch = !UseSemanticClipSearch);
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
        RefreshBackups();
        IsSettingsOpen = true;
    }

    private void OpenHelp()
    {
        // Handled in the view-layer (code-behind) via an observable; the command
        // just pulses and the view listens and opens the HelpWindow.
        HelpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenAbout()
    {
        AboutRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task CheckForUpdateAsync()
    {
        StatusText = AppText.CheckingForUpdateStatus;

        try
        {
            var result = await _jobIndicator.TrackAsync(
                AppText.CheckingForUpdateStatus,
                () => _updateService.CheckForUpdatesAsync(ignoreAutoUpdateDisabled: true));
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? AppText.UpdateCheckCompleteStatus
                : result.Message;

            StatusText = message;
            if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Version))
            {
                // Mirror the background-check notification: surface explicit
                // "Restart and install" / "Install on exit" actions instead of
                // leaving the user with a plain info toast.
                _notificationService.Publish(new AppNotification
                {
                    Title = $"Clipthrough update {result.Version} ready",
                    Message = "Restart now to install, or it will be applied next time you close Clipthrough.",
                    Level = AppNotificationLevel.Information,
                    IsPersistent = true,
                    Actions = new[]
                    {
                        new AppNotificationAction
                        {
                            Label = "Restart and install",
                            ExecuteAsync = () =>
                            {
                                _updateService.ApplyDownloadedUpdateAndRestart();
                                return Task.CompletedTask;
                            },
                        },
                        new AppNotificationAction
                        {
                            Label = "Install on exit",
                            ExecuteAsync = () => Task.CompletedTask,
                        },
                    },
                });
            }
            else
            {
                _notificationService.PublishInfo(AppText.UpdateCheckCompleteTitle, message);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update check failed: {ex}");
            StatusText = AppText.FormatUpdateCheckFailed(ex.Message);
            _notificationService.PublishError(AppText.UpdateCheckFailedTitle, ex.Message);
        }
    }

    public event EventHandler? HelpRequested;

    public event EventHandler? AboutRequested;

    private void OpenAiPrompt()
    {
        OpenAiPrompt(ResolveDefaultAiPromptKind());
    }

    private void OpenAiPrompt(AiPresetKind kind)
    {
        OpenAiPrompt(kind, prefill: null);
    }

    public void OpenAiPromptWithPrefill(AiPresetKind kind, string? prefill)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenAiPrompt(kind, prefill));
    }

    private void OpenAiPrompt(AiPresetKind kind, string? prefill)
    {
        if (!_aiTransformService.IsConfigured)
        {
            ReportAiNotConfigured();
            return;
        }

        SetAiPromptKind(kind);
        AiPromptError = string.Empty;
        AiPromptInput = prefill ?? string.Empty;
        IsAiPromptOpen = true;
    }

    private void ReportAiNotConfigured()
    {
        const string body = "Enable AI in Settings → AI and pick a provider (sign in to Copilot or paste an OpenAI API key) before running AI actions.";
        System.Diagnostics.Trace.TraceError("AI action attempted while AI is not configured.");
        StatusText = "AI is not configured. " + body;
        _notificationService.PublishError("AI not configured", body);
    }

    private void CancelAiPrompt()
    {
        IsAiPromptOpen = false;
        AiPromptError = string.Empty;
    }

    private async Task CopilotSignInAsync()
    {
        if (_copilotAuthService is null)
        {
            CopilotSignInStatus = "Copilot auth service not available.";
            return;
        }

        IsCopilotSigningIn = true;
        CopilotSignInStatus = "Starting device code flow\u2026";
        CopilotUserCode = string.Empty;
        CopilotVerificationUri = string.Empty;
        try
        {
            var deviceCode = await Task.Run(() => _copilotAuthService.StartDeviceCodeFlowAsync()).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                CopilotUserCode = deviceCode.UserCode;
                CopilotVerificationUri = deviceCode.VerificationUri;
                CopilotSignInStatus = $"Enter code {deviceCode.UserCode} at {deviceCode.VerificationUri} (code copied to clipboard).";
                try
                {
                    // Auto-copy the device code so the user can paste it into
                    // the GitHub page immediately. The clipboard monitor will
                    // capture this — that's fine; it's just a 9-character
                    // public code with no security implication.
                    _clipboardMonitorService.SuppressNext();
                    await _systemInteractionService.CopyTextAsync(deviceCode.UserCode);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Copilot sign-in: failed to auto-copy device code: {ex.Message}");
                }
                await _systemInteractionService.OpenUrlAsync(deviceCode.VerificationUri);
            });

            var success = await Task.Run(() => _copilotAuthService.PollForAuthorizationAsync(deviceCode)).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CopilotUserCode = string.Empty;
                CopilotVerificationUri = string.Empty;
                if (success)
                {
                    CopilotSignInStatus = "Signed in to GitHub Copilot.";
                    this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
                }
                else
                {
                    CopilotSignInStatus = "Sign-in expired or denied.";
                }
            });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copilot sign-in failed: {ex}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CopilotUserCode = string.Empty;
                CopilotVerificationUri = string.Empty;
                CopilotSignInStatus = $"Sign-in failed: {ex.Message}";
            });
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCopilotSigningIn = false);
        }
    }

    private async Task CopyCopilotUserCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(CopilotUserCode))
        {
            return;
        }

        try
        {
            _clipboardMonitorService.SuppressNext();
            await _systemInteractionService.CopyTextAsync(CopilotUserCode);
            CopilotSignInStatus = $"Code {CopilotUserCode} copied. Paste it at {CopilotVerificationUri}.";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy Copilot device code failed: {ex.Message}");
            CopilotSignInStatus = $"Could not copy code: {ex.Message}";
        }
    }

    private void CopilotSignOut()
    {
        _copilotAuthService?.SignOut();
        CopilotUserCode = string.Empty;
        CopilotVerificationUri = string.Empty;
        CopilotSignInStatus = "Signed out.";
        this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
    }

    private Task SubmitAiPromptAsync() => SubmitAiPromptAsync(transformKind: GetCustomAiTransformKind(_aiPromptKind), presetLabel: null);

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

        return _aiPromptKind switch
        {
            AiPresetKind.ImageToText => SubmitImageAiPromptAsync(prompt, transformKind, presetLabel, toImage: false),
            AiPresetKind.ImageToImage => SubmitImageAiPromptAsync(prompt, transformKind, presetLabel, toImage: true),
            _ => SubmitTextAiPromptAsync(prompt, transformKind, presetLabel),
        };
    }

    private Task SubmitTextAiPromptAsync(string prompt, string transformKind, string? presetLabel)
    {
        IsAiPromptBusy = true;

        var checkedClips = Clips.Where(static c => c.IsChecked).ToList();
        var targets = checkedClips.Count > 0
            ? checkedClips
            : SelectedClip is not null ? new List<ClipItemViewModel> { SelectedClip } : new List<ClipItemViewModel>();

        targets = targets
            .Where(static t => t.CanTransform)
            .ToList();

        if (targets.Count == 0)
        {
            IsAiPromptBusy = false;
            AiPromptError = "Select one or more text or file clips first.";
            return Task.CompletedTask;
        }

        var useSelectionSlice = checkedClips.Count == 0
            && SelectedClip is not null
            && ReferenceEquals(targets[0], SelectedClip)
            && EditedClipSelectionLength > 0
            && EditedClipSelectionStart >= 0
            && EditedClipSelectionStart + EditedClipSelectionLength <= (EditedClipText?.Length ?? 0)
            && SelectedClip.CanTransform;
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

    private Task SubmitImageAiPromptAsync(string prompt, string transformKind, string? presetLabel, bool toImage)
    {
        IsAiPromptBusy = true;

        var clip = GetEffectiveSelectedClip();
        if (clip is null || clip.Clip.ContentType != ContentType.Image || clip.Clip.ContentBytes is not { Length: > 0 } imageBytes)
        {
            IsAiPromptBusy = false;
            AiPromptError = "Select an image clip first.";
            return Task.CompletedTask;
        }

        IsAiPromptOpen = false;
        AiPromptError = string.Empty;
        AiPromptInput = string.Empty;

        QueueImageAiTransform(
            prompt,
            clip,
            imageBytes,
            ResolveImageMediaType(clip.Clip.ContentFormat),
            transformKind,
            presetLabel,
            toImage);

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
        try
        {
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
                        source = GetTransformSourceText(target);
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
                var captured = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
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
                    SkipPostInsertMaintenance = true,
                }));
                if (captured is not null)
                {
                    lastCreatedId = captured.Id;
                }
                produced++;
            }

            if (failure is not null)
            {
                var title = presetLabel is null ? "AI transform failed" : $"AI preset '{presetLabel}' failed";
                System.Diagnostics.Trace.TraceError($"{title}: {failure}");
                _notificationService.PublishError(title, failure.Message);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusText = failure.Message);
                return;
            }

            if (produced > 0)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText = produced == 1
                        ? "AI transform produced a new clip."
                        : $"AI transform produced {produced} new clips.";
                });
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await RefreshAsync(lastCreatedId));
            }
            else
            {
                var message = "AI transform returned no new content. Check the provider or refine the prompt.";
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusText = message);
                _notificationService.PublishWarning(
                    presetLabel is null ? "AI transform" : $"AI preset '{presetLabel}'",
                    message);
            }
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsAiPromptBusy = false);
        }
    }

    private void QueueImageAiTransform(
        string prompt,
        ClipItemViewModel clip,
        byte[] imageBytes,
        string mediaType,
        string transformKind,
        string? presetLabel,
        bool toImage)
    {
        var label = presetLabel is null
            ? $"AI: {Shorten(prompt, 40)}"
            : $"AI: {presetLabel}";

        StatusText = presetLabel is null
            ? (toImage ? "AI image edit running…" : "AI image prompt running…")
            : $"Running AI preset '{presetLabel}'…";

        _ = _jobIndicator.TrackAsync(label, () => RunImagePromptAsync(
            prompt,
            clip,
            imageBytes,
            mediaType,
            transformKind,
            presetLabel,
            toImage));
    }

    private async Task RunImagePromptAsync(
        string prompt,
        ClipItemViewModel clip,
        byte[] imageBytes,
        string mediaType,
        string transformKind,
        string? presetLabel,
        bool toImage)
    {
        var actionLabel = presetLabel is null
            ? (toImage ? "AI image edit" : "AI image prompt")
            : $"AI preset '{presetLabel}'";

        try
        {
            if (toImage)
            {
                var result = await _aiTransformService.EditImageAsync(prompt, imageBytes, mediaType).ConfigureAwait(false);
                if (result is not { Length: > 0 })
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusText = "AI image edit returned no data.");
                    return;
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await CopyEditedImageAsync(result));
                return;
            }

            var text = await _aiTransformService.DescribeImageAsync(prompt, imageBytes, mediaType).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusText = "AI image description was empty.");
                return;
            }

            var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            var captured = await Task.Run(() => _clipStoreService.CaptureAsync(new ClipCaptureRequest
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
                TransformKind = transformKind,
                SkipPostInsertMaintenance = true,
            }));

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = presetLabel is null
                    ? "AI image prompt produced a new clip."
                    : $"AI preset '{presetLabel}' produced a new clip.";
            });
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await RefreshAsync(captured?.Id));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"{actionLabel} failed: {ex}");
            _notificationService.PublishError($"{actionLabel} failed", ex.Message);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusText = ex.Message);
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsAiPromptBusy = false);
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

        // Pause background workers to avoid write contention during bulk import.
        await _backgroundOcrQueue.StopAsync();
        if (_embeddingWorker is not null)
            await _embeddingWorker.StopAsync();

        try
        {
            var progress = new Progress<ClipAngelImportProgress>(p =>
            {
                ClipAngelImportProcessed = p.Processed;
                ClipAngelImportTotal = p.Total;
                StatusText = AppText.FormatClipAngelImportProgress(p.Processed, p.Total);
            });
            var result = await Task.Run(() => _clipAngelImportService.ImportAsync(path!, progress));
            var msg = AppText.FormatClipAngelImportSuccess(result.Imported, result.Skipped, result.Failed);
            StatusText = msg;
            _notificationService.PublishInfo(AppText.SettingsClipAngelImportTitle, msg);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"ClipAngel import failed: {ex}");
            StatusText = AppText.FormatClipAngelImportError(ex.Message);
            _notificationService.PublishError(AppText.SettingsClipAngelImportTitle, AppText.FormatClipAngelImportError(ex.Message));
        }
        finally
        {
            // Resume background workers.
            _backgroundOcrQueue.Start();
            _embeddingWorker?.Start();

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
            new HotkeyDraft(nameof(AppSettings.ToggleFuzzyHotkey), SettingsEnableToggleFuzzyHotkey, SettingsToggleFuzzyHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleSemanticHotkey), SettingsEnableToggleSemanticHotkey, SettingsToggleSemanticHotkey),
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

        if (!string.IsNullOrEmpty(SettingsDatabasePassword)
            && !string.Equals(SettingsDatabasePassword, SettingsDatabasePasswordConfirm, StringComparison.Ordinal))
        {
            StatusText = AppText.SettingsPasswordMismatch;
            return;
        }

        StorageOptions storageOptions;
        try
        {
            storageOptions = new StorageOptions
            {
                DatabasePath = SettingsDatabasePath,
                DatabasePassword = SettingsDatabasePassword,
                RememberPassword = SettingsRememberDatabasePassword,
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
            EnableToggleFuzzyHotkey = SettingsEnableToggleFuzzyHotkey,
            ToggleFuzzyHotkey = normalizedHotkeys[nameof(AppSettings.ToggleFuzzyHotkey)],
            EnableToggleSemanticHotkey = SettingsEnableToggleSemanticHotkey,
            ToggleSemanticHotkey = normalizedHotkeys[nameof(AppSettings.ToggleSemanticHotkey)],
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
            ExternalImageEditorPath = SettingsExternalImageEditorPath.Trim(),
            ExternalDiffToolPath = SettingsExternalDiffToolPath.Trim(),
            EnableAi = SettingsEnableAi,
            AiProvider = SettingsAiProvider,
            AiBaseUrl = (SettingsAiBaseUrl ?? string.Empty).Trim(),
            AiApiKey = (SettingsAiApiKey ?? string.Empty).Trim(),
            AiModel = (SettingsAiModel ?? string.Empty).Trim(),
            AiImageModel = (SettingsAiImageModel ?? string.Empty).Trim(),
            AiReasoningEffort = (SettingsAiReasoningEffort ?? string.Empty).Trim(),
            EnableAutoUpdate = SettingsEnableAutoUpdate,
            AutoApplyUpdatesOnStartup = SettingsAutoApplyUpdatesOnStartup,
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
            EnableSemanticSearch = SettingsEnableSemanticSearch,
            UseSemanticClipSearch = UseSemanticClipSearch,
            UseFuzzySettingsSearch = SettingsUseFuzzySearch,
        };

        await Task.Run(async () =>
        {
            await _storageOptionsService.SaveAsync(storageOptions).ConfigureAwait(false);
            await _settingsService.SaveAsync(settings).ConfigureAwait(false);
        });
        if (!_isDatabaseReady)
        {
            IsLoadingDatabase = true;
            StatusText = "Loading clipboard library\u2026";
            _ = StartDatabaseInBackgroundAsync();
            return;
        }

        await Task.Run(async () =>
        {
            var existingRules = await _sensitivityService.GetRulesAsync().ConfigureAwait(false);
            await _sensitivityService.SaveRulesAsync(sensitivityRules).ConfigureAwait(false);

            if (SensitivityRulesChanged(existingRules, sensitivityRules))
            {
                await _clipStoreService.RebuildSensitivityMatchesAsync().ConfigureAwait(false);
            }
        });
        _ = ApplyMaintenanceAndRefreshAsync();

        if (_isDatabaseReady && _ocrService.IsAvailable)
        {
            var nowAutoOcr = settings.AutoOcrImageClips;
            var nowOcrLanguages = (settings.OcrLanguages ?? string.Empty).Trim();
            var languagesChanged = !string.Equals(nowOcrLanguages, previousOcrLanguages, StringComparison.OrdinalIgnoreCase);
            if (nowAutoOcr && (!previousAutoOcr || languagesChanged))
            {
                if (languagesChanged)
                {
                    await Task.Run(() => _clipStoreService.MarkAllSucceededForRerunAsync());
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
        SettingsDatabasePasswordConfirm = _storageOptionsService.Current.DatabasePassword;
        SettingsRememberDatabasePassword = _storageOptionsService.Current.RememberPassword;
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
        SettingsEnableToggleFuzzyHotkey = settings.EnableToggleFuzzyHotkey;
        SettingsToggleFuzzyHotkey = settings.ToggleFuzzyHotkey;
        SettingsEnableToggleSemanticHotkey = settings.EnableToggleSemanticHotkey;
        SettingsToggleSemanticHotkey = settings.ToggleSemanticHotkey;
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
        SettingsExternalImageEditorPath = settings.ExternalImageEditorPath;
        SettingsExternalDiffToolPath = settings.ExternalDiffToolPath;
        SettingsEnableAi = settings.EnableAi;
        this.RaisePropertyChanged(nameof(IsAiMenuVisible));
        SettingsAiProvider = settings.AiProvider;
        SettingsAiBaseUrl = settings.AiBaseUrl;
        SettingsAiApiKey = settings.AiApiKey;
        SettingsAiModel = settings.AiModel;
        SettingsAiImageModel = settings.AiImageModel;
        SettingsAiReasoningEffort = settings.AiReasoningEffort;
        SettingsEnableAutoUpdate = settings.EnableAutoUpdate;
        SettingsAutoApplyUpdatesOnStartup = settings.AutoApplyUpdatesOnStartup;
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
        SettingsEnableSemanticSearch = settings.EnableSemanticSearch;
        IsDatabasePasswordVisible = false;
        RebuildCustomHotkeyTargetSuggestions();
    }

    private void OnCopilotSignedInChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
            this.RaisePropertyChanged(nameof(IsAiMenuVisible));
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!IsSettingsOpen)
        {
            LoadSettingsDraft(settings);
        }

        SyncUserScripts(settings);
        SyncAiPresets(settings);
        RebuildCustomHotkeyTargetSuggestions();
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
        this.RaisePropertyChanged(nameof(FuzzyFilterTooltip));
        this.RaisePropertyChanged(nameof(SemanticFilterTooltip));
        this.RaisePropertyChanged(nameof(IsSemanticSearchEnabled));
        this.RaisePropertyChanged(nameof(IsAiMenuVisible));
    }

    private void SyncUserScripts(AppSettings settings)
    {
        UserScripts.Clear();
        foreach (var s in settings.UserScripts)
        {
            UserScripts.Add(s);
        }

        RefreshVisibleTransformMenus();
    }

    private void RebuildCustomHotkeyTargetSuggestions()
    {
        var suggestions = new List<string>();

        foreach (var tx in Enum.GetValues<TextTransformation>())
        {
            if (tx == TextTransformation.None) continue;
            suggestions.Add($"builtin:{tx}");
        }

        foreach (var s in SettingsUserScriptDrafts)
        {
            if (!string.IsNullOrWhiteSpace(s.Name))
                suggestions.Add($"script:{s.Name}");
        }

        foreach (var p in AiPresets)
        {
            if (!string.IsNullOrWhiteSpace(p.Name))
                suggestions.Add($"ai:{p.Name}");
        }

        suggestions.Add("prompt:");
        suggestions.Add("aiprompt:auto");
        suggestions.Add("aiprompt:text");
        suggestions.Add("aiprompt:image-to-text");
        suggestions.Add("aiprompt:image-to-image");

        CustomHotkeyTargetSuggestions = suggestions;
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
        AiMenuEntries.Add(new AiMenuEntry("Image → Text · Custom prompt…", null, true, AiPresetKind.ImageToText));
        AiMenuEntries.Add(new AiMenuEntry("Image → Image · Custom prompt…", null, true, AiPresetKind.ImageToImage));
        foreach (var p in settings.AiPresets)
        {
            var label = p.Kind switch
            {
                AiPresetKind.ImageToText => $"Image → Text · {p.Name}",
                AiPresetKind.ImageToImage => $"Image → Image · {p.Name}",
                _ => p.Name,
            };
            AiMenuEntries.Add(new AiMenuEntry(label, p, false, p.Kind));
        }

        RefreshVisibleTransformMenus();
    }

    private async Task InvokeAiMenuEntryAsync(AiMenuEntry? entry)
    {
        if (entry is null) return;
        if (entry.IsCustomPrompt)
        {
            OpenAiPrompt(entry.Kind);
            return;
        }
        if (entry.Preset is { } preset)
        {
            await ApplyAiPresetAsync(preset).ConfigureAwait(false);
        }
    }

    private async Task StartDatabaseAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _databaseInitializer.InitializeAsync();
        Trace.TraceInformation($"[startup-timing] DatabaseInitializer @ {sw.ElapsedMilliseconds}ms");

        if (!_settingsService.HasSavedSettings)
        {
            await _settingsService.InitializeAsync();
        }

        await EnsureDefaultScriptsLoadedAsync();

        await LoadSensitivityRulesAsync();
        Trace.TraceInformation($"[startup-timing] sensitivity rules loaded @ {sw.ElapsedMilliseconds}ms");

        // Prewarm: run a tiny FTS-touching query so the OS file cache, the
        // SQLCipher key derivation and the FTS5 index header are all hot by
        // the time the visible refresh runs. Without this, the first
        // SearchAsync after startup paid all three costs on the UI thread.
        await Task.Run(async () =>
        {
            try
            {
                await _clipStoreService.PrewarmAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Database prewarm failed: {ex.Message}");
            }
        }).ConfigureAwait(false);
        Trace.TraceInformation($"[startup-timing] prewarm @ {sw.ElapsedMilliseconds}ms");

        _isDatabaseReady = true;

        // Take a daily backup once the database has been opened, integrity-
        // checked, and migrated. Fire-and-forget so a slow file-system copy
        // never delays the first refresh.
        _ = Task.Run(async () =>
        {
            try
            {
                await _databaseBackupService.EnsureDailyBackupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Daily backup failed: {ex.Message}");
            }
        });
    }

    private void StartBackgroundServices()
    {
        if (_areBackgroundServicesStarted)
        {
            return;
        }

        _areBackgroundServicesStarted = true;
        _clipboardMonitorService.Start();
        _backgroundOcrQueue.Start();
        _ = Task.Run(() => _backgroundOcrQueue.EnqueueBacklogAsync());
        if (_embeddingWorker is not null && _settingsService.Current.EnableSemanticSearch)
        {
            _embeddingWorker.Start();
        }
        StartMaintenanceLoop();
        _ = RefreshSemanticCoverageAsync();
        _ = RefreshOcrCoverageAsync();
        _subscriptions.Add(_backgroundOcrQueue.QueueChanged
            .Throttle(TimeSpan.FromMilliseconds(150), RxSchedulers.TaskpoolScheduler)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(__ =>
            {
                _ = RefreshOcrCoverageAsync();
            }));
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
            ReportAiNotConfigured();
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

        QueueImageAiTransform(
            preset.Prompt,
            clip,
            imageBytes,
            ResolveImageMediaType(clip.Clip.ContentFormat),
            $"ai:{preset.Name}",
            preset.Name,
            toImage);
    }

    private static string ResolveImageMediaType(ClipContentFormat format) => "image/png";

    private AiPresetKind ResolveDefaultAiPromptKind()
    {
        var targets = GetCheckedOrSelectedClips();
        if (targets.Any(static clip => clip.CanTransform))
        {
            return AiPresetKind.TextToText;
        }

        return targets.Any(static clip => clip.IsImageClip && clip.Clip.ContentBytes is { Length: > 0 })
            ? AiPresetKind.ImageToText
            : AiPresetKind.TextToText;
    }

    private void SetAiPromptKind(AiPresetKind kind)
    {
        if (_aiPromptKind == kind)
        {
            return;
        }

        _aiPromptKind = kind;
        this.RaisePropertyChanged(nameof(AiPromptTitle));
        this.RaisePropertyChanged(nameof(AiPromptDescription));
        this.RaisePropertyChanged(nameof(AiPromptPlaceholder));
        this.RaisePropertyChanged(nameof(AiPromptApplyLabel));
    }

    private static string GetCustomAiTransformKind(AiPresetKind kind) => kind switch
    {
        AiPresetKind.ImageToText => "ai:image-to-text:custom",
        AiPresetKind.ImageToImage => "ai:image-to-image:custom",
        _ => "ai:custom",
    };

    private string ResolveExternalEditorPath(ClipItemViewModel clip)
    {
        var settings = _settingsService.Current;
        if (clip.Clip.ContentType == ContentType.Image && !string.IsNullOrWhiteSpace(settings.ExternalImageEditorPath))
        {
            return settings.ExternalImageEditorPath;
        }

        return settings.ExternalEditorPath;
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

            // Close the prompt immediately and show loading state.
            IsPasswordPromptOpen = false;
            IsLoadingDatabase = true;
            StatusText = "Loading clipboard library\u2026";

            _ = StartDatabaseInBackgroundAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Password unlock failed: {ex}");
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

    private async Task CheckForUpdatesNowAsync()
    {
        if (_updateService is null)
        {
            StatusText = "Updates are not available in this build.";
            return;
        }

        StatusText = "Checking for updates\u2026";
        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Version))
            {
                StatusText = $"Update {result.Version} downloaded. Restart Clipthrough to install.";
                _notificationService.Publish(new AppNotification
                {
                    Title = $"Clipthrough update {result.Version} ready",
                    Message = "Restart now to install, or it will be applied next time you close Clipthrough.",
                    Level = AppNotificationLevel.Information,
                    IsPersistent = true,
                    Actions = new[]
                    {
                        new AppNotificationAction
                        {
                            Label = "Restart and install",
                            ExecuteAsync = () =>
                            {
                                _updateService.ApplyDownloadedUpdateAndRestart();
                                return Task.CompletedTask;
                            },
                        },
                        new AppNotificationAction
                        {
                            Label = "Install on exit",
                            ExecuteAsync = () => Task.CompletedTask,
                        },
                    },
                });
            }
            else
            {
                StatusText = string.IsNullOrWhiteSpace(result.Message)
                    ? "Clipthrough is up to date."
                    : result.Message!;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Manual update check failed: {ex}");
            StatusText = AppText.FormatErrorStatus(ex.Message);
        }
    }

    private void RestartAndInstallUpdate()
    {
        if (_updateService is null)
        {
            StatusText = "Updates are not available in this build.";
            return;
        }

        if (!_updateService.ApplyDownloadedUpdateAndRestart())
        {
            StatusText = "No downloaded update is waiting to install. Check for updates first.";
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
            ? await Task.Run(async () => await _sensitivityService.GetRulesAsync().ConfigureAwait(false)).ConfigureAwait(false)
            : _sensitivityService.GetDefaultRules();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ReplaceSensitivityRules(rules));
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

    private static bool SensitivityRulesChanged(IReadOnlyList<SensitivityRule> existing, IReadOnlyList<SensitivityRule> incoming)
    {
        if (existing.Count != incoming.Count)
        {
            return true;
        }

        for (int i = 0; i < existing.Count; i++)
        {
            var a = existing[i];
            var b = incoming[i];
            if (a.Name != b.Name || a.Pattern != b.Pattern || a.Severity != b.Severity || a.IsEnabled != b.IsEnabled)
            {
                return true;
            }
        }

        return false;
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

        var maintenanceResult = await Task.Run(async () => await _clipStoreService.ApplyMaintenanceAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
            .Subscribe(_ => { }, ex => ReportError("Maintenance loop", ex));
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
        this.RaisePropertyChanged(nameof(ShowSelectedFilesTextual));
        this.RaisePropertyChanged(nameof(ShowSelectedFilesTextualFallback));
        this.RaisePropertyChanged(nameof(ShowSelectedImageRenderer));
        this.RaisePropertyChanged(nameof(ShowSelectedImagePreview));
        this.RaisePropertyChanged(nameof(ShowSelectedImageEditor));
        this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
        this.RaisePropertyChanged(nameof(ShowSelectedImageOcrText));
        this.RaisePropertyChanged(nameof(ShowSelectedImageOcrTextBox));
        this.RaisePropertyChanged(nameof(ShowSelectedImageOcrEmptyState));
        this.RaisePropertyChanged(nameof(ShowSelectedImageOcrBusy));
        this.RaisePropertyChanged(nameof(CanRunOcrOnEmptyState));
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
        this.RaisePropertyChanged(nameof(CanEditSelectedRichTextInRenderedMode));
        this.RaisePropertyChanged(nameof(SelectedClipTextIsReadOnly));
        this.RaisePropertyChanged(nameof(SelectedClipRenderedContentIsReadOnly));
        this.RaisePropertyChanged(nameof(RawContentSyntaxHint));
        this.RaisePropertyChanged(nameof(IsRenderedMode));
        this.RaisePropertyChanged(nameof(IsTextualMode));
        this.RaisePropertyChanged(nameof(IsRawMode));
    }

    private static ContentDisplayMode NormalizeContentDisplayMode(ContentDisplayMode mode)
        => mode == ContentDisplayMode.WebView
            ? ContentDisplayMode.Rendered
            : mode;

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
        this.RaisePropertyChanged(nameof(HasTextTransformTarget));
        this.RaisePropertyChanged(nameof(HasImageTransformTarget));
        RefreshVisibleTransformMenus();
    }

    private void RefreshVisibleTransformMenus()
    {
        var targets = GetCheckedOrSelectedClips();
        var hasTextTargets = targets.Any(static clip => clip.CanTransform);
        var hasImageTargets = _aiTransformService.IsConfigured
                              && targets.Any(static clip => clip.IsImageClip && clip.Clip.ContentBytes is { Length: > 0 });

        ReplaceVisibleCollection(
            VisibleUserScripts,
            hasTextTargets ? UserScripts : []);

        ReplaceVisibleCollection(
            VisibleAiMenuEntries,
            AiMenuEntries.Where(entry =>
                (entry.Kind == AiPresetKind.TextToText && hasTextTargets)
                || (entry.Kind is AiPresetKind.ImageToText or AiPresetKind.ImageToImage && hasImageTargets)));
        this.RaisePropertyChanged(nameof(IsAiMenuVisible));
        this.RaisePropertyChanged(nameof(HasVisibleUserScripts));
    }

    private static void ReplaceVisibleCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
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
                        await Task.Run(() => _clipStoreService.DeleteAsync(clip.Id));
                        await RefreshAsync();
                    }
                },
                new AppNotificationAction
                {
                    Label = AppText.UnmarkSensitiveButtonLabel,
                    ExecuteAsync = async () =>
                    {
                        await Task.Run(() => _clipStoreService.ClearSensitivityAsync(clip.Id));
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

    /// <summary>
    /// Build a drag-and-drop payload for the currently checked clips (falling
    /// back to <see cref="SelectedClip"/> when nothing is checked), so the
    /// view can hand it to <c>DragDrop.DoDragDropAsync</c>.
    /// </summary>
    public async Task<Avalonia.Input.IDataTransfer?> BuildDragPayloadForCurrentSelectionAsync(Avalonia.Platform.Storage.IStorageProvider storageProvider)
    {
        var clips = GetCheckedOrSelectedClips();
        if (clips.Length == 0)
        {
            return null;
        }

        var entries = clips.Select(static c => c.Clip).ToArray();
        return await _dragDropService.BuildDragPayloadAsync(entries, storageProvider);
    }

    /// <summary>
    /// Import the contents of an incoming drop as new clips. Each accepted
    /// payload is routed through <see cref="IClipStoreService.CaptureFastAsync"/>
    /// and emitted on the captured-clips stream so the UI sees it instantly.
    /// Returns the number of clips imported.
    /// </summary>
    public async Task<int> ImportDroppedDataAsync(Avalonia.Input.IDataTransfer drop, ClipboardSourceApplicationInfo? sourceInfo)
    {
        if (drop is null)
        {
            return 0;
        }

        IReadOnlyList<ClipCaptureRequest> requests;
        try
        {
            requests = await _dragDropService.TryBuildCaptureRequestsAsync(drop, sourceInfo);
        }
        catch (Exception ex)
        {
            ReportError("Drag-import parse", ex);
            return 0;
        }

        if (requests.Count == 0)
        {
            return 0;
        }

        var imported = 0;
        foreach (var request in requests)
        {
            try
            {
                var clip = await Task.Run(() => _clipStoreService.CaptureFastAsync(request));
                if (clip is null)
                {
                    continue;
                }

                ApplyCapturedClipOptimistically(clip);
                imported++;
            }
            catch (Exception ex)
            {
                ReportError("Drag-import capture", ex);
            }
        }

        if (imported > 0)
        {
            _notificationService.PublishInfo(AppText.ClipDragImportTitle, AppText.FormatClipDragImportSummary(imported));
        }

        return imported;
    }

    private readonly record struct HotkeyDraft(string Name, bool IsEnabled, string HotkeyText);

    private readonly record struct RefreshRequest(ClipSearchFilters Filters, bool UseSemanticClipSearch);

}
