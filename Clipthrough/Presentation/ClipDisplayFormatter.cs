using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Clipthrough.Localization;
using Clipthrough.Models;

namespace Clipthrough.Presentation;

/// <summary>
/// The three display strings a list row needs, built together.
/// </summary>
public readonly record struct ClipDisplayStrings(string Title, string PreviewSnippet, string SingleLinePreview);

public static partial class ClipDisplayFormatter
{
    /// <summary>
    /// Resolves the clip's display text ONCE and derives all three strings from it.
    /// Each of the three used to resolve it independently, so a rich-text clip ran the
    /// whole HTML/RTF strip - four regex passes over the full content - three times per
    /// clip, synchronously in the row's constructor, for every row of every list build.
    /// Callers that need more than one string must use this rather than the single
    /// <see cref="ClipEntry"/> overloads.
    /// </summary>
    public static ClipDisplayStrings BuildDisplayStrings(ClipEntry clip)
    {
        if (clip.ContentType == ContentType.Image && TryGetPreferredImageLabel(clip) is { } imageLabel)
        {
            return BuildDisplayStrings(imageLabel, ContentType.Text);
        }

        if (clip.ContentType == ContentType.Image && TryGetImageDimensionsDisplay(clip) is { } dimensions)
        {
            var summary = AppText.FormatImageSummary(dimensions);
            return new ClipDisplayStrings(summary, summary, summary);
        }

        if (clip.ContentType == ContentType.RichText)
        {
            return BuildDisplayStrings(GetDisplayRichText(clip), ContentType.Text);
        }

        return BuildDisplayStrings(clip.Content, clip.ContentType);
    }

    private static ClipDisplayStrings BuildDisplayStrings(string content, ContentType contentType)
        => new(
            BuildTitle(content, contentType),
            BuildPreviewSnippet(content, contentType),
            BuildSingleLinePreview(content, contentType));

    public static string BuildTitle(ClipEntry clip) => BuildDisplayStrings(clip).Title;

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

    public static string BuildPreviewSnippet(ClipEntry clip) => BuildDisplayStrings(clip).PreviewSnippet;

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

    public static string BuildSingleLinePreview(ClipEntry clip) => BuildDisplayStrings(clip).SingleLinePreview;

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
            ContentType.RichText => GetDisplayRichText(clip),
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

