using System.Text;

namespace Vellum.Interop.Html;

/// <summary>
/// Unwraps the <c>CF_HTML</c> envelope that Windows puts around HTML on the clipboard.
/// </summary>
/// <remarks>
/// The format is a plain-text header followed by a whole HTML document, with a fragment marked out
/// inside it:
/// <code>
/// Version:0.9
/// StartHTML:00000097
/// EndHTML:00000203
/// StartFragment:00000133
/// EndFragment:00000167
/// SourceURL:https://example.com/
/// &lt;html&gt;&lt;body&gt;&lt;!--StartFragment--&gt;the bit that was copied&lt;!--EndFragment--&gt;&lt;/body&gt;&lt;/html&gt;
/// </code>
/// Importing the whole document instead of the fragment is how a paste of three words ends up
/// carrying a page's navigation bar with it.
/// </remarks>
public static class ClipboardHtml
{
    private const string StartFragmentComment = "<!--StartFragment-->";
    private const string EndFragmentComment = "<!--EndFragment-->";

    /// <summary>Whether a string looks like a <c>CF_HTML</c> payload rather than plain HTML.</summary>
    /// <param name="text">The candidate.</param>
    /// <returns>Whether it carries a <c>CF_HTML</c> header.</returns>
    public static bool IsClipboardHtml(string? text) =>
        text is not null
        && text.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
        && text.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the copied fragment, or returns the input unchanged when it is not <c>CF_HTML</c>.
    /// </summary>
    /// <param name="text">The clipboard payload.</param>
    /// <param name="sourceUri">The <c>SourceURL</c> from the header, if it declared a usable one.</param>
    /// <returns>The HTML to import.</returns>
    /// <remarks>
    /// The fragment comments are preferred over the byte offsets in the header, even though the
    /// offsets are the format's own mechanism. The offsets are counted in UTF-8 bytes against the
    /// whole payload, which several widely used applications get wrong by a few bytes; the comments
    /// cannot be off by a few bytes because they are the thing being pointed at. The offsets are
    /// used when the comments are missing.
    /// </remarks>
    public static string ExtractFragment(string text, out Uri? sourceUri)
    {
        sourceUri = null;

        if (string.IsNullOrEmpty(text) || !IsClipboardHtml(text))
        {
            return text ?? string.Empty;
        }

        var header = ReadHeader(text, out var headerEnd);

        if (header.TryGetValue("SourceURL", out var source)
            && Uri.TryCreate(source, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            sourceUri = parsed;
        }

        var start = text.IndexOf(StartFragmentComment, StringComparison.OrdinalIgnoreCase);

        if (start >= 0)
        {
            start += StartFragmentComment.Length;

            var end = text.IndexOf(EndFragmentComment, start, StringComparison.OrdinalIgnoreCase);

            if (end >= 0)
            {
                return text[start..end];
            }

            // A start with no end. Everything after it is still better than everything.
            return text[start..];
        }

        if (TryByteOffsets(text, header, "StartFragment", "EndFragment", out var byOffset)
            || TryByteOffsets(text, header, "StartHTML", "EndHTML", out byOffset))
        {
            return byOffset;
        }

        // A header we could not make sense of. Whatever follows it is the best guess available,
        // and is certainly better than importing the header as if it were content.
        return headerEnd < text.Length ? text[headerEnd..] : string.Empty;
    }

    private static Dictionary<string, string> ReadHeader(string text, out int headerEnd)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        headerEnd = 0;

        // The header runs until the first line that is not "Name:value". In practice that is the
        // line the HTML starts on.
        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var line = lineEnd < 0 ? text[pos..] : text[pos..lineEnd];
            var trimmed = line.TrimEnd('\r');
            var colon = trimmed.IndexOf(':');

            if (colon <= 0 || trimmed.StartsWith('<'))
            {
                break;
            }

            var name = trimmed[..colon];

            // A header name is a bare word. Anything else means we have run into content that
            // happens to contain a colon.
            if (!IsHeaderName(name))
            {
                break;
            }

            header[name] = trimmed[(colon + 1)..];

            if (lineEnd < 0)
            {
                pos = text.Length;
                break;
            }

            pos = lineEnd + 1;
            headerEnd = pos;
        }

