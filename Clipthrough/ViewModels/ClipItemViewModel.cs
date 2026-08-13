using System;
using System.Collections.Generic;
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
using Clipthrough.Services;
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

    private static readonly IBrush s_metaMutedBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

    private bool _isChecked;
    private int _displayIndex;
    private readonly string _title;
    private readonly string _previewSnippet;
    private readonly string _singleLinePreview;
    private string _metaLine = string.Empty;
    private IReadOnlyList<(string Text, IBrush Foreground)> _metaSegments = Array.Empty<(string, IBrush)>();
    private string? _fullContent;
    private Bitmap? _sourceAppIconImage;
    private Bitmap? _previewThumbnailImage;
    private bool _sourceAppIconLoaded;
    private bool _previewThumbnailLoaded;
    private bool _isDisposed;
    private readonly Func<long, Task<ClipEntry?>>? _contentHydrator;
    private readonly Func<ClipItemViewModel, Task<byte[]?>>? _sourceAppIconLoader;
    private byte[]? _loadedSourceAppIcon;
    private readonly IDisposable _commandErrors;
    private bool _contentHydrationStarted;
    private bool _sourceAppIconRequested;

    /// <summary>
    /// Width the row thumbnail is decoded to. The row draws it at 84x48 logical pixels;
    /// this leaves headroom for high-DPI displays and UniformToFill cropping.
    /// </summary>
    private const int ThumbnailDecodeWidth = 256;

    public ClipItemViewModel(
        ClipEntry clip,
        Func<ClipItemViewModel, Task>? copyHandler = null,
        Func<ClipItemViewModel, Task>? toggleFavoriteHandler = null,
        Func<ClipItemViewModel, Task>? deleteHandler = null,
        Func<ClipItemViewModel, Task>? exportHandler = null,
        Func<ClipItemViewModel, Task>? togglePinHandler = null,
        Func<ClipItemViewModel, TextTransformation, Task>? applyTransformHandler = null,
        Func<long, Task<ClipEntry?>>? contentHydrator = null,
        Func<ClipItemViewModel, Task<byte[]?>>? sourceAppIconLoader = null)
    {
        Clip = clip;
        _contentHydrator = contentHydrator;
        _sourceAppIconLoader = sourceAppIconLoader;
        var display = ClipDisplayFormatter.BuildDisplayStrings(clip);
        _title = display.Title;
        _previewSnippet = display.PreviewSnippet;
        _singleLinePreview = display.SingleLinePreview;
        _metaLine = BuildMetaLine();
        _metaSegments = BuildMetaSegments();
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
        _commandErrors = ObserveCommandErrors();
    }

    public ClipEntry Clip { get; private set; }

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

    // Single precomputed meta line for the list row (type · age · markers), built
    // once at construction and rebuilt only on favorite/pin toggle. Replaces the
    // per-row chip WrapPanel (~16 controls + ~13 bindings) with one TextBlock so
    // row realization/recycling on scroll stays cheap.
    public string MetaLine => _metaLine;

    private string BuildMetaLine()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(DisplayContentType).Append(" · ").Append(CapturedAtCompact);
        if (ShowWindowTitleChipInRow && !string.IsNullOrEmpty(SourceWindowTitle))
        {
            sb.Append(" · ").Append(SourceWindowTitle);
        }
        if (HasBeenPasted)
        {
            sb.Append(" · ").Append(PastedMarker);
        }
        if (ShowCopyCountBadge)
        {
            sb.Append(" · ").Append(CopyCountCompact);
        }
        if (IsFavorite)
        {
            sb.Append(" · ★");
        }
        if (IsPinned)
        {
            sb.Append(" · 📌");
        }
        if (IsImported)
        {
            sb.Append(" · ").Append(ImportedBadgeLabel);
        }
        return sb.ToString();
    }

    // Colored token list backing the row meta line (rendered as inline Runs via
    // controls:MetaInlines). Same tokens as MetaLine, each carrying the matching
    // chip foreground colour so the row keeps per-token colour without a chip
    // control per token. Rebuilt with MetaLine on favorite/pin toggle.
    public IReadOnlyList<(string Text, IBrush Foreground)> MetaSegments => _metaSegments;

    private IReadOnlyList<(string Text, IBrush Foreground)> BuildMetaSegments()
    {
        var segments = new List<(string Text, IBrush Foreground)>(7)
        {
            (DisplayContentType, TypeChipForeground),
            (CapturedAtCompact, AgeChipForeground),
        };
        if (ShowWindowTitleChipInRow && !string.IsNullOrEmpty(SourceWindowTitle))
        {
            segments.Add((SourceWindowTitle!, s_metaMutedBrush));
        }
        if (HasBeenPasted)
        {
            segments.Add((PastedMarker, PastedChipForeground));
        }
        if (ShowCopyCountBadge)
        {
            segments.Add((CopyCountCompact, s_metaMutedBrush));
        }
        if (IsFavorite)
        {
            segments.Add(("★", StateAccentBrush));
        }
        if (IsPinned)
        {
            segments.Add(("📌", s_metaMutedBrush));
        }
        if (IsImported)
        {
            segments.Add((ImportedBadgeLabel, s_metaMutedBrush));
        }
        return segments;
    }

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

    public bool CanTransform =>
        (IsTextClip || Clip.ContentType == ContentType.Files)
        && !string.IsNullOrEmpty(Clip.Content);

    // For image clips, ContentBytes may be null on metadata-only list reads (U12); use
    // ContentType to decide availability — full bytes load when the clip is opened. (U12)
    public bool CanAiTransform =>
        ((IsTextClip || Clip.ContentType == ContentType.Files) && !string.IsNullOrEmpty(Clip.Content))
        || IsImageClip;

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

            var iconBytes = SourceAppIconBytes;
            if (iconBytes is null)
            {
                // List reads omit the icon blob (U12). Fetch just that one column
                // rather than the whole row; this getter re-runs when the load
                // re-raises SourceAppIconImage.
                if (Clip.SourceAppIconAvailable && _sourceAppIconLoader is not null)
                {
                    _ = EnsureSourceAppIconAsync();
                    return null;
                }
                _sourceAppIconLoaded = true;
                return null;
            }

            _sourceAppIconLoaded = true;
            LoadBitmapInBackground(iconBytes, bitmap =>
            {
                _sourceAppIconImage = bitmap;
                this.RaisePropertyChanged(nameof(SourceAppIconImage));
            });
            return _sourceAppIconImage;
        }
    }

    /// <summary>
    /// The source-application icon blob, whether it arrived with the clip or was loaded
    /// separately afterwards. Prefer this over <c>Clip.SourceAppIconBytes</c>, which is
    /// null on list reads (U12) and stays null because the icon is not loaded back into
    /// the entry.
    /// </summary>
    public byte[]? SourceAppIconBytes => Clip.SourceAppIconBytes ?? _loadedSourceAppIcon;

    /// <summary>
    /// Loads the source-app icon blob on its own, without dragging back the thirty-column
    /// row (image blob included) that <see cref="EnsureContentHydratedAsync"/> reads.
    /// </summary>
    public async Task EnsureSourceAppIconAsync()
    {
        if (_sourceAppIconLoader is null || _sourceAppIconRequested || _isDisposed)
        {
            return;
        }
        if (!Clip.SourceAppIconAvailable || SourceAppIconBytes is not null)
        {
            return;
        }
        _sourceAppIconRequested = true;
        try
        {
            var bytes = await _sourceAppIconLoader(this).ConfigureAwait(false);
            if (bytes is not { Length: > 0 } || _isDisposed)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed)
                {
                    return;
                }
                _loadedSourceAppIcon = bytes;
                _sourceAppIconLoaded = false;
                this.RaisePropertyChanged(nameof(SourceAppIconImage));
            });
        }
        catch (Exception ex)
        {
            _sourceAppIconRequested = false; // allow a later retry
            System.Diagnostics.Trace.TraceWarning($"Clip {Clip.Id} source-app icon load failed: {ex.Message}");
        }
    }

    // Uses SourceAppIconAvailable so the icon presence flag is correct even when
    // SourceAppIconBytes is null (metadata-only list reads from U12). (U12)
    public bool HasSourceAppIcon => Clip.SourceAppIconAvailable;

    public bool ShowTypeGlyph => !HasSourceAppIcon;

    public Bitmap? PreviewThumbnailImage
    {
        get
        {
            if (_previewThumbnailLoaded)
            {
                return _previewThumbnailImage;
            }

            if (Clip.ContentType != ContentType.Image)
            {
                _previewThumbnailLoaded = true;
                return null;
            }

            if (Clip.ContentBytes is null)
            {
                // Metadata-only list read (U12) omitted the image bytes; pull the
                // full entry, then this getter re-runs when PreviewThumbnailImage re-raises.
                _ = EnsureContentHydratedAsync();
                return null;
            }

            _previewThumbnailLoaded = true;
            LoadBitmapInBackground(Clip.ContentBytes, bitmap =>
            {
                _previewThumbnailImage = bitmap;
                this.RaisePropertyChanged(nameof(PreviewThumbnailImage));
            },
            // The row draws this into an 84x48 box, but a decoded bitmap costs
            // width x height x 4 bytes whatever size it is drawn at. Decoding a
            // 4000x3000 screenshot in full held ~48 MB per row. ImageWidth comes off
            // the list read, so the guard costs nothing; a source narrower than the
            // target is decoded plainly rather than upscaled.
            decodeWidth: Clip.ImageWidth is > ThumbnailDecodeWidth ? ThumbnailDecodeWidth : null);
            return _previewThumbnailImage;
        }
    }

    /// <summary>
    /// List/search reads omit the image BLOB (U12). When this clip is shown as a
    /// thumbnail or selected, lazily reload the full entry by id so the bytes are
    /// present for rendering, edit, export, drag, and AI-image. No-op once started or
    /// when the entry already carries its bytes. The source-app icon has its own,
    /// much cheaper loader - see <see cref="EnsureSourceAppIconAsync"/>.
    /// </summary>
    public async Task EnsureContentHydratedAsync()
    {
        if (_contentHydrator is null || _contentHydrationStarted || _isDisposed)
        {
            return;
        }
        var needsImage = Clip.ContentType == ContentType.Image && Clip.ContentBytes is null;
        if (!needsImage)
        {
            return;
        }
        _contentHydrationStarted = true;
        try
        {
            var full = await _contentHydrator(Clip.Id).ConfigureAwait(false);
            if (full is null || _isDisposed)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed)
                {
                    return;
                }
                Clip = full;
                _previewThumbnailLoaded = false;
                this.RaisePropertyChanged(nameof(PreviewThumbnailImage));
            });
        }
        catch (Exception ex)
        {
            _contentHydrationStarted = false; // allow a later retry
            System.Diagnostics.Trace.TraceWarning($"Clip {Clip.Id} content hydration failed: {ex.Message}");
        }
    }

    // ContentBytes may be null on metadata-only list reads (U12); test ContentType only so the
    // thumbnail placeholder renders correctly. Actual bytes load when the clip is opened/selected.
    public bool ShowPreviewThumbnail => Clip.ContentType == ContentType.Image;

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

    public bool IsImported => string.Equals(Clip.ImportKind, ClipImportKinds.DragDrop, StringComparison.Ordinal);

    public string ImportedBadgeLabel => AppText.ClipDragImportBadgeLabel;

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
        _metaLine = BuildMetaLine();
        this.RaisePropertyChanged(nameof(MetaLine));
        _metaSegments = BuildMetaSegments();
        this.RaisePropertyChanged(nameof(MetaSegments));
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
        _metaLine = BuildMetaLine();
        this.RaisePropertyChanged(nameof(MetaLine));
        _metaSegments = BuildMetaSegments();
        this.RaisePropertyChanged(nameof(MetaSegments));
    }

    public void Dispose()
    {
        _isDisposed = true;
        _commandErrors.Dispose();
        _previewThumbnailImage?.Dispose();
        _sourceAppIconImage?.Dispose();
    }

    private void LoadBitmapInBackground(byte[]? bytes, Action<Bitmap?> apply, int? decodeWidth = null)
    {
        if (bytes is not { Length: > 0 })
        {
            return;
        }

        _ = Task.Run(() => ClipBitmapFactory.TryLoad(bytes, decodeWidth))
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
