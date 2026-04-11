using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    private readonly IClipStoreService _clipStoreService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly CompositeDisposable _subscriptions = new();

    private string _searchText = string.Empty;
    private ContentTypeOption _selectedContentTypeOption = new(null);
    private bool _showFavoritesOnly;
    private bool _showSensitiveOnly;
    private bool _useRegexSearch;
    private bool _caseSensitiveSearch;
    private ClipItemViewModel? _selectedClip;
    private ClipFileItemViewModel? _selectedFileItem;
    private bool _hasMoreResults;
    private bool _isBusy;
    private string _statusText = AppText.LoadingStatus;
    private int _currentOffset;
    private int _matchingClipCount;
    private int _totalClipCount;
    private int _sensitiveClipCount;
    private string _lastCaptureSummary = AppText.WaitingForFirstCapture;
    private bool _showRawContent;
    private string _selectedClipRenderedText = AppText.PreviewSelectContent;
    private Bitmap? _selectedClipImagePreview;
    private string _selectedClipImageHint = AppText.PreviewSelectImage;
    private bool _isStartupInProgress;
    private bool _isDatabaseReady;
    private bool _isStarted;
    private bool _isSettingsOpen;
    private string _settingsToggleRegexHotkey = AppSettings.Default.ToggleRegexHotkey;
    private string _settingsToggleFavoritesHotkey = AppSettings.Default.ToggleFavoritesHotkey;
    private string _settingsToggleSensitiveHotkey = AppSettings.Default.ToggleSensitiveHotkey;
    private string _settingsToggleCaseSensitiveHotkey = AppSettings.Default.ToggleCaseSensitiveHotkey;
    private string _settingsToggleWindowHotkey = AppSettings.Default.ToggleWindowHotkey;
    private string _settingsMaxClipSizeKilobytes = (AppSettings.Default.MaxClipSizeBytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture);

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService, ISettingsService settingsService, ISystemInteractionService systemInteractionService, DatabaseInitializer databaseInitializer)
    {
        _clipStoreService = clipStoreService;
        _clipboardMonitorService = clipboardMonitorService;
        _settingsService = settingsService;
        _systemInteractionService = systemInteractionService;
        _databaseInitializer = databaseInitializer;
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
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasSelection);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);
        OpenSettingsCommand = ReactiveCommand.Create(OpenSettings);
        CloseSettingsCommand = ReactiveCommand.Create(CloseSettings);
        SaveSettingsCommand = ReactiveCommand.CreateFromTask(SaveSettingsAsync);

        _settingsService.SettingsChanged += OnSettingsChanged;

        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.SearchText,
                    x => x.SelectedContentTypeOption,
                    x => x.ShowFavoritesOnly,
                    x => x.ShowSensitiveOnly,
                    x => x.UseRegexSearch,
                    x => x.CaseSensitiveSearch)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .Select(static _ => Unit.Default)
                .InvokeCommand(RefreshCommand));

        _subscriptions.Add(
            _clipboardMonitorService.CapturedClips
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(static _ => Unit.Default)
                .InvokeCommand(RefreshCommand));

        _subscriptions.Add(
            RefreshCommand.ThrownExceptions
                .Merge(LoadMoreCommand.ThrownExceptions)
                .Merge(ToggleFavoriteCommand.ThrownExceptions)
                .Merge(CopySelectedCommand.ThrownExceptions)
                .Merge(DeleteSelectedCommand.ThrownExceptions)
                .Merge(SaveSettingsCommand.ThrownExceptions)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(ex => StatusText = AppText.FormatErrorStatus(ex.Message)));
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public ObservableCollection<ClipFileItemViewModel> SelectedClipFiles { get; } = [];

    public IReadOnlyList<ContentTypeOption> ContentTypeOptions { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }

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

    public bool ShowRawContent
    {
        get => _showRawContent;
        set
        {
            if (_showRawContent == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _showRawContent, value);
            RaiseRenderModeProperties();
        }
    }

    public ClipItemViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (_selectedClip == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedClip, value);
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
        private set => this.RaiseAndSetIfChanged(ref _totalClipCount, value);
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

    public string CopyButtonLabel => AppText.CopyButtonLabel;

    public string DeleteButtonLabel => AppText.DeleteButtonLabel;

    public string FavoriteBadgeLabel => AppText.FavoriteBadgeLabel;

    public string FavoriteButtonLabel => AppText.FavoriteButtonLabel;

    public string CaseSensitiveFilterLabel => AppText.CaseSensitiveFilterLabel;

    public string SettingsButtonLabel => AppText.SettingsButtonLabel;

    public string SettingsTitleText => AppText.SettingsTitleText;

    public string SettingsDescriptionText => AppText.SettingsDescriptionText;

    public string SettingsLocalHotkeysTitle => AppText.SettingsLocalHotkeysTitle;

    public string SettingsGlobalHotkeyTitle => AppText.SettingsGlobalHotkeyTitle;

    public string SettingsClipLimitLabel => AppText.SettingsClipLimitLabel;

    public string SettingsRegexHotkeyLabel => AppText.SettingsRegexHotkeyLabel;

    public string SettingsFavoritesHotkeyLabel => AppText.SettingsFavoritesHotkeyLabel;

    public string SettingsSensitiveHotkeyLabel => AppText.SettingsSensitiveHotkeyLabel;

    public string SettingsCaseSensitiveHotkeyLabel => AppText.SettingsCaseSensitiveHotkeyLabel;

    public string SettingsToggleWindowHotkeyLabel => AppText.SettingsToggleWindowHotkeyLabel;

    public string SettingsSaveButtonLabel => AppText.SettingsSaveButtonLabel;

    public string SettingsCancelButtonLabel => AppText.SettingsCancelButtonLabel;

    public string SettingsHintText => AppText.SettingsHintText;

    public string EmptySelectionTitleText => AppText.EmptySelectionTitle;

    public string EmptySelectionDescriptionText => AppText.EmptySelectionDescription;

    public string SelectedImageTypeText => AppText.ImageClipTitle;

    public string SourceLabelText => AppText.SourceLabel;

    public string FirstCopiedLabelText => AppText.FirstCopiedLabel;

    public string CapturedLabelText => AppText.CapturedLabel;

    public string CopiesLabelText => AppText.CopiesLabel;

    public string SizeLabelText => AppText.SizeLabel;

    public string SensitivityLabelText => AppText.SensitivityLabel;

    public string MatchingClipCountText => AppText.FormatMatchingCount(MatchingClipCount);

    public string SensitiveClipCountText => AppText.FormatSensitiveCount(SensitiveClipCount);

    public bool IsSelectedClipFavorite => SelectedClip?.IsFavorite == true;

    public bool HasSelectedClip => SelectedClip is not null;

    public bool ShowEmptySelectionState => !HasSelectedClip;

    public bool ShowRenderedContent => HasSelectedClip && !ShowRawContent;

    public bool ShowRawTextContent => HasSelectedClip && ShowRawContent;

    public bool ShowSelectedTextRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Text;

    public bool ShowSelectedRichTextRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.RichText;

    public bool ShowSelectedFilesRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Files && HasSelectedClipFileItems;

    public bool ShowSelectedFilesFallback => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Files && !HasSelectedClipFileItems;

    public bool ShowSelectedImageRenderer => ShowRenderedContent && SelectedClip?.Clip.ContentType == ContentType.Image;

    public bool ShowSelectedImagePreview => ShowSelectedImageRenderer && SelectedClipImagePreview is not null;

    public bool ShowSelectedImagePlaceholder => ShowSelectedImageRenderer && SelectedClipImagePreview is null;

    public bool HasSelectedClipFileItems => SelectedClipFiles.Count > 0;

    public string SelectedClipRenderedText => _selectedClipRenderedText;

    public string SelectedClipRawContent => SelectedClip?.FullContent ?? AppText.PreviewSelectRawContent;

    public Bitmap? SelectedClipImagePreview => _selectedClipImagePreview;

    public string SelectedClipImageHint => _selectedClipImageHint;

    public string SelectedClipContentTypeText => SelectedClip?.DisplayContentType ?? AppText.SelectClipTypeFallback;

    public string SelectedClipTitleText => SelectedClip?.Title ?? AppText.SelectClipTitleFallback;

    public string SelectedClipSourceText => SelectedClip?.SourceApp ?? AppText.UnknownSource;

    public string SelectedClipFirstCopiedAtText => SelectedClip?.FirstCopiedAtDisplay ?? AppText.NotAvailable;

    public string SelectedClipCapturedAtText => SelectedClip?.CapturedAtDisplay ?? AppText.NotAvailable;

    public string SelectedClipCopyCountText => SelectedClip?.CopyCountDisplay ?? AppText.NotAvailable;

    public string SelectedClipByteSizeText => SelectedClip?.ByteSizeDisplay ?? AppText.FormatByteCount(0);

    public string SelectedClipSensitivityText => SelectedClip?.SensitivitySummary ?? AppText.NoClipSelected;

    public IBrush SelectedClipAccentBrush => SelectedClip?.StateAccentBrush ?? s_defaultDetailAccentBrush;

    public IBrush SelectedClipAreaBorderBrush => SelectedClip?.RowBorderBrush ?? s_defaultDetailBorderBrush;

    public Thickness SelectedClipAreaBorderThickness => SelectedClip?.RowBorderThickness ?? new Thickness(1);

    public bool ShowSelectedClipFavoriteIndicator => SelectedClip?.IsFavorite == true;

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

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => this.RaiseAndSetIfChanged(ref _isSettingsOpen, value);
    }

    public string SettingsToggleRegexHotkey
    {
        get => _settingsToggleRegexHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleRegexHotkey, value);
    }

    public string SettingsToggleFavoritesHotkey
    {
        get => _settingsToggleFavoritesHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleFavoritesHotkey, value);
    }

    public string SettingsToggleSensitiveHotkey
    {
        get => _settingsToggleSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleSensitiveHotkey, value);
    }

    public string SettingsToggleCaseSensitiveHotkey
    {
        get => _settingsToggleCaseSensitiveHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleCaseSensitiveHotkey, value);
    }

    public string SettingsToggleWindowHotkey
    {
        get => _settingsToggleWindowHotkey;
        set => this.RaiseAndSetIfChanged(ref _settingsToggleWindowHotkey, value);
    }

    public string SettingsMaxClipSizeKilobytes
    {
        get => _settingsMaxClipSizeKilobytes;
        set => this.RaiseAndSetIfChanged(ref _settingsMaxClipSizeKilobytes, value);
    }

    public void Dispose()
    {
        _clipboardMonitorService.Stop();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        SelectedClipFiles.Clear();
        ReplaceSelectedClipImagePreview(null);
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
            await _databaseInitializer.InitializeAsync();
            await _settingsService.InitializeAsync();
            LoadSettingsDraft(_settingsService.Current);
            _isDatabaseReady = true;

            _clipboardMonitorService.Start();
            await RefreshAsync();

            await _clipStoreService.SeedSampleDataAsync();
            await RefreshAsync();

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

    private async Task RefreshAsync()
    {
        if (!_isDatabaseReady)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _clipStoreService.SearchAsync(BuildFilters(offset: 0));
            ApplyRefreshResult(result);
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
            foreach (var item in result.Items.Select(static clip => new ClipItemViewModel(clip)))
            {
                Clips.Add(item);
            }

            _currentOffset += result.Items.Count;
            HasMoreResults = Clips.Count < result.TotalMatchingCount;
            this.RaisePropertyChanged(nameof(HasNoClips));
            UpdateStatus(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        await _clipStoreService.SetFavoriteAsync(SelectedClip.Id, !SelectedClip.IsFavorite);
        await RefreshAsync();
    }

    private async Task CopySelectedAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        if (SelectedClip.Clip.ContentType == ContentType.Image)
        {
            using var bitmap = TryLoadImage(SelectedClip.FullContent);
            if (bitmap is null)
            {
                throw new InvalidOperationException("The selected image clip could not be decoded for copying.");
            }

            await _systemInteractionService.CopyBitmapAsync(bitmap);
            StatusText = AppText.CopiedImageStatus;
            return;
        }

        if (SelectedClip.Clip.ContentType == ContentType.RichText)
        {
            await _systemInteractionService.CopyRichContentAsync(SelectedClip.FullContent, SelectedClipRenderedText);
            StatusText = AppText.FormatCopiedClip(SelectedClip.DisplayContentType.ToLower(AppText.CurrentCulture));
            return;
        }

        var isFileList = SelectedClip.Clip.ContentType == ContentType.Files && SelectedClipFiles.Count > 0;
        var contentToCopy = isFileList
            ? string.Join(Environment.NewLine, SelectedClipFiles.Select(static file => file.FilePath))
            : SelectedClip.FullContent;

        await _systemInteractionService.CopyTextAsync(contentToCopy);
        StatusText = isFileList
            ? AppText.FormatCopiedFileList(SelectedClipFiles.Count)
            : AppText.FormatCopiedClip(SelectedClip.DisplayContentType.ToLower(AppText.CurrentCulture));
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        await _clipStoreService.DeleteAsync(SelectedClip.Id);
        await RefreshAsync();
    }

    private ClipSearchFilters BuildFilters(int offset) => new()
    {
        SearchText = SearchText,
        ContentType = SelectedContentTypeOption.Value,
        FavoritesOnly = ShowFavoritesOnly,
        SensitiveOnly = ShowSensitiveOnly,
        UseRegex = UseRegexSearch,
        CaseSensitive = CaseSensitiveSearch,
        Limit = PageSize,
        Offset = offset,
    };

    private void ApplyRefreshResult(ClipSearchResult result)
    {
        var previousSelectionId = SelectedClip?.Id;

        Clips.Clear();
        foreach (var item in result.Items.Select(static clip => new ClipItemViewModel(clip)))
        {
            Clips.Add(item);
        }

        _currentOffset = result.Items.Count;
        HasMoreResults = Clips.Count < result.TotalMatchingCount;
        this.RaisePropertyChanged(nameof(HasNoClips));
        SelectedClip = previousSelectionId is null
            ? Clips.FirstOrDefault()
            : Clips.FirstOrDefault(clip => clip.Id == previousSelectionId) ?? Clips.FirstOrDefault();

        UpdateStatus(result);
    }

    private void UpdateSelectedClipPresentation()
    {
        ReplaceSelectedClipFiles(ClipDisplayFormatter.BuildFileItems(SelectedClip?.FullContent));
        _selectedClipRenderedText = ClipDisplayFormatter.BuildRenderedText(SelectedClip?.Clip, SelectedClipFiles.Select(static file => file.FilePath).ToArray());

        var imagePreview = TryLoadImage(SelectedClip?.FullContent, _settingsService.Current.MaxClipSizeBytes);
        ReplaceSelectedClipImagePreview(imagePreview);
        _selectedClipImageHint = ClipDisplayFormatter.BuildImageHint(SelectedClip?.Clip, imagePreview is not null);
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

    private void ReplaceSelectedClipImagePreview(Bitmap? image)
    {
        var previousImage = _selectedClipImagePreview;
        _selectedClipImagePreview = image;
        previousImage?.Dispose();
    }

    private void RaiseSelectionStateProperties()
    {
        this.RaisePropertyChanged(nameof(IsSelectedClipFavorite));
        this.RaisePropertyChanged(nameof(HasSelectedClip));
        this.RaisePropertyChanged(nameof(SelectionStateTitle));
        this.RaisePropertyChanged(nameof(ShowEmptySelectionState));
        this.RaisePropertyChanged(nameof(SelectedClipFiles));
        this.RaisePropertyChanged(nameof(HasSelectedClipFileItems));
        this.RaisePropertyChanged(nameof(SelectedClipRenderedText));
        this.RaisePropertyChanged(nameof(SelectedClipRawContent));
        this.RaisePropertyChanged(nameof(SelectedClipImagePreview));
        this.RaisePropertyChanged(nameof(SelectedClipImageHint));
        this.RaisePropertyChanged(nameof(SelectedClipContentTypeText));
        this.RaisePropertyChanged(nameof(SelectedClipTitleText));
        this.RaisePropertyChanged(nameof(SelectedClipSourceText));
        this.RaisePropertyChanged(nameof(SelectedClipFirstCopiedAtText));
        this.RaisePropertyChanged(nameof(SelectedClipCapturedAtText));
        this.RaisePropertyChanged(nameof(SelectedClipCopyCountText));
        this.RaisePropertyChanged(nameof(SelectedClipByteSizeText));
        this.RaisePropertyChanged(nameof(SelectedClipSensitivityText));
        this.RaisePropertyChanged(nameof(SelectedClipAccentBrush));
        this.RaisePropertyChanged(nameof(SelectedClipAreaBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipAreaBorderThickness));
        this.RaisePropertyChanged(nameof(ShowSelectedClipFavoriteIndicator));
        this.RaisePropertyChanged(nameof(ShowSelectedClipSeverityIndicator));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityIndicatorText));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeBackground));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeBorderBrush));
        this.RaisePropertyChanged(nameof(SelectedClipSeverityBadgeForeground));
        RaiseRenderModeProperties();
    }

    private void RaiseFilterStateProperties()
    {
        this.RaisePropertyChanged(nameof(ActiveFilterSummary));
        this.RaisePropertyChanged(nameof(EmptyListMessage));
    }

    public bool TryHandleShortcut(KeyEventArgs e)
    {
        if (IsSettingsOpen)
        {
            return false;
        }

        return TryHandleShortcut(e, _settingsService.Current.ToggleRegexHotkey, () => UseRegexSearch = !UseRegexSearch)
            || TryHandleShortcut(e, _settingsService.Current.ToggleFavoritesHotkey, () => ShowFavoritesOnly = !ShowFavoritesOnly)
            || TryHandleShortcut(e, _settingsService.Current.ToggleSensitiveHotkey, () => ShowSensitiveOnly = !ShowSensitiveOnly)
            || TryHandleShortcut(e, _settingsService.Current.ToggleCaseSensitiveHotkey, () => CaseSensitiveSearch = !CaseSensitiveSearch);
    }

    private bool TryHandleShortcut(KeyEventArgs e, string hotkeyText, Action action)
    {
        if (!HotkeyGesture.TryParse(hotkeyText, out var hotkey, out _) || hotkey is null || !hotkey.Matches(e))
        {
            return false;
        }

        action();
        return true;
    }

    private void OpenSettings()
    {
        LoadSettingsDraft(_settingsService.Current);
        IsSettingsOpen = true;
    }

    private void CloseSettings()
    {
        LoadSettingsDraft(_settingsService.Current);
        IsSettingsOpen = false;
    }

    private async Task SaveSettingsAsync()
    {
        var hotkeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(AppSettings.ToggleRegexHotkey)] = SettingsToggleRegexHotkey,
            [nameof(AppSettings.ToggleFavoritesHotkey)] = SettingsToggleFavoritesHotkey,
            [nameof(AppSettings.ToggleSensitiveHotkey)] = SettingsToggleSensitiveHotkey,
            [nameof(AppSettings.ToggleCaseSensitiveHotkey)] = SettingsToggleCaseSensitiveHotkey,
            [nameof(AppSettings.ToggleWindowHotkey)] = SettingsToggleWindowHotkey,
        };

        var normalizedHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in hotkeys)
        {
            if (!HotkeyGesture.TryParse(pair.Value, out var gesture, out var error) || gesture is null)
            {
                StatusText = AppText.FormatSettingsValidationError(error ?? AppText.SettingsInvalidHotkeyFallback);
                return;
            }

            normalizedHotkeys[pair.Key] = gesture.ToString();
        }

        var duplicates = normalizedHotkeys.Values
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

        var settings = new AppSettings
        {
            ToggleRegexHotkey = normalizedHotkeys[nameof(AppSettings.ToggleRegexHotkey)],
            ToggleFavoritesHotkey = normalizedHotkeys[nameof(AppSettings.ToggleFavoritesHotkey)],
            ToggleSensitiveHotkey = normalizedHotkeys[nameof(AppSettings.ToggleSensitiveHotkey)],
            ToggleCaseSensitiveHotkey = normalizedHotkeys[nameof(AppSettings.ToggleCaseSensitiveHotkey)],
            ToggleWindowHotkey = normalizedHotkeys[nameof(AppSettings.ToggleWindowHotkey)],
            MaxClipSizeBytes = maxClipSizeBytes,
        };

        await _settingsService.SaveAsync(settings);
        IsSettingsOpen = false;
        StatusText = AppText.SettingsSavedStatus;
        UpdateSelectedClipPresentation();
        RaiseSelectionStateProperties();
    }

    private void LoadSettingsDraft(AppSettings settings)
    {
        SettingsToggleRegexHotkey = settings.ToggleRegexHotkey;
        SettingsToggleFavoritesHotkey = settings.ToggleFavoritesHotkey;
        SettingsToggleSensitiveHotkey = settings.ToggleSensitiveHotkey;
        SettingsToggleCaseSensitiveHotkey = settings.ToggleCaseSensitiveHotkey;
        SettingsToggleWindowHotkey = settings.ToggleWindowHotkey;
        SettingsMaxClipSizeKilobytes = (settings.MaxClipSizeBytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!IsSettingsOpen)
        {
            LoadSettingsDraft(settings);
        }

        UpdateSelectedClipPresentation();
        RaiseSelectionStateProperties();
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
        this.RaisePropertyChanged(nameof(ShowSelectedImagePlaceholder));
    }

    private void UpdateStatus(ClipSearchResult result)
    {
        var lastCaptured = result.LastCapturedAt is null
            ? AppText.NoCapturesYetLower
            : ClipDisplayFormatter.ToRelativeTime(result.LastCapturedAt.Value);

        MatchingClipCount = result.TotalMatchingCount;
        TotalClipCount = result.TotalClipCount;
        SensitiveClipCount = result.SensitiveClipCount;
        LastCaptureSummary = result.LastCapturedAt is null
            ? AppText.NoCapturesYet
            : AppText.FormatLastCapture(lastCaptured);

        StatusText = AppText.FormatStatusSummary(result.TotalMatchingCount, result.TotalClipCount, result.SensitiveClipCount, lastCaptured);
    }

    private static Bitmap? TryLoadImage(string? content, int? maxClipSizeBytes = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();

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

                    var bytes = Convert.FromBase64String(trimmed[(commaIndex + 1)..]);
                    using var stream = new MemoryStream(bytes);
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
        catch
        {
            return null;
        }

        return null;
    }

}