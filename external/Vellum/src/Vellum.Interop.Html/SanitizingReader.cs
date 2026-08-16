using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace Vellum.Interop.Html;

/// <summary>
/// Configures <see cref="HtmlSanitizer"/> for what the Vellum document model can actually
/// represent, and reports what it removed.
/// </summary>
/// <remarks>
/// <para>
/// The sanitizing itself is deliberately not ours. An XSS filter is a block-list of everything
/// anyone has thought of so far, maintained against a research community that thinks of new ones;
/// a hand-written one is a list of the attacks its author happened to know about on the day. So the
/// policy here is configuration of a maintained implementation, not a reimplementation of it.
/// </para>
/// <para>
/// What is ours is the allow-list's shape, because that is a question about this editor rather than
/// about security in general: there is no point keeping an element the document model has nowhere
/// to put.
/// </para>
/// </remarks>
internal sealed class SanitizingReader
{
    /// <summary>
    /// Elements worth keeping. Anything else has its tags dropped and its children kept, so the
    /// text inside an unrecognised wrapper survives.
    /// </summary>
    private static readonly string[] Tags =
    [
        // The document's own scaffolding. These are not content and never become blocks, but
        // removing them takes the body with them and leaves nothing to walk.
        "html", "head", "body",

        // Blocks the model has a node for.
        "p", "div", "br", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "pre", "ul", "ol", "li", "hr",

        // Tables.
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",

        // Inline formatting.
        "span", "a", "img", "strong", "b", "em", "i", "u", "ins", "s", "strike", "del",
        "sup", "sub", "mark", "small", "big", "code", "tt", "kbd", "samp", "var", "font",
        "abbr", "cite", "q", "time", "dfn", "wbr",

        // Containers that carry no formatting but do carry block structure.
        "section", "article", "main", "header", "footer", "aside", "nav",
        "figure", "figcaption", "dl", "dt", "dd", "center", "address",
    ];

    private static readonly string[] Attributes =
    [
        "href", "src", "alt", "title", "style", "dir", "lang",
        "colspan", "rowspan", "start", "type", "reversed", "value",
        "width", "height", "align", "valign", "color", "face", "size", "cite",
    ];

    /// <summary>
    /// CSS properties the model can express. Everything else is removed, which matters more than
    /// it looks: <c>position</c>, <c>behavior</c> and <c>-moz-binding</c> have all been script
    /// vectors, and none of them mean anything in a document model with no layout of its own.
    /// </summary>
    private static readonly string[] CssProperties =
    [
        "font-weight", "font-style", "font-family", "font-size",
        "text-decoration", "text-decoration-line", "text-decoration-style",
        "color", "background-color", "background",
        "text-align", "vertical-align", "direction",
        "margin-left", "margin-inline-start", "padding-left", "padding-inline-start",
        "text-indent", "list-style-type", "white-space",
    ];

    private static readonly string[] Schemes = ["http", "https", "mailto", "tel"];

