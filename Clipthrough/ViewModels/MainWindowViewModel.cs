using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Clipthrough.Models;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 200;

    private readonly IClipStoreService _clipStoreService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly CompositeDisposable _subscriptions = new();

    private string _searchText = string.Empty;
    private string _selectedContentType = "All";
    private bool _showFavoritesOnly;
    private bool _showSensitiveOnly;
    private bool _useRegexSearch;
    private ClipItemViewModel? _selectedClip;
    private ClipFileItemViewModel? _selectedFileItem;
    private bool _hasMoreResults;
    private bool _isBusy;
    private string _statusText = "Loading…";
    private int _currentOffset;
    private int _matchingClipCount;
    private int _totalClipCount;
    private int _sensitiveClipCount;
    private string _lastCaptureSummary = "Waiting for first capture";
    private bool _showRawContent;
    private string _selectedClipRenderedText = "Select a clip to preview its content.";
    private Bitmap? _selectedClipImagePreview;
    private string _selectedClipImageHint = "Select a clip to preview it.";

    public MainWindowViewModel(IClipStoreService clipStoreService, IClipboardMonitorService clipboardMonitorService, ISystemInteractionService systemInteractionService)
    {
        _clipStoreService = clipStoreService;
        _clipboardMonitorService = clipboardMonitorService;
        _systemInteractionService = systemInteractionService;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync, this.WhenAnyValue(x => x.HasMoreResults, x => x.IsBusy, static (hasMore, isBusy) => hasMore && !isBusy));

        var hasSelection = this.WhenAnyValue(x => x.SelectedClip).Select(static clip => clip is not null);
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync, hasSelection);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasSelection);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);


        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.SearchText,
                    x => x.SelectedContentType,
                    x => x.ShowFavoritesOnly,
                    x => x.ShowSensitiveOnly,
                    x => x.UseRegexSearch)
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
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(ex => StatusText = $"Error: {ex.Message}"));

        _clipboardMonitorService.Start();
        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public ObservableCollection<ClipFileItemViewModel> SelectedClipFiles { get; } = [];

    public IReadOnlyList<string> ContentTypeOptions { get; } = ["All", "Text", "Image", "RichText", "Files"];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }

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

    public bool UseRegexSearch
    {
        get => _useRegexSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _useRegexSearch, value);
            this.RaisePropertyChanged(nameof(ActiveFilterSummary));
            this.RaisePropertyChanged(nameof(EmptyListMessage));
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

    public string SelectedClipActionText => SelectedClip?.IsFavorite == true ? "Remove Favorite" : "Add Favorite";

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

    public string SelectedClipRawContent => SelectedClip?.FullContent ?? "Select a clip to preview its full content.";

    public Bitmap? SelectedClipImagePreview => _selectedClipImagePreview;

    public string SelectedClipImageHint => _selectedClipImageHint;

    public bool HasNoClips => Clips.Count == 0;

    public string SelectionStateTitle => HasSelectedClip
        ? "Selected clip"
        : "Choose a clip from the list to preview its details.";

    public string ClipboardStateText => IsBusy
        ? "Refreshing clipboard library…"
        : HasMoreResults
            ? "Scroll to load more clips."
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

            if (UseRegexSearch)
            {
                parts.Add("Regex");
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
        : UseRegexSearch
            ? "No clips match the current regex filters."
            : "No clips match the current filters.";

    public void Dispose()
    {
        _clipboardMonitorService.Stop();
        SelectedClipFiles.Clear();
        ReplaceSelectedClipImagePreview(null);
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

    private async Task CopySelectedAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        var contentToCopy = SelectedClip.Clip.ContentType == ContentType.Files && SelectedClipFiles.Count > 0
            ? string.Join(Environment.NewLine, SelectedClipFiles.Select(static file => file.FilePath))
            : SelectedClip.FullContent;

        await _systemInteractionService.CopyTextAsync(contentToCopy);
        StatusText = $"Copied {SelectedClip.DisplayContentType.ToLowerInvariant()} clip to the clipboard.";
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
        UseRegex = UseRegexSearch,
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
        ReplaceSelectedClipFiles(BuildFileItems(SelectedClip?.FullContent));
        _selectedClipRenderedText = BuildRenderedText(SelectedClip, SelectedClipFiles.Select(static file => file.FilePath).ToArray());

        var imagePreview = TryLoadImage(SelectedClip?.FullContent);
        ReplaceSelectedClipImagePreview(imagePreview);
        _selectedClipImageHint = BuildImageHint(SelectedClip, imagePreview is not null);
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
        this.RaisePropertyChanged(nameof(SelectedClipActionText));
        this.RaisePropertyChanged(nameof(HasSelectedClip));
        this.RaisePropertyChanged(nameof(SelectionStateTitle));
        this.RaisePropertyChanged(nameof(ShowEmptySelectionState));
        this.RaisePropertyChanged(nameof(SelectedClipFiles));
        this.RaisePropertyChanged(nameof(HasSelectedClipFileItems));
        this.RaisePropertyChanged(nameof(SelectedClipRenderedText));
        this.RaisePropertyChanged(nameof(SelectedClipRawContent));
        this.RaisePropertyChanged(nameof(SelectedClipImagePreview));
        this.RaisePropertyChanged(nameof(SelectedClipImageHint));
        RaiseRenderModeProperties();
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

    private static string BuildRenderedText(ClipItemViewModel? clip, IReadOnlyList<string> fileItems)
    {
        if (clip is null)
        {
            return "Select a clip to preview its content.";
        }

        if (string.IsNullOrWhiteSpace(clip.FullContent))
        {
            return clip.Clip.ContentType switch
            {
                ContentType.Image => "This image clip does not include previewable image data.",
                ContentType.Files => "This file clip does not include any stored paths.",
                ContentType.RichText => "This rich text clip is empty.",
                _ => "This clip is empty.",
            };
        }

        return clip.Clip.ContentType switch
        {
            ContentType.RichText => RenderRichContent(clip.FullContent),
            ContentType.Files => fileItems.Count == 0
                ? NormalizePreviewText(clip.FullContent)
                : $"{fileItems.Count} file{(fileItems.Count == 1 ? string.Empty : "s")} captured",
            _ => NormalizePreviewText(clip.FullContent),
        };
    }

    private static IReadOnlyList<string> BuildFileItems(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var normalized = content
            .Replace("Files copied:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Copied files:", string.Empty, StringComparison.OrdinalIgnoreCase);

        return normalized
            .Split(['\r', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.Trim(' ', '"', '\'', '•', '-'))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildImageHint(ClipItemViewModel? clip, bool hasPreview)
    {
        if (clip is null)
        {
            return "Select an image clip to preview it.";
        }

        if (hasPreview)
        {
            return "Image preview loaded from the stored clipboard payload.";
        }

        if (string.IsNullOrWhiteSpace(clip.FullContent))
        {
            return "This image clip does not include previewable image data.";
        }

        return "This entry is marked as an image, but the stored payload is text only. Switch to raw mode to inspect the original data.";
    }

    private static Bitmap? TryLoadImage(string? content)
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

    private static string RenderRichContent(string content)
    {
        if (LooksLikeHtml(content))
        {
            var withoutScripts = Regex.Replace(content, @"<(script|style)[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var withListItems = Regex.Replace(withoutScripts, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
            var withBreaks = Regex.Replace(withListItems, @"</?(br|p|div|section|article|ul|ol|h[1-6]|tr)[^>]*>", Environment.NewLine, RegexOptions.IgnoreCase);
            var withoutTags = Regex.Replace(withBreaks, @"<[^>]+>", string.Empty, RegexOptions.IgnoreCase);
            return NormalizePreviewText(WebUtility.HtmlDecode(withoutTags));
        }

        if (LooksLikeRtf(content))
        {
            var withParagraphs = Regex.Replace(content, @"\\par[d]? ?", Environment.NewLine, RegexOptions.IgnoreCase);
            var withTabs = Regex.Replace(withParagraphs, @"\\tab ?", "\t", RegexOptions.IgnoreCase);
            var withHexDecoded = Regex.Replace(withTabs, @"\\'[0-9a-fA-F]{2}", static match => DecodeRtfHex(match.Value));
            var withoutControlWords = Regex.Replace(withHexDecoded, @"\\[a-zA-Z]+-?\d* ?", string.Empty, RegexOptions.IgnoreCase);
            var withoutGroups = withoutControlWords.Replace("{", string.Empty).Replace("}", string.Empty, StringComparison.Ordinal);
            return NormalizePreviewText(withoutGroups);
        }

        return NormalizePreviewText(content);
    }

    private static bool LooksLikeHtml(string content) => Regex.IsMatch(content, @"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase);

    private static bool LooksLikeRtf(string content) => content.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);

    private static string DecodeRtfHex(string token)
    {
        if (token.Length < 4)
        {
            return string.Empty;
        }

        var hexValue = token[^2..];
        return byte.TryParse(hexValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? ((char)value).ToString()
            : string.Empty;
    }

    private static string NormalizePreviewText(string content)
    {
        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var normalizedLines = new List<string>(lines.Length);
        var previousWasBlank = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var isBlank = string.IsNullOrWhiteSpace(line);

            if (isBlank)
            {
                if (previousWasBlank)
                {
                    continue;
                }

                normalizedLines.Add(string.Empty);
            }
            else
            {
                normalizedLines.Add(line);
            }

            previousWasBlank = isBlank;
        }

        var normalized = string.Join(Environment.NewLine, normalizedLines).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "This clip does not contain previewable text."
            : normalized;
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