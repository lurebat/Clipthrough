using System;
using System.Linq;
using AvaloniaApplication1.Models;

namespace AvaloniaApplication1.ViewModels;

public sealed class ClipItemViewModel : ViewModelBase
{
    public ClipItemViewModel(ClipEntry clip)
    {
        Clip = clip;
    }

    public ClipEntry Clip { get; }

    public long Id => Clip.Id;

    public string Title => BuildTitle(Clip.Content, Clip.ContentType);

    public string Preview => Clip.Content;

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

    public string Subtitle => $"{RelativeCapturedAt} · {DisplayContentType}";

    public string SourceApp => string.IsNullOrWhiteSpace(Clip.SourceApp) ? "Unknown source" : Clip.SourceApp;

    public string ByteSizeDisplay => $"{Clip.ByteSize:N0} bytes";

    public bool IsFavorite => Clip.IsFavorite;

    public bool IsSensitive => Clip.IsSensitive;

    public string SensitivitySummary => Clip.SensitivityMatches.Count == 0
        ? "No sensitive patterns matched"
        : string.Join(", ", Clip.SensitivityMatches.Select(static match => $"{match.RuleName} ({match.Severity})"));

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