    /// <summary>
    /// Media types an inline <c>data:</c> image may declare.
    /// </summary>
    /// <remarks>
    /// <c>image/svg+xml</c> is deliberately absent, and its absence is the whole reason this list
    /// is written out rather than expressed as "anything beginning image/". SVG is a document
    /// format that can carry script and fetch external references, so an SVG data URL is markup
    /// injection wearing an image's name — and it arrives having already passed every check that
    /// looks at the word before the slash.
    /// </remarks>
    private static readonly HashSet<string> DataImageTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/jpg", "image/gif",
            "image/bmp", "image/webp", "image/tiff",
            "image/x-icon", "image/vnd.microsoft.icon",
        };

    private readonly HtmlImportOptions _options;
    private readonly List<ImportDiagnostic> _diagnostics;
    private readonly HashSet<string> _alreadyReported = new(StringComparer.OrdinalIgnoreCase);

    internal SanitizingReader(HtmlImportOptions options, List<ImportDiagnostic> diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>Sanitizes a parsed document in place.</summary>
    /// <param name="document">The document to clean.</param>
    internal void Sanitize(IDocument document)
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(Attributes, StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(CssProperties, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(Schemes, StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType>(),
            UriAttributes = new HashSet<string>(["href", "src", "cite"], StringComparer.OrdinalIgnoreCase),
        })
        {
            // An unknown element's tags go but its content stays. A fragment wrapped in something
            // we do not recognise is still a fragment, and throwing away the text inside it would
            // lose the thing the user actually copied.
            KeepChildNodes = true,

            // "data-*" attributes are an application's private state. They mean nothing here.
            AllowDataAttributes = false,
        };

        sanitizer.RemovingTag += (_, e) => ReportOnce(
            DiagnosticSeverity.Dropped,
            "Removed an element that cannot appear in a document.",
            e.Tag.NodeName.ToLowerInvariant());

        sanitizer.RemovingAttribute += (_, e) => ReportOnce(
            DiagnosticSeverity.Dropped,
            "Removed an attribute that is not safe to keep.",
            e.Attribute.Name.ToLowerInvariant());

        sanitizer.RemovingStyle += (_, e) => ReportOnce(
            DiagnosticSeverity.Downgraded,
            "Removed a style the editor cannot represent.",
            e.Style.Name.ToLowerInvariant());

        sanitizer.FilterUrl += OnFilterUrl;

        // The base address has to reach the sanitizer, not just the walker: it resolves relative
        // URLs before it checks their scheme, so without it a relative address is judged on its
        // own and there is nothing left for the walker to resolve afterwards.
        sanitizer.SanitizeDom(
            (IHtmlDocument)document,
            context: null,
            baseUrl: _options.BaseUri?.AbsoluteUri ?? string.Empty);
    }

    /// <summary>
    /// Applies the two URL decisions the sanitizer cannot make for us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first is that a remote image is not merely a picture. Fetching one tells whoever sent
    /// the document that it was opened, by which address and when, so it stays out unless the host
    /// has said otherwise — and the sanitizer will happily have approved it already, because
    /// <c>https</c> is a perfectly good scheme. Refusing it therefore means taking an approval
    /// back, not declining to grant one.
    /// </para>
    /// <para>
    /// The second is the reverse: <c>data:</c> is not an allowed scheme, which is right for a
    /// hyperlink — <c>data:text/html</c> in an <c>href</c> is a navigable document — and wrong for
    /// the <c>&lt;img&gt;</c> that a pasted picture actually arrives as. So the exception is
    /// granted, as narrowly as the event allows: an image element, the value that is actually its
    /// source rather than some other URL attribute that happens to sit on it, a declared media type
    /// on the list above, and a length agreed in advance.
    /// </para>
    /// </remarks>
    private void OnFilterUrl(object? sender, FilterUrlEventArgs e)
    {
        var original = e.OriginalUrl;

        if (original is null)
        {
            return;
        }

        // The event says which element the URL came from but not which attribute, and an element
        // can carry more than one. Matching the value against the source attribute is what keeps
        // the exception below from leaking onto an "href" that happens to sit on an image.
        var isImageSource =
            e.Tag is IHtmlImageElement
            && string.Equals(e.Tag.GetAttribute("src"), original, StringComparison.Ordinal);

        if (e.SanitizedUrl is not null)
        {
            // The sanitizer resolves relative addresses against the base the caller gave it. If
            // one is still relative afterwards there was no base, and keeping it would leave the
            // address to be resolved against whatever the host happens to be showing later.
            if (!Uri.TryCreate(e.SanitizedUrl, UriKind.Absolute, out _))
            {
                e.SanitizedUrl = null;

                ReportOnce(
                    DiagnosticSeverity.Dropped,
                    "Removed an address that is relative to a document this one did not come with.",
                    original);
                return;
            }

            if (isImageSource
                && !_options.AllowRemoteImages
                && !original.AsSpan().TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;

                ReportOnce(
                    DiagnosticSeverity.Dropped,
                    "Removed an image loaded from the network, which would have told its host that "
                    + "this document was opened.",
                    "img");
            }

            return;
        }

        if (!original.AsSpan().TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            ReportOnce(
                DiagnosticSeverity.Dropped,
                "Removed a link whose address is not http, https, mailto or tel.",
                SchemeOf(original));
            return;
        }

        if (!isImageSource)
        {
            ReportOnce(
                DiagnosticSeverity.Dropped,
                "Removed inline data used somewhere other than an image source.",
                "data:");
            return;
        }

        if (!_options.AllowDataImages)
        {
            ReportOnce(DiagnosticSeverity.Dropped, "Removed an inline image.", "data:");
            return;
        }

        if (!IsAllowedDataImage(original))
        {
            ReportOnce(
                DiagnosticSeverity.Dropped,
                "Removed inline data that does not declare a supported image type.",
                MediaTypeOf(original));
            return;
        }

        switch (MeasureDataImage(original, _options.MaxDataImageBytes))
        {
            case DataImageVerdict.TooLarge:
                ReportOnce(
                    DiagnosticSeverity.Dropped,
                    $"Removed an inline image larger than {_options.MaxDataImageBytes} bytes.",
                    "data:");
                return;

            case DataImageVerdict.Undecodable:
                ReportOnce(
                    DiagnosticSeverity.Dropped,
                    "Removed an inline image whose data is not valid base64.",
                    "data:");
                return;
        }

        e.SanitizedUrl = original;
    }

    /// <summary>What inspecting the payload of a <c>data:</c> image concluded.</summary>
    private enum DataImageVerdict
    {
        /// <summary>The payload decodes and is within the size limit.</summary>
        Usable,

        /// <summary>The payload decodes to more bytes than are allowed.</summary>
        TooLarge,

        /// <summary>The payload does not decode at all.</summary>
        Undecodable,
    }

    /// <summary>Checks that a <c>data:</c> image decodes, and how large it really is.</summary>
    /// <param name="url">The whole data URL.</param>
    /// <param name="maxBytes">The largest decoded size to allow.</param>
    /// <returns>What the payload turned out to be.</returns>
    /// <remarks>
    /// The limit is named in bytes and has to be measured in bytes. Base64 spends four characters
    /// on every three bytes, so judging the URL's length instead silently applies a limit a quarter
    /// tighter than the one that was asked for.
    /// <para>
    /// Decoding is also the only way to find out whether the payload is an image at all rather than
    /// a broken paste. A run of characters that cannot decode would otherwise reach the document as
    /// an embed and fail later, at draw time, far away from anything that could explain it.
    /// </para>
    /// </remarks>
    private static DataImageVerdict MeasureDataImage(string url, int maxBytes)
    {
        var comma = url.IndexOf(',', StringComparison.Ordinal);

        if (comma < 0)
        {
            return DataImageVerdict.Undecodable;
        }

        var header = url.AsSpan(0, comma);
        var payload = url.AsSpan(comma + 1);

        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            // A payload that is not base64 is percent-encoded text. It carries at most one byte per
            // character, so its length is a sound upper bound and no decode is needed.
            return payload.Length > maxBytes ? DataImageVerdict.TooLarge : DataImageVerdict.Usable;
        }

        // Four characters become three bytes, so the decoded size is at least this, less padding.
        // Checking it first means an enormous payload is rejected without allocating room for it.
        if ((long)(payload.Length / 4) * 3 - 2 > maxBytes)
        {
            return DataImageVerdict.TooLarge;
        }

        var buffer = new byte[(payload.Length / 4 * 3) + 3];

        if (!Convert.TryFromBase64Chars(payload, buffer, out var written))
        {
            return DataImageVerdict.Undecodable;
        }

        if (written == 0)
        {
            return DataImageVerdict.Undecodable;
        }

        return written > maxBytes ? DataImageVerdict.TooLarge : DataImageVerdict.Usable;
    }

    private static bool IsAllowedDataImage(string url) => DataImageTypes.Contains(MediaTypeOf(url));

    private static string MediaTypeOf(string url)
    {
        var span = url.AsSpan().Trim();

        if (!span.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        span = span[5..];

        var comma = span.IndexOf(',');

        if (comma < 0)
        {
            return string.Empty;
        }

        var header = span[..comma];
        var semicolon = header.IndexOf(';');

        return (semicolon < 0 ? header : header[..semicolon]).Trim().ToString();
    }

    private static string SchemeOf(string url)
    {
        var colon = url.IndexOf(':');

        return colon <= 0 ? "relative" : url[..colon].Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Records a diagnostic the first time each distinct thing is removed.
    /// </summary>
    /// <remarks>
    /// A page's worth of HTML can contain thousands of removals of the same kind. Reporting each
    /// one turns a useful list into a wall the caller cannot read, and gives the source control
    /// over how much memory the diagnostics take.
    /// </remarks>
    private void ReportOnce(DiagnosticSeverity severity, string message, string context)
    {
        if (_alreadyReported.Add($"{severity}|{context}"))
        {
            _diagnostics.Add(new ImportDiagnostic(severity, message, context));
        }
    }
}