        var rawMarkup = GetRawMarkup(clip);
        if (!string.IsNullOrWhiteSpace(rawMarkup))
        {
            return rawMarkup;
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
            // CA1863 wants a cached CompositeFormat, but AppText.PreviewImageResolution
            // is a localizable entry that a language switch is meant to replace at
            // runtime. Parsing it once into a static would freeze the string chosen at
            // first use, which is the exact bug A19 (runtime language switching) exists
            // to avoid. One Format call per preview render is not worth that.
#pragma warning disable CA1863
            return TryGetImageDimensionsDisplay(clip) is { } dimensions
                ? string.Format(AppText.CurrentCulture, AppText.PreviewImageResolution, dimensions)
                : AppText.PreviewImageLoaded;
#pragma warning restore CA1863
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

    public static string? TryGetPreferredImageLabel(ClipEntry? clip)
    {
        if (clip?.ContentType != ContentType.Image || string.IsNullOrWhiteSpace(clip.Content))
        {
            return null;
        }

        var normalized = NormalizePreviewText(clip.Content);
        if (string.IsNullOrWhiteSpace(normalized) || normalized == AppText.PreviewTextUnavailable)
        {
            return null;
        }

        var defaultImageSummary = TryGetImageDimensionsDisplay(clip) is { } dimensions
            ? AppText.FormatImageSummary(dimensions)
            : null;
        return string.Equals(normalized, defaultImageSummary, StringComparison.Ordinal)
            ? null
            : normalized;
    }

    public static string? GetRawMarkup(ClipEntry? clip)
    {
        if (clip is null || clip.ContentBytes is not { Length: > 0 } bytes)
        {
            return null;
        }

        return clip.ContentFormat switch
        {
            ClipContentFormat.Html or ClipContentFormat.Rtf => ClipboardMarkupDecoder.NormalizePlatformMarkupString(
                ClipboardMarkupDecoder.DecodeMarkupBytes(bytes),
                clip.ContentFormat),
            _ => null,
        };
    }

    public static string ToRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();

        if (delta.TotalSeconds < 10)
        {
            return AppText.JustNow;
        }

        if (delta.TotalMinutes < 1)
        {
            return AppText.FormatRelativeSeconds(Math.Max(1, (int)delta.TotalSeconds));
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
        if (LooksLikeCfHtml(content))
        {
            content = ClipboardMarkupDecoder.ExtractHtmlFragment(content);
        }

        if (LooksLikeHtml(content))
        {
            var withoutScripts = ScriptOrStyleBlockRegex().Replace(content, string.Empty);
            var withListItems = ListItemTagRegex().Replace(withoutScripts, "• ");
            var withBreaks = BlockLevelTagRegex().Replace(withListItems, Environment.NewLine);
            var withoutTags = AnyTagRegex().Replace(withBreaks, string.Empty);
            return NormalizePreviewText(WebUtility.HtmlDecode(withoutTags));
        }

        if (LooksLikeRtf(content))
        {
            var withParagraphs = RtfParagraphRegex().Replace(content, Environment.NewLine);
            var withTabs = RtfTabRegex().Replace(withParagraphs, "\t");
            var withHexDecoded = RtfHexEscapeRegex().Replace(withTabs, static match => DecodeRtfHex(match.Value));
            var withoutControlWords = RtfControlWordRegex().Replace(withHexDecoded, string.Empty);
            var withoutGroups = withoutControlWords.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
            return NormalizePreviewText(withoutGroups);
        }

        return NormalizePreviewText(content);
    }

    private static string CollapseWhitespace(string content) => string.Join(" ", content
        .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string GetDisplayRichText(ClipEntry clip)
    {
        if (!string.IsNullOrWhiteSpace(clip.Content) && !LooksLikeRawRichText(clip.Content))
        {
            return NormalizePreviewText(clip.Content);
        }

        var rawMarkup = GetRawMarkup(clip);
        if (!string.IsNullOrWhiteSpace(rawMarkup))
        {
            return RenderRichContent(rawMarkup);
        }

        return RenderRichContent(clip.Content);
    }

    private static bool LooksLikeRawRichText(string content) => LooksLikeCfHtml(content) || LooksLikeHtml(content) || LooksLikeRtf(content);

    private static bool LooksLikeCfHtml(string content)
        => content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
           || content.StartsWith("Format:HTML Format", StringComparison.OrdinalIgnoreCase)
           || (content.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase)
               && content.Contains("EndHTML:", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHtml(string content) => HtmlTagProbeRegex().IsMatch(content);

    /// <summary>
    /// HTML and RTF are culture-neutral formats, so every one of these carries
    /// <see cref="RegexOptions.CultureInvariant"/>. Without it, case-insensitive matching
    /// follows the current culture, and under tr-TR an uppercase <c>&lt;LI&gt;</c> or
    /// <c>&lt;DIV&gt;</c> does not match its lower-case pattern at all - a Turkish user
    /// silently loses every bullet and paragraph break when pasting HTML. The other two
    /// markup call sites in this codebase (ClipboardMarkupDecoder, RichWebContentView)
    /// already pass it; this file was the one that did not.
    ///
    /// They are also source-generated rather than built per call. These run for every clip
    /// on every list build, so an interpreted engine and a shared static cache lookup are
    /// both avoidable per-row cost.
    /// </summary>
    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptOrStyleBlockRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItemTagRegex();

    [GeneratedRegex(@"</?(br|p|div|section|article|ul|ol|h[1-6]|tr)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockLevelTagRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagProbeRegex();

    [GeneratedRegex(@"\\par[d]? ?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfParagraphRegex();

    [GeneratedRegex(@"\\tab ?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfTabRegex();

    [GeneratedRegex(@"\\'[0-9a-fA-F]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex RtfHexEscapeRegex();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfControlWordRegex();

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
