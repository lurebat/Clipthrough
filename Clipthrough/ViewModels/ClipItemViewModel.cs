using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Clipthrough.Models;

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

    public string Title => BuildTitle(Clip.Content, Clip.ContentType);

    public string Preview => Clip.Content;

    public string PreviewSnippet => BuildPreviewSnippet(Clip.Content, Clip.ContentType);

    public string SingleLinePreview => BuildSingleLinePreview(Clip.Content, Clip.ContentType);

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

    public string RelativeCapturedAt => ToRelativeTime(Clip.CapturedAt);

    public string CapturedAtDisplay => Clip.CapturedAt.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public string CapturedAtCompact => Clip.CapturedAt.ToLocalTime().ToString("MMM d HH:mm");

    public string Subtitle => $"{RelativeCapturedAt} · {DisplayContentType}";

    public string SourceApp => string.IsNullOrWhiteSpace(Clip.SourceApp) ? "Unknown source" : Clip.SourceApp;

    public string SourceSummary => $"{SourceApp} · {RelativeCapturedAt}";

    public string ByteSizeDisplay => $"{Clip.ByteSize:N0} bytes";

    public bool IsFavorite => Clip.IsFavorite;

    public bool IsSensitive => Clip.IsSensitive;

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
        ? "No sensitive patterns matched"
        : string.Join(", ", Clip.SensitivityMatches.Select(static match => $"{match.RuleName} ({match.Severity})"));

    public string FavoriteMarker => IsFavorite ? "★" : string.Empty;

    private static string BuildTitle(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return contentType switch
            {
                ContentType.Image => "Image clip",
                ContentType.Files => "File list clip",
                ContentType.RichText => "Rich text clip",
                _ => "Empty text clip",
            };
        }

        var firstLine = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? content.Trim();

        return firstLine.Length <= 90 ? firstLine : $"{firstLine[..87]}...";
    }

    private static string BuildPreviewSnippet(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return contentType switch
            {
                ContentType.Image => "Image data captured from the clipboard.",
                ContentType.Files => "File paths captured from the clipboard.",
                ContentType.RichText => "Formatted text content captured from the clipboard.",
                _ => "This clip does not contain previewable text.",
            };
        }

        var collapsed = string.Join(" ", content
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return "This clip does not contain previewable text.";
        }

        return collapsed.Length <= 140 ? collapsed : $"{collapsed[..137]}...";
    }

    private static string BuildSingleLinePreview(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return contentType switch
            {
                ContentType.Image => "Image clip",
                ContentType.Files => "File list",
                ContentType.RichText => "Rich text clip",
                _ => "Empty text clip",
            };
        }

        var collapsed = string.Join(" ", content
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return "Empty clip";
        }

        return collapsed.Length <= 88 ? collapsed : $"{collapsed[..85]}...";
    }

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

    private static string ToRelativeTime(DateTimeOffset timestamp)
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

