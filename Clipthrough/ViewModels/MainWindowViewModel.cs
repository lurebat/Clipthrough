using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 200;

    private readonly IClipStoreService _clipStoreService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly CompositeDisposable _subscriptions = new();

    private string _searchText = string.Empty;
    private string _selectedContentType = "All";
    private bool _showFavoritesOnly;
    private bool _showSensitiveOnly;
    private ClipItemViewModel? _selectedClip;
    private bool _hasMoreResults;
    private bool _isBusy;
    private string _statusText = "Loading…";
    private int _currentOffset;
    private int _matchingClipCount;
    private int _totalClipCount;
    private int _sensitiveClipCount;
    private string _lastCaptureSummary = "Waiting for first capture";

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService)
    {
        _clipStoreService = clipStoreService;
        _clipboardMonitorService = clipboardMonitorService;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync, this.WhenAnyValue(x => x.HasMoreResults));

        var hasSelection = this.WhenAnyValue(x => x.SelectedClip).Select(static clip => clip is not null);
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync, hasSelection);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasSelection);


        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.SearchText,
                    x => x.SelectedContentType,
                    x => x.ShowFavoritesOnly,
                    x => x.ShowSensitiveOnly)
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
                .Merge(DeleteSelectedCommand.ThrownExceptions)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(ex => StatusText = $"Error: {ex.Message}"));

        _clipboardMonitorService.Start();
        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public IReadOnlyList<string> ContentTypeOptions { get; } = ["All", "Text", "Image", "RichText", "Files"];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            this.RaisePropertyChanged(nameof(ActiveFilterSummary));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
        }
    }

    public string SelectedContentType
    {
        get => _selectedContentType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedContentType, value);
            this.RaisePropertyChanged(nameof(ActiveFilterSummary));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
        }
    }

    public bool ShowFavoritesOnly
    {
        get => _showFavoritesOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFavoritesOnly, value);
            this.RaisePropertyChanged(nameof(ActiveFilterSummary));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
        }
    }

    public bool ShowSensitiveOnly
    {
        get => _showSensitiveOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showSensitiveOnly, value);
            this.RaisePropertyChanged(nameof(ActiveFilterSummary));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
        }
    }

    public ClipItemViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedClip, value);
            this.RaisePropertyChanged(nameof(SelectedClipActionText));
            this.RaisePropertyChanged(nameof(HasSelectedClip));
            this.RaisePropertyChanged(nameof(SelectionStateTitle));
        }
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
        private set => this.RaiseAndSetIfChanged(ref _matchingClipCount, value);
    }

    public int TotalClipCount
    {
        get => _totalClipCount;
        private set => this.RaiseAndSetIfChanged(ref _totalClipCount, value);
    }

    public int SensitiveClipCount
    {
        get => _sensitiveClipCount;
        private set => this.RaiseAndSetIfChanged(ref _sensitiveClipCount, value);
    }

    public string LastCaptureSummary
    {
        get => _lastCaptureSummary;
        private set => this.RaiseAndSetIfChanged(ref _lastCaptureSummary, value);
    }

    public string SelectedClipActionText => SelectedClip?.IsFavorite == true ? "Unpin" : "Pin";

    public bool HasSelectedClip => SelectedClip is not null;

    public bool HasNoClips => Clips.Count == 0;

    public string SelectionStateTitle => HasSelectedClip
        ? "Selected clip"
        : "Choose a clip from the list to preview its details.";

    public string ClipboardStateText => IsBusy
        ? "Refreshing clipboard library…"
        : HasMoreResults
            ? "More results are available to load."
            : "Everything matching your filters is loaded.";

    public string ActiveFilterSummary
    {
        get
        {
            var parts = new List<string>();

            if (!string.Equals(SelectedContentType, "All", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(SelectedContentType);
            }

            if (ShowFavoritesOnly)
            {
                parts.Add("Favorites");
            }

            if (ShowSensitiveOnly)
            {
                parts.Add("Sensitive");
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                parts.Add($"Search: \"{SearchText.Trim()}\"");
            }

            return parts.Count == 0 ? "Showing the full clipboard archive" : string.Join(" · ", parts);
        }
    }

    public string EmptyListMessage => IsBusy
        ? "Loading your clipboard history…"
        : "No clips match the current filters.";

    public void Dispose()
    {
        _clipboardMonitorService.Stop();
        _subscriptions.Dispose();
    }

    private async Task RefreshAsync()
    {
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
        if (!HasMoreResults)
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
        ContentType = ContentTypeExtensions.FromFilter(SelectedContentType),
        FavoritesOnly = ShowFavoritesOnly,
        SensitiveOnly = ShowSensitiveOnly,
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

    private void UpdateStatus(ClipSearchResult result)
    {
        var lastCaptured = result.LastCapturedAt is null
            ? "no captures yet"
            : ToRelative(result.LastCapturedAt.Value);

        MatchingClipCount = result.TotalMatchingCount;
        TotalClipCount = result.TotalClipCount;
        SensitiveClipCount = result.SensitiveClipCount;
        LastCaptureSummary = result.LastCapturedAt is null
            ? "No captures yet"
            : $"Last capture {lastCaptured}";

        StatusText = $"{result.TotalMatchingCount:N0} matching · {result.TotalClipCount:N0} total clips · {result.SensitiveClipCount:N0} sensitive · Last capture {lastCaptured}";
    }

    private static string ToRelative(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();

        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalHours)} hr ago";
        }

        return $"{Math.Max(1, (int)delta.TotalDays)} day ago" + (delta.TotalDays >= 2 ? "s" : string.Empty);
    }
}