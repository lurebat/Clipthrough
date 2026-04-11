using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.ViewModels;

public sealed class ClipItemViewModel : ViewModelBase
{
    private static readonly IBrush s_defaultAccentBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush s_favoriteAccentBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush s_sensitiveAccentBrush = new SolidColorBrush(Color.Parse("#F43F5E"));
    private static readonly IBrush s_favoriteSensitiveAccentBrush = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush s_defaultBorderBrush = new SolidColorBrush(Color.Parse("#243247"));

    public ClipItemViewModel(ClipEntry clip)
    {
        Clip = clip;
    }

    public ClipEntry Clip { get; }

    public long Id => Clip.Id;

    public string Title => ClipDisplayFormatter.BuildTitle(Clip.Content, Clip.ContentType);

    public string Preview => Clip.Content;

    public string PreviewSnippet => ClipDisplayFormatter.BuildPreviewSnippet(Clip.Content, Clip.ContentType);

    public string SingleLinePreview => ClipDisplayFormatter.BuildSingleLinePreview(Clip.Content, Clip.ContentType);

    public string FullContent => Clip.Content;

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

    public string SourceSummary => HasMultipleCopies
        ? $"{SourceApp} · {RelativeCapturedAt} · {CopyCountDisplay}"
        : $"{SourceApp} · {RelativeCapturedAt}";

    public string ByteSizeDisplay => AppText.FormatByteCount(Clip.ByteSize);

    public bool IsFavorite => Clip.IsFavorite;

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

    private static int GetSeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0,
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