        return header;
    }

    private static bool IsHeaderName(string name)
    {
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return name.Length > 0;
    }

    private static bool TryByteOffsets(
        string text,
        Dictionary<string, string> header,
        string startKey,
        string endKey,
        out string fragment)
    {
        fragment = string.Empty;

        if (!header.TryGetValue(startKey, out var startText)
            || !header.TryGetValue(endKey, out var endText)
            || !int.TryParse(startText.Trim(), out var startByte)
            || !int.TryParse(endText.Trim(), out var endByte)
            || startByte < 0
            || endByte <= startByte)
        {
            return false;
        }

        // The offsets count UTF-8 bytes, and the payload we hold is UTF-16 characters. Any header
        // containing a non-ASCII SourceURL — or content before the fragment that is not ASCII —
        // makes the two disagree, so the conversion has to actually happen rather than be assumed.
        var bytes = Encoding.UTF8.GetBytes(text);

        if (startByte >= bytes.Length)
        {
            return false;
        }

        if (endByte > bytes.Length)
        {
            endByte = bytes.Length;
        }

        // A truncated payload can cut a multi-byte character in half. UTF8Encoding's default
        // decoder substitutes a replacement character rather than throwing, which is the behaviour
        // we want here.
        fragment = Encoding.UTF8.GetString(bytes, startByte, endByte - startByte);
        return true;
    }

    /// <summary>
    /// Builds a <c>CF_HTML</c> payload around a fragment, filling in the byte offsets.
    /// </summary>
    /// <param name="fragmentHtml">The HTML fragment to wrap.</param>
    /// <param name="sourceUri">An address to record as the fragment's origin, or null.</param>
    /// <returns>The payload, ready to put on the clipboard.</returns>
    /// <remarks>
    /// The offsets are counted after encoding, not before, because they are defined in bytes. A
    /// writer that formats the header, then counts characters, produces a payload that is correct
    /// only while every character in it is ASCII.
    /// </remarks>
    public static string Wrap(string fragmentHtml, Uri? sourceUri = null)
    {
        ArgumentNullException.ThrowIfNull(fragmentHtml);

        var source = sourceUri is null ? string.Empty : $"SourceURL:{sourceUri.AbsoluteUri}\r\n";

        // The offsets are ten digits wide so that writing the real values back over the
        // placeholders cannot change the length of the header, which would change the offsets.
        const string placeholder = "0000000000";

        var header =
            $"Version:0.9\r\nStartHTML:{placeholder}\r\nEndHTML:{placeholder}\r\n"
            + $"StartFragment:{placeholder}\r\nEndFragment:{placeholder}\r\n{source}";

        var prefix = "<html><body>\r\n" + StartFragmentComment;
        var suffix = EndFragmentComment + "\r\n</body></html>";

        var body = prefix + fragmentHtml + suffix;

        var startHtml = Encoding.UTF8.GetByteCount(header);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragmentHtml);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(body);

        // Rewritten over the header alone, never over the payload: the fragment is arbitrary text
        // and could perfectly well contain the placeholder string itself.
        var filled = header
            .Replace($"StartHTML:{placeholder}", $"StartHTML:{startHtml:D10}", StringComparison.Ordinal)
            .Replace($"EndHTML:{placeholder}", $"EndHTML:{endHtml:D10}", StringComparison.Ordinal)
            .Replace($"StartFragment:{placeholder}", $"StartFragment:{startFragment:D10}", StringComparison.Ordinal)
            .Replace($"EndFragment:{placeholder}", $"EndFragment:{endFragment:D10}", StringComparison.Ordinal);

        return filled + body;
    }
}
