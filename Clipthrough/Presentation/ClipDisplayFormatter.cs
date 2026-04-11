using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Clipthrough.Localization;
using Clipthrough.Models;

namespace Clipthrough.Presentation;

public static class ClipDisplayFormatter
{
    public static string BuildTitle(ClipEntry clip)
    {
        if (clip.ContentType == ContentType.Image && TryGetImageDimensionsDisplay(clip) is { } dimensions)
        {
            return AppText.FormatImageSummary(dimensions);
        }

        return BuildTitle(clip.Content, clip.ContentType);
    }

    public static string BuildTitle(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AppText.GetEmptyClipTitle(contentType);
        }

        var firstLine = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? content.Trim();

        return firstLine.Length <= 90 ? firstLine : $"{firstLine[..87]}...";
    }

    public static string BuildPreviewSnippet(ClipEntry clip)
    {
        if (clip.ContentType == ContentType.Image && TryGetImageDimensionsDisplay(clip) is { } dimensions)
        {
            return AppText.FormatImageSummary(dimensions);
        }

        return BuildPreviewSnippet(clip.Content, clip.ContentType);
    }

    public static string BuildPreviewSnippet(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AppText.GetEmptyPreviewSnippet(contentType);
        }

        var collapsed = CollapseWhitespace(content);
        return string.IsNullOrWhiteSpace(collapsed)
            ? AppText.PreviewTextUnavailable
            : collapsed.Length <= 140 ? collapsed : $"{collapsed[..137]}...";
    }

    public static string BuildSingleLinePreview(ClipEntry clip)
    {
        if (clip.ContentType == ContentType.Image && TryGetImageDimensionsDisplay(clip) is { } dimensions)
        {
            return AppText.FormatImageSummary(dimensions);
        }

        return BuildSingleLinePreview(clip.Content, clip.ContentType);
    }

    public static string BuildSingleLinePreview(string content, ContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AppText.GetEmptySingleLinePreview(contentType);
        }

        var collapsed = CollapseWhitespace(content);
        return string.IsNullOrWhiteSpace(collapsed)
            ? AppText.EmptyClip
            : collapsed.Length <= 88 ? collapsed : $"{collapsed[..85]}...";
    }

    public static string BuildRenderedText(ClipEntry? clip, IReadOnlyList<string> fileItems)
    {
        if (clip is null)
        {
            return AppText.PreviewSelectContent;
        }

        if (string.IsNullOrWhiteSpace(clip.Content))
        {
            return clip.ContentType switch
            {
                ContentType.Image => AppText.PreviewEmptyImageData,
                ContentType.Files => AppText.PreviewEmptyFilesData,
                ContentType.RichText => AppText.PreviewEmptyRichTextData,
                _ => AppText.PreviewEmptyClip,
            };
        }

        return clip.ContentType switch
        {
            ContentType.RichText => RenderRichContent(clip.Content),
            ContentType.Files => fileItems.Count == 0
                ? NormalizePreviewText(clip.Content)
                : AppText.FormatFileCount(fileItems.Count),
            _ => NormalizePreviewText(clip.Content),
        };
    }

    public static string GetRawContentDisplay(ClipEntry? clip)
    {
        if (clip is null)
        {
            return AppText.PreviewSelectRawContent;
        }

        if (!string.IsNullOrWhiteSpace(clip.Content))
        {
            return clip.Content;
        }

        return clip.ContentType switch
        {
            ContentType.Image when TryGetImageDimensionsDisplay(clip) is { } dimensions => AppText.FormatImageSummary(dimensions),
            ContentType.Image => AppText.PreviewEmptyImageData,
            ContentType.Files => AppText.PreviewEmptyFilesData,
            ContentType.RichText => AppText.PreviewEmptyRichTextData,
            _ => AppText.PreviewEmptyClip,
        };
    }

    public static IReadOnlyList<string> BuildFileItems(string? content)
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

    public static string BuildImageHint(ClipEntry? clip, bool hasPreview)
    {
        if (clip is null)
        {
            return AppText.PreviewSelectImage;
        }

        if (hasPreview)
        {
            return TryGetImageDimensionsDisplay(clip) is { } dimensions
                ? string.Format(AppText.CurrentCulture, AppText.PreviewImageResolution, dimensions)
                : AppText.PreviewImageLoaded;
        }

        if (clip.ContentBytes is null || clip.ContentBytes.Length == 0)
        {
            return AppText.PreviewEmptyImageData;
        }

        return AppText.PreviewImageTextOnly;
    }

    public static string? TryGetImageDimensionsDisplay(ClipEntry? clip)
    {
        if (clip?.ImageWidth is not int width || clip.ImageHeight is not int height || width <= 0 || height <= 0)
        {
            return null;
        }

        return AppText.FormatImageDimensions(width, height);
    }

    public static string ToRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();

        if (delta.TotalMinutes < 1)
        {
            return AppText.JustNow;
        }

        if (delta.TotalHours < 1)
        {
            return AppText.FormatRelativeMinutes(Math.Max(1, (int)delta.TotalMinutes));
        }

        if (delta.TotalDays < 1)
        {
            return AppText.FormatRelativeHours(Math.Max(1, (int)delta.TotalHours));
        }

        return AppText.FormatRelativeDays(Math.Max(1, (int)delta.TotalDays));
    }

    public static string ToCapturedAtDisplay(DateTimeOffset timestamp) => timestamp.ToLocalTime().ToString("g", AppText.CurrentCulture);

    public static string ToCapturedAtCompact(DateTimeOffset timestamp) => timestamp.ToLocalTime().ToString("g", AppText.CurrentCulture);

    public static string NormalizePreviewText(string content)
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
            ? AppText.PreviewTextUnavailable
            : normalized;
    }

    public static string RenderRichContent(string content)
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

    private static string CollapseWhitespace(string content) => string.Join(" ", content
        .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool LooksLikeHtml(string content) => Regex.IsMatch(content, @"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase);

    private static bool LooksLikeRtf(string content) => content.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);

    private static string DecodeRtfHex(string token)
    {
        if (token.Length < 4)
        {
            return string.Empty;
        }

        var hexValue = token[^2..];
        return byte.TryParse(hexValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? ((char)value).ToString()
            : string.Empty;
    }
}
