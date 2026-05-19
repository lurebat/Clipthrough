using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Clipthrough.Services.Platform;

/// <summary>
/// Shared helpers for wrapping rich content (HTML/RTF) in the formats that
/// Windows expects on the clipboard and the drag-and-drop data object. Used
/// by both <see cref="SystemInteractionService"/> (clipboard writes) and the
/// drag-and-drop service (drag-out payloads).
/// </summary>
internal static class RichClipboardFormatting
{
    private const string StartFragmentMarker = "<!--StartFragment-->";
    private const string EndFragmentMarker = "<!--EndFragment-->";
    private const string HeaderTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    /// <summary>
    /// Returns true when <paramref name="content"/> already carries the CF_HTML
    /// header. Avoids double-wrapping when re-emitting stored HTML.
    /// </summary>
    public static bool LooksLikeCfHtml(string content)
        => content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
           && content.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase)
           && content.Contains("StartFragment:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort detection of an HTML fragment vs. plain text.
    /// </summary>
    public static bool LooksLikeHtml(string content)
        => !string.IsNullOrWhiteSpace(content)
           && Regex.IsMatch(content, @"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Wraps an HTML fragment in the CF_HTML envelope expected by Word,
    /// Outlook, and other rich-text targets. Already-wrapped input is returned
    /// verbatim. Non-HTML input is escaped first so it round-trips as text.
    /// </summary>
    public static string BuildCfHtml(string html)
    {
        var fragment = html;
        if (LooksLikeCfHtml(fragment))
        {
            return fragment;
        }

        if (!LooksLikeHtml(fragment))
        {
            fragment = System.Net.WebUtility.HtmlEncode(fragment).Replace(Environment.NewLine, "<br>", StringComparison.Ordinal);
        }

        var document = $"<html><body>{StartFragmentMarker}{fragment}{EndFragmentMarker}</body></html>";
        var header = string.Format(HeaderTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(header);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body>");
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(StartFragmentMarker) + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(document);

        header = string.Format(HeaderTemplate, startHtml, endHtml, startFragment + Encoding.UTF8.GetByteCount(StartFragmentMarker), endFragment);
        return header + document;
    }

    /// <summary>
    /// Escapes non-ASCII characters in an RTF document using the \uN? form so
    /// it survives the ANSI-encoded "Rich Text Format" clipboard channel.
    /// </summary>
    public static string NormalizeRtfForClipboard(string richContent)
    {
        var builder = new StringBuilder(richContent.Length);
        foreach (var character in richContent)
        {
            if (character <= sbyte.MaxValue)
            {
                builder.Append(character);
                continue;
            }

            builder.Append("\\u");
            builder.Append(unchecked((short)character));
            builder.Append('?');
        }

        return builder.ToString();
    }
}
