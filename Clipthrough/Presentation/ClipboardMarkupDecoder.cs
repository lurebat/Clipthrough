using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clipthrough.Models;

namespace Clipthrough.Presentation;

public static class ClipboardMarkupDecoder
{
    private static readonly Regex s_cfHtmlHeaderRegex = new(@"(?<name>StartHTML|EndHTML|StartFragment|EndFragment):(?<value>\d{1,10})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string DecodeMarkupBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (HasBom(bytes, 0xEF, 0xBB, 0xBF))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).TrimEnd('\0');
        }

        if (HasBom(bytes, 0xFF, 0xFE))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
        }

        if (HasBom(bytes, 0xFE, 0xFF))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
        }

        if (LooksLikeUtf16LittleEndian(bytes))
        {
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        if (LooksLikeUtf16BigEndian(bytes))
        {
            return Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return GetWindows1252Encoding().GetString(bytes).TrimEnd('\0');
        }
    }

    public static string NormalizePlatformMarkupString(string value, ClipContentFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim('\0');
        if (LooksLikeMarkup(trimmed, format))
        {
            return trimmed;
        }

        var recovered = RecoverBytePairedString(trimmed);
        return LooksLikeMarkup(recovered, format) ? recovered : trimmed;
    }

    public static string ExtractHtmlFragment(string html)
    {
        if (!LooksLikeClipboardHtml(html))
        {
            return html;
        }

        var headerFragment = GetHeaderRegion(html, "StartFragment", "EndFragment");
        if (!string.IsNullOrWhiteSpace(headerFragment))
        {
            return headerFragment.Trim();
        }

        const string startFragmentMarker = "<!--StartFragment-->";
        const string endFragmentMarker = "<!--EndFragment-->";
        var markerStart = html.IndexOf(startFragmentMarker, StringComparison.OrdinalIgnoreCase);
        var markerEnd = html.IndexOf(endFragmentMarker, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0 && markerEnd > markerStart)
        {
            return html[(markerStart + startFragmentMarker.Length)..markerEnd].Trim();
        }

        var offsets = s_cfHtmlHeaderRegex.Matches(html);
        var startHtml = GetHeaderOffset(offsets, "StartHTML");
        var endHtml = GetHeaderOffset(offsets, "EndHTML");
        if (startHtml is >= 0 && endHtml > startHtml && endHtml <= html.Length)
        {
            return html[startHtml.Value..endHtml.Value].Trim();
        }

        var htmlIndex = html.IndexOf('<');
        return htmlIndex >= 0 ? html[htmlIndex..].Trim() : html;
    }

    public static string ExtractHtmlDocument(string html)
    {
        if (!LooksLikeClipboardHtml(html))
        {
            return html;
        }

        var document = GetHeaderRegion(html, "StartHTML", "EndHTML");
        if (!string.IsNullOrWhiteSpace(document))
        {
            return document.Trim();
        }

        var htmlIndex = html.IndexOf('<');
        return htmlIndex >= 0 ? html[htmlIndex..].Trim() : html;
    }

    public static string BuildHtmlRenderDocument(string html)
    {
        var document = ExtractHtmlDocument(html);
        if (string.IsNullOrWhiteSpace(document))
        {
            return document;
        }

        return document
            .Replace("<!--StartFragment-->", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<!--EndFragment-->", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static int? GetHeaderOffset(MatchCollection matches, string headerName)
    {
        foreach (Match match in matches)
        {
            if (!string.Equals(match.Groups["name"].Value, headerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw > int.MaxValue)
                return null;
            return (int)raw;
        }

        return null;
    }

    private static string? GetHeaderRegion(string html, string startHeaderName, string endHeaderName)
    {
        var offsets = s_cfHtmlHeaderRegex.Matches(html);
        var startOffset = GetHeaderOffset(offsets, startHeaderName);
        var endOffset = GetHeaderOffset(offsets, endHeaderName);
        if (startOffset is >= 0 && endOffset > startOffset && endOffset <= html.Length)
        {
            return html[startOffset.Value..endOffset.Value];
        }

        return null;
    }

    private static bool LooksLikeClipboardHtml(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("Format:HTML Format", StringComparison.OrdinalIgnoreCase)
               || s_cfHtmlHeaderRegex.IsMatch(value));

    private static string RecoverBytePairedString(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            bytes[index * 2] = (byte)(character & 0xFF);
            bytes[(index * 2) + 1] = (byte)(character >> 8);
        }

        return DecodeMarkupBytes(bytes);
    }

    private static bool LooksLikeMarkup(string value, ClipContentFormat format)
        => format switch
        {
            ClipContentFormat.Html => value.Contains("<html", StringComparison.OrdinalIgnoreCase)
                                      || value.Contains("<body", StringComparison.OrdinalIgnoreCase)
                                      || value.Contains("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase)
                                      || value.Contains("<img", StringComparison.OrdinalIgnoreCase)
                                      || value.StartsWith("Format:HTML Format", StringComparison.OrdinalIgnoreCase)
                                      || value.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
                                      || Regex.IsMatch(value, @"<\s*[a-zA-Z][^>]*>", RegexOptions.CultureInvariant),
            ClipContentFormat.Rtf => value.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool HasBom(byte[] bytes, params byte[] bom)
    {
        if (bytes.Length < bom.Length)
        {
            return false;
        }

        for (var index = 0; index < bom.Length; index++)
        {
            if (bytes[index] != bom[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeUtf16LittleEndian(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var zeroCount = 0;
        var sampleCount = 0;
        for (var index = 1; index < bytes.Length && sampleCount < 32; index += 2, sampleCount++)
        {
            if (bytes[index] == 0)
            {
                zeroCount++;
            }
        }

        return sampleCount > 0 && zeroCount >= sampleCount * 0.7;
    }

    private static bool LooksLikeUtf16BigEndian(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var zeroCount = 0;
        var sampleCount = 0;
        for (var index = 0; index < bytes.Length && sampleCount < 32; index += 2, sampleCount++)
        {
            if (bytes[index] == 0)
            {
                zeroCount++;
            }
        }

        return sampleCount > 0 && zeroCount >= sampleCount * 0.7;
    }

    private static Encoding GetWindows1252Encoding()
    {
        try
        {
            return Encoding.GetEncoding(1252);
        }
        catch (NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
