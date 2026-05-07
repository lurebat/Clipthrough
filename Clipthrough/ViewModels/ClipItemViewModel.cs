using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class ClipItemViewModel : ViewModelBase, IDisposable
{
    private static readonly IBrush s_defaultAccentBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush s_favoriteAccentBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush s_sensitiveAccentBrush = new SolidColorBrush(Color.Parse("#F43F5E"));
    private static readonly IBrush s_favoriteSensitiveAccentBrush = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush s_defaultBorderBrush = new SolidColorBrush(Color.Parse("#243247"));

    private static readonly IBrush s_frequencyLowBrush = new SolidColorBrush(Color.Parse("#1A3B82F6"));
    private static readonly IBrush s_frequencyMediumBrush = new SolidColorBrush(Color.Parse("#1A22C55E"));
    private static readonly IBrush s_frequencyHighBrush = new SolidColorBrush(Color.Parse("#1AF59E0B"));
    private static readonly IBrush s_pinnedBackgroundBrush = new SolidColorBrush(Color.Parse("#24210B"));

    private static readonly IBrush s_shortcutIndexForeground = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IBrush s_normalIndexForeground = new SolidColorBrush(Color.Parse("#475569"));

    // Type chip colors (bg, border, fg triples), content-type themed
    private static readonly IBrush s_typeTextBg = new SolidColorBrush(Color.Parse("#1E1E44"));
    private static readonly IBrush s_typeTextBorder = new SolidColorBrush(Color.Parse("#4338CA"));
    private static readonly IBrush s_typeTextFg = new SolidColorBrush(Color.Parse("#C7D2FE"));

    private static readonly IBrush s_typeImageBg = new SolidColorBrush(Color.Parse("#082F20"));
    private static readonly IBrush s_typeImageBorder = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush s_typeImageFg = new SolidColorBrush(Color.Parse("#6EE7B7"));

    private static readonly IBrush s_typeRichBg = new SolidColorBrush(Color.Parse("#3B1A00"));
    private static readonly IBrush s_typeRichBorder = new SolidColorBrush(Color.Parse("#C2410C"));
    private static readonly IBrush s_typeRichFg = new SolidColorBrush(Color.Parse("#FDBA74"));

    private static readonly IBrush s_typeFilesBg = new SolidColorBrush(Color.Parse("#0B2A3F"));
    private static readonly IBrush s_typeFilesBorder = new SolidColorBrush(Color.Parse("#0369A1"));
    private static readonly IBrush s_typeFilesFg = new SolidColorBrush(Color.Parse("#7DD3FC"));

    // Age chip colors
    private static readonly IBrush s_ageFreshBg = new SolidColorBrush(Color.Parse("#0A2E1F"));
    private static readonly IBrush s_ageFreshBorder = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush s_ageFreshFg = new SolidColorBrush(Color.Parse("#6EE7B7"));

    private static readonly IBrush s_ageRecentBg = new SolidColorBrush(Color.Parse("#22263D"));
    private static readonly IBrush s_ageRecentBorder = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush s_ageRecentFg = new SolidColorBrush(Color.Parse("#CBD5E1"));

    private static readonly IBrush s_ageOldBg = new SolidColorBrush(Color.Parse("#3A2807"));
    private static readonly IBrush s_ageOldBorder = new SolidColorBrush(Color.Parse("#A16207"));
    private static readonly IBrush s_ageOldFg = new SolidColorBrush(Color.Parse("#FCD34D"));

    private static readonly IBrush s_ageAncientBg = new SolidColorBrush(Color.Parse("#2D1421"));
    private static readonly IBrush s_ageAncientBorder = new SolidColorBrush(Color.Parse("#7F1D3A"));
    private static readonly IBrush s_ageAncientFg = new SolidColorBrush(Color.Parse("#FECDD3"));

    // Pasted chip colors
    private static readonly IBrush s_pastedBg = new SolidColorBrush(Color.Parse("#1E1444"));
    private static readonly IBrush s_pastedBorder = new SolidColorBrush(Color.Parse("#7C3AED"));
    private static readonly IBrush s_pastedFg = new SolidColorBrush(Color.Parse("#C4B5FD"));

    private bool _isChecked;
    private int _displayIndex;
    private readonly string _title;
    private readonly string _previewSnippet;
    private readonly string _singleLinePreview;
    private string? _fullContent;
    private Bitmap? _sourceAppIconImage;
    private Bitmap? _previewThumbnailImage;
    private bool _sourceAppIconLoaded;
    private bool _previewThumbnailLoaded;
    private bool _isDisposed;

    public ClipItemViewModel(
        ClipEntry clip,
        Func<ClipItemViewModel, Task>? copyHandler = null,
        Func<ClipItemViewModel, Task>? toggleFavoriteHandler = null,
        Func<ClipItemViewModel, Task>? deleteHandler = null,
        Func<ClipItemViewModel, Task>? exportHandler = null,
        Func<ClipItemViewModel, Task>? togglePinHandler = null,
        Func<ClipItemViewModel, TextTransformation, Task>? applyTransformHandler = null)
    {
        Clip = clip;
        _title = ClipDisplayFormatter.BuildTitle(clip);
        _previewSnippet = ClipDisplayFormatter.BuildPreviewSnippet(clip);
        _singleLinePreview = ClipDisplayFormatter.BuildSingleLinePreview(clip);
        CopyCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (copyHandler is not null)
                {
                    await copyHandler(this);
                }
            });
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (toggleFavoriteHandler is not null)
                {
                    await toggleFavoriteHandler(this);
                }
            });
        DeleteCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (deleteHandler is not null)
                {
                    await deleteHandler(this);
                }
            });
        ExportCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (exportHandler is not null)
                {
                    await exportHandler(this);
                }
            });
        TogglePinCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (togglePinHandler is not null)
                {
                    await togglePinHandler(this);
                }
            });
        ApplyTextTransformationCommand = ReactiveCommand.CreateFromTask<TextTransformation>(
            async t =>
            {
                if (applyTransformHandler is not null)
                {
                    await applyTransformHandler(this, t);
                }
            });
    }

    public ClipEntry Clip { get; }

    public long Id => Clip.Id;

    public ReactiveCommand<Unit, Unit> CopyCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportCommand { get; }

    public ReactiveCommand<Unit, Unit> TogglePinCommand { get; }

    public ReactiveCommand<TextTransformation, Unit> ApplyTextTransformationCommand { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => this.RaiseAndSetIfChanged(ref _isChecked, value);
    }

    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex == value)
            {
                return;
            }

            _displayIndex = value;
            this.RaisePropertyChanged(nameof(DisplayIndex));
            this.RaisePropertyChanged(nameof(DisplayIndexText));
            this.RaisePropertyChanged(nameof(IsShortcutIndexed));
            this.RaisePropertyChanged(nameof(DisplayIndexForeground));
        }
    }

    public string DisplayIndexText => _displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsShortcutIndexed => _displayIndex >= 1 && _displayIndex <= 9;

    public IBrush DisplayIndexForeground => IsShortcutIndexed ? s_shortcutIndexForeground : s_normalIndexForeground;

    public string Title => _title;

    public string Preview => Clip.Content;

    public string PreviewSnippet => _previewSnippet;

    public string SingleLinePreview => _singleLinePreview;

    public string FullContent => _fullContent ??= ClipDisplayFormatter.GetRawContentDisplay(Clip);

    public string DisplayContentType => Clip.ContentType.ToDisplayName();

    public string TypeGlyph => Clip.ContentType switch
    {
        ContentType.Text => "📋",
        ContentType.Image => "🖼",
        ContentType.RichText => "📝",
        ContentType.Files => "📁",
        _ => "📋",
    };

    public int CopyCount => Clip.CopyCount;

    public bool HasMultipleCopies => Clip.CopyCount > 1;

    public bool IsTextClip => Clip.ContentType == ContentType.Text || Clip.ContentType == ContentType.RichText;

    public bool IsImageClip => Clip.ContentType == ContentType.Image;

    public bool CanTransform => IsTextClip && !string.IsNullOrEmpty(Clip.Content);

    public bool CanAiTransform =>
        (IsTextClip && !string.IsNullOrEmpty(Clip.Content))
        || (IsImageClip && Clip.ContentBytes is { Length: > 0 });

    public string CopyCountDisplay => AppText.FormatCopyCount(Clip.CopyCount);

    public string CopyCountCompact => AppText.FormatCopyCountCompact(Clip.CopyCount);

    public string RelativeCapturedAt => ClipDisplayFormatter.ToRelativeTime(Clip.LastCopiedAt);

    public string CapturedAtDisplay => ClipDisplayFormatter.ToCapturedAtDisplay(Clip.LastCopiedAt);

    public string FirstCopiedAtDisplay => ClipDisplayFormatter.ToCapturedAtDisplay(Clip.FirstCopiedAt);

    public string LastCopiedAtDisplay => ClipDisplayFormatter.ToCapturedAtDisplay(Clip.LastCopiedAt);

    public string CapturedAtCompact => RelativeCapturedAt;

    public string Subtitle => HasMultipleCopies
        ? $"{RelativeCapturedAt} · {DisplayContentType} · {CopyCountDisplay}"
        : $"{RelativeCapturedAt} · {DisplayContentType}";

    public string SourceApp => string.IsNullOrWhiteSpace(Clip.SourceApp) ? AppText.UnknownSource : Clip.SourceApp;

    public string? SourceWindowTitle => Clip.SourceWindowTitle;

    public bool HasSourceWindowTitle => !string.IsNullOrWhiteSpace(Clip.SourceWindowTitle);

    public string? SourceUrl => Clip.SourceUrl;

    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(Clip.SourceUrl);

    public bool HasAnySourceInfo => HasSourceUrl || HasSourceWindowTitle;

    public bool ShowWindowTitleChipInRow => HasSourceWindowTitle && !ShowPreviewThumbnail;

    public bool IsPasted => Clip.IsPasted;

    public int PasteCount => Clip.PasteCount;

    public bool HasBeenPasted => Clip.PasteCount > 0;

    public string PasteCountDisplay => Clip.PasteCount > 0 ? $"Pasted {Clip.PasteCount}x" : string.Empty;

    public Bitmap? SourceAppIconImage
    {
        get
        {
            if (_sourceAppIconLoaded)
            {
                return _sourceAppIconImage;
            }

            _sourceAppIconLoaded = true;
            LoadBitmapInBackground(Clip.SourceAppIconBytes, bitmap =>
            {
                _sourceAppIconImage = bitmap;
                this.RaisePropertyChanged(nameof(SourceAppIconImage));
            });
            return _sourceAppIconImage;
        }
    }

    public bool HasSourceAppIcon => Clip.SourceAppIconBytes is { Length: > 0 };

    public bool ShowTypeGlyph => !HasSourceAppIcon;

    public Bitmap? PreviewThumbnailImage
    {
        get
        {
            if (_previewThumbnailLoaded)
            {
                return _previewThumbnailImage;
            }

            _previewThumbnailLoaded = true;
            if (Clip.ContentType == ContentType.Image)
            {
                LoadBitmapInBackground(Clip.ContentBytes, bitmap =>
                {
                    _previewThumbnailImage = bitmap;
                    this.RaisePropertyChanged(nameof(PreviewThumbnailImage));
                });
            }
            return _previewThumbnailImage;
        }
    }

    public bool ShowPreviewThumbnail => Clip.ContentType == ContentType.Image && Clip.ContentBytes is { Length: > 0 };

    public bool ShowTextPreview => !ShowPreviewThumbnail;

    public string ImageResolutionDisplay => ClipDisplayFormatter.TryGetImageDimensionsDisplay(Clip) ?? AppText.NotAvailable;

    public string ThumbnailInfoCompact
    {
        get
        {
            if (!ShowPreviewThumbnail)
            {
                return string.Empty;
            }

            var dims = ClipDisplayFormatter.TryGetImageDimensionsDisplay(Clip);
            var size = AppText.FormatByteCount(Clip.ByteSize);
            return string.IsNullOrWhiteSpace(dims) ? size : $"{dims} · {size}";
        }
    }

    public string SourceSummary => HasMultipleCopies
        ? $"{SourceApp} · {RelativeCapturedAt} · {CopyCountDisplay}"
        : $"{SourceApp} · {RelativeCapturedAt}";

    public string ByteSizeDisplay => AppText.FormatByteCount(Clip.ByteSize);

    public bool IsFavorite => Clip.IsFavorite;

    public bool IsPinned => Clip.IsPinned;

    public string PinMarker => IsPinned ? "📌" : string.Empty;

    public string PinActionLabel => IsPinned ? "Unpin" : "Pin";

    public bool IsSensitive => Clip.IsSensitive;

    public string HighestSeverity => Clip.SensitivityMatches
        .Select(static match => match.Severity)
        .OrderByDescending(GetSeverityRank)
        .FirstOrDefault() ?? string.Empty;

    public bool HasCriticalSeverity => string.Equals(HighestSeverity, "critical", StringComparison.OrdinalIgnoreCase);

    public bool HasWarningSeverity => IsSensitive && !HasCriticalSeverity;

    public IBrush StateAccentBrush => GetStateAccentBrush(IsFavorite, IsSensitive);

    public IBrush RowBorderBrush => IsSensitive
        ? StateAccentBrush
        : IsFavorite
            ? s_favoriteAccentBrush
            : s_defaultBorderBrush;

    public Thickness RowBorderThickness => IsSensitive
        ? new Thickness(2)
        : IsFavorite
            ? new Thickness(1.5)
            : new Thickness(1);

    public string SensitivitySummary => Clip.SensitivityMatches.Count == 0
        ? AppText.SensitivityNoMatch
        : string.Join(", ", Clip.SensitivityMatches.Select(static match => $"{match.RuleName} ({match.Severity})"));

    public string FavoriteMarker => IsFavorite ? "★" : string.Empty;

    public string CopyLabel => AppText.CopyButtonLabel;

    public string DeleteLabel => AppText.DeleteButtonLabel;

    public string FavoriteActionLabel => AppText.FavoriteButtonLabel;

    public string ExportLabel => AppText.ExportButtonLabel;

    public IBrush RowBackgroundBrush => IsPinned ? s_pinnedBackgroundBrush : GetFrequencyBrush(Clip.CopyCount);

    public string CopyCountBadge => Clip.CopyCount > 1 ? $"×{Clip.CopyCount}" : string.Empty;

    public bool ShowCopyCountBadge => Clip.CopyCount > 1;

    public IBrush TypeChipBackground => Clip.ContentType switch
    {
        ContentType.Text => s_typeTextBg,
        ContentType.Image => s_typeImageBg,
        ContentType.RichText => s_typeRichBg,
        ContentType.Files => s_typeFilesBg,
        _ => s_typeTextBg,
    };

    public IBrush TypeChipBorderBrush => Clip.ContentType switch
    {
        ContentType.Text => s_typeTextBorder,
        ContentType.Image => s_typeImageBorder,
        ContentType.RichText => s_typeRichBorder,
        ContentType.Files => s_typeFilesBorder,
        _ => s_typeTextBorder,
    };

    public IBrush TypeChipForeground => Clip.ContentType switch
    {
        ContentType.Text => s_typeTextFg,
        ContentType.Image => s_typeImageFg,
        ContentType.RichText => s_typeRichFg,
        ContentType.Files => s_typeFilesFg,
        _ => s_typeTextFg,
    };

    private (IBrush Bg, IBrush Border, IBrush Fg) GetAgeColors()
    {
        var age = DateTimeOffset.UtcNow - Clip.LastCopiedAt.ToUniversalTime();
        if (age.TotalHours < 1) return (s_ageFreshBg, s_ageFreshBorder, s_ageFreshFg);
        if (age.TotalDays < 1) return (s_ageRecentBg, s_ageRecentBorder, s_ageRecentFg);
        if (age.TotalDays < 7) return (s_ageOldBg, s_ageOldBorder, s_ageOldFg);
        return (s_ageAncientBg, s_ageAncientBorder, s_ageAncientFg);
    }

    public IBrush AgeChipBackground => GetAgeColors().Bg;

    public IBrush AgeChipBorderBrush => GetAgeColors().Border;

    public IBrush AgeChipForeground => GetAgeColors().Fg;

    public IBrush PastedChipBackground => s_pastedBg;

    public IBrush PastedChipBorderBrush => s_pastedBorder;

    public IBrush PastedChipForeground => s_pastedFg;

    public string PastedMarker => HasBeenPasted
        ? (Clip.PasteCount > 1 ? $"✓ Pasted ×{Clip.PasteCount}" : "✓ Pasted")
        : string.Empty;

    public void SetFavoriteState(bool isFavorite)
    {
        if (Clip.IsFavorite == isFavorite)
        {
            return;
        }

        Clip.IsFavorite = isFavorite;
        this.RaisePropertyChanged(nameof(IsFavorite));
        this.RaisePropertyChanged(nameof(StateAccentBrush));
        this.RaisePropertyChanged(nameof(RowBorderBrush));
        this.RaisePropertyChanged(nameof(RowBorderThickness));
        this.RaisePropertyChanged(nameof(FavoriteMarker));
        this.RaisePropertyChanged(nameof(FavoriteActionLabel));
    }

    public void SetPinnedState(bool isPinned)
    {
        var now = isPinned ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        if (Clip.PinnedAt.HasValue == isPinned)
        {
            return;
        }

        Clip.PinnedAt = now;
        this.RaisePropertyChanged(nameof(IsPinned));
        this.RaisePropertyChanged(nameof(PinMarker));
        this.RaisePropertyChanged(nameof(PinActionLabel));
    }

    public void Dispose()
    {
        _isDisposed = true;
        _previewThumbnailImage?.Dispose();
        _sourceAppIconImage?.Dispose();
    }

    private void LoadBitmapInBackground(byte[]? bytes, Action<Bitmap?> apply)
    {
        if (bytes is not { Length: > 0 })
        {
            return;
        }

        _ = Task.Run(() => ClipBitmapFactory.TryLoad(bytes))
            .ContinueWith(task =>
            {
                var bitmap = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isDisposed)
                    {
                        bitmap?.Dispose();
                        return;
                    }

                    apply(bitmap);
                });
            }, TaskScheduler.Default);
    }

    private static int GetSeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0,
    };

    private static IBrush GetFrequencyBrush(int copyCount) => copyCount switch
    {
        >= 10 => s_frequencyHighBrush,
        >= 5 => s_frequencyMediumBrush,
        >= 2 => s_frequencyLowBrush,
        _ => Brushes.Transparent,
    };

    private static IBrush GetStateAccentBrush(bool isFavorite, bool isSensitive)
    {
        if (isFavorite && isSensitive)
        {
            return s_favoriteSensitiveAccentBrush;
        }

        if (isSensitive)
        {
            return s_sensitiveAccentBrush;
        }

        if (isFavorite)
        {
            return s_favoriteAccentBrush;
        }

        return s_defaultAccentBrush;
    }
}
