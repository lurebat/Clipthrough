using System.Globalization;
using System.Text;

namespace Vellum.Interop.Html;

/// <summary>
/// Writes a document as HTML, in the dialect <see cref="HtmlImporter"/> reads back.
/// </summary>
/// <remarks>
/// <para>
/// Every choice here is made against the importer rather than against a style guide: this is the
/// output half of a round trip, and a construct written in a form the importer does not read is
/// a construct silently lost the moment a user copies and pastes within the application. Where
/// the two formats disagree the round-trip tests are the arbiter, not this comment.
/// </para>
/// <para>
/// The output also has to survive Word and Outlook, which is why formatting is carried by
/// presentational elements and inline <c>style</c> attributes rather than by classes and a
/// stylesheet. A stylesheet does not travel on the clipboard.
/// </para>
/// </remarks>
public sealed class HtmlExporter : IDocumentExporter
{
    /// <summary>
    /// The width of one indent level in pixels, which must stay equal to the importer's step or
    /// an indent gains or loses a level on every round trip.
    /// </summary>
    private const double IndentPixels = 48;

    /// <summary>A shared instance. The exporter holds no state.</summary>
    public static HtmlExporter Instance { get; } = new();

    /// <inheritdoc/>
    public string Format => "html";

    /// <inheritdoc/>
    public string MediaType => "text/html";

    /// <inheritdoc/>
    public string Export(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var html = new StringBuilder();

        WriteBlocks(html, doc.Blocks);

        return html.ToString();
    }

    /// <summary>Writes the document wrapped in the clipboard's CF_HTML envelope.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="sourceUri">A source URL to record, or null.</param>
    /// <returns>A complete CF_HTML payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    /// <remarks>
    /// Separate from <see cref="Export"/> because the envelope is a clipboard convention, not a
    /// property of the format: an exporter writing to a file must not emit it.
    /// </remarks>
    public string ExportForClipboard(DocumentNode doc, Uri? sourceUri = null) =>
        ClipboardHtml.Wrap(Export(doc), sourceUri);

    private static void WriteBlocks(StringBuilder html, IEnumerable<BlockNode> blocks)
    {
        foreach (var block in blocks)
        {
            WriteBlock(html, block);
        }
    }

    private static void WriteBlock(StringBuilder html, BlockNode block)
    {
        switch (block)
        {
            case ParagraphNode paragraph:
                WriteParagraph(html, paragraph);
                return;

            case ListNode list:
                WriteList(html, list);
                return;

            case TableNode table:
                WriteTable(html, table);
                return;

            case RuleNode:
                html.Append("<hr>");
                return;

            case BlockImageNode image:
                html.Append("<p");
                WriteStyleAttribute(html, AlignStyle(image.Align));
                html.Append('>');
                WriteImage(html, image.Image);
                html.Append("</p>");
                return;

            default:
                // Reachable only by growing the schema without extending this exporter, which is
                // exactly when silence would lose a user's content without a trace.
                throw new NotSupportedException(
                    $"The HTML exporter does not know how to write a '{block.TypeName}' block.");
        }
    }

    private static void WriteParagraph(StringBuilder html, ParagraphNode paragraph)
    {
        var tag = Tag(paragraph.Kind);
        var style = new List<string>();
        var preformatted = paragraph.Kind == ParagraphKind.Code;

        style.AddRange(AlignStyle(paragraph.Align));

        if (paragraph.IndentLevel > 0)
        {
            var pixels = paragraph.IndentLevel * IndentPixels;

            style.Add($"margin-left:{pixels.ToString("0.###", CultureInfo.InvariantCulture)}px");
        }

        // Only where the text would actually be damaged, so ordinary paragraphs stay clean markup
        // and only the ones that need defending carry the declaration.
        if (!preformatted && NeedsWhitespacePreserved(paragraph.Content.Text))
        {
            style.Add("white-space:pre-wrap");
            preformatted = true;
        }

        html.Append('<').Append(tag);
        WriteStyleAttribute(html, style);
        html.Append('>');

        // The parser throws away a newline directly after a <pre> start tag — a convenience for
        // hand-written markup, and a silent truncation for generated markup. Writing a second one
        // gives it something to throw away that was not the user's.
        if (tag == "pre" && paragraph.Content.Text.StartsWith('\n'))
        {
            html.Append('\n');
        }

        // <pre> is the one block whose text is significant as written. Everything else is read
        // back through HTML whitespace collapsing, so its content is escaped for that.
        WriteContent(html, paragraph.Content, preformatted);

        html.Append("</").Append(tag).Append('>');
    }

    /// <summary>
    /// Whether HTML's whitespace collapsing would change this text if it were written plainly.
    /// </summary>
    /// <param name="text">The paragraph's text.</param>
    /// <returns>True if any space in it needs defending.</returns>
    /// <remarks>
    /// Collapsing eats a leading space, a trailing space, the second and later of any run, and
    /// turns a tab into a space. A clipboard has no business reformatting what it was handed —
    /// two spaces after a full stop is a choice, not an accident — so a paragraph containing any
    /// of those is written with the rule switched off instead.
    /// </remarks>
    private static bool NeedsWhitespacePreserved(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\t' or '\f' or '\r')
            {
                return true;
            }

            if (c != ' ')
            {
                continue;
            }

            // A space survives only between two things, and a line break counts as neither.
            var before = i == 0 ? '\n' : text[i - 1];
            var after = i == text.Length - 1 ? '\n' : text[i + 1];

            if (before is ' ' or '\n' || after is ' ' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteList(StringBuilder html, ListNode list)
    {
        var tag = list.Kind == ListKind.Ordered ? "ol" : "ul";

        html.Append('<').Append(tag);

        // Only when it differs from the default, so the common case stays clean and a reader
        // that ignores the attribute is not misled by a redundant one.
        if (list.Kind == ListKind.Ordered && list.Start != 1)
        {
            html.Append(" start=\"")
                .Append(list.Start.ToString(CultureInfo.InvariantCulture))
                .Append('"');
        }

        html.Append('>');

        foreach (var item in list.Items)
        {
            html.Append("<li>");
            WriteBlocks(html, item.Blocks);
            html.Append("</li>");
        }

        html.Append("</").Append(tag).Append('>');
    }

    private static void WriteTable(StringBuilder html, TableNode table)
    {
        html.Append("<table>");

        foreach (var row in table.Rows)
        {
            html.Append("<tr>");

            foreach (var cell in row.Cells)
            {
                var tag = cell.IsHeader ? "th" : "td";

                html.Append('<').Append(tag);

                if (cell.RowSpan > 1)
                {
                    html.Append(" rowspan=\"")
                        .Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture))
                        .Append('"');
                }

                if (cell.ColumnSpan > 1)
                {
                    html.Append(" colspan=\"")
                        .Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture))
                        .Append('"');
                }

                if (cell.Background is { } background)
                {
                    WriteStyleAttribute(html, [$"background-color:{Css(background)}"]);
                }

                html.Append('>');
                WriteBlocks(html, cell.Blocks);
                html.Append("</").Append(tag).Append('>');
            }

            html.Append("</tr>");
        }

        html.Append("</table>");
    }

    /// <summary>Writes inline content, opening and closing mark elements as the marks change.</summary>
    /// <remarks>
    /// <para>
    /// The run boundaries are derived by walking the text and asking for the mark at each offset,
    /// not by iterating <see cref="InlineContent.Marks"/>. Mark spans are canonical, and canonical
    /// form omits stretches that override nothing — unformatted text has no spans at all — so
    /// driving the loop from them writes nothing for a plain paragraph.
    /// </para>
    /// <para>
    /// Each run is closed before the next opens rather than nesting elements across a boundary.
    /// Nesting would produce smaller output, but it only stays well-formed while marks nest — and
    /// they do not: a link can start in the middle of a bold run and end after it.
    /// </para>
    /// </remarks>
    private static void WriteContent(StringBuilder html, InlineContent content, bool preformatted)
    {
        var embed = 0;
        var start = 0;

        while (start < content.Length)
        {
            var mark = content.MarkAt(start);
            var end = start + 1;

            while (end < content.Length && content.MarkAt(end) == mark)
            {
                end++;
            }

            var open = new StringBuilder();
            var close = new StringBuilder();

            WriteMarkElements(mark, open, close);

            html.Append(open);

            for (var i = start; i < end; i++)
            {
                var c = content.Text[i];

                if (c == InlineContent.Placeholder)
                {
                    if (embed < content.Embeds.Length && content.Embeds[embed] is ImageEmbed image)
                    {
                        WriteImage(html, image);
                    }

                    embed++;
                    continue;
                }

                if (c == '\n' && !preformatted)
                {
                    // A newline inside a paragraph is a line break the user asked for. Written
                    // literally it would be collapsed to a space by every HTML reader there is.
                    html.Append("<br>");
                    continue;
                }

                Escape(html, c);
            }

            html.Append(close);
            start = end;
        }
    }

    /// <summary>
    /// Builds the opening and closing tags for one mark set.
    /// </summary>
    /// <remarks>
    /// The order matters only in that the two strings must mirror each other, so the closing tags
    /// are built by prepending as the opening tags are appended.
    /// </remarks>
    private static void WriteMarkElements(MarkSet mark, StringBuilder open, StringBuilder close)
    {
        void Wrap(string tag)
        {
            open.Append('<').Append(tag).Append('>');
            close.Insert(0, $"</{tag}>");
        }

        if (mark.Link is { } link)
        {
            open.Append("<a href=\"").Append(Attribute(link.Href)).Append('"');

            if (link.Title is { } title)
            {
                open.Append(" title=\"").Append(Attribute(title)).Append('"');
            }

            open.Append('>');
            close.Insert(0, "</a>");
        }

        var style = new List<string>();

        if (mark.FontFamily is { } family)
        {
            style.Add($"font-family:{CssFontFamily(family)}");
        }

        if (mark.FontSize is { } size)
        {
            style.Add($"font-size:{size.ToString("0.###", CultureInfo.InvariantCulture)}px");
        }

        if (mark.Foreground is { } foreground)
        {
            style.Add($"color:{Css(foreground)}");
        }

        if (mark.Highlight is { } highlight)
        {
            style.Add($"background-color:{Css(highlight)}");
        }

        if (style.Count > 0)
        {
            open.Append("<span");
            WriteStyleAttribute(open, style);
            open.Append('>');
            close.Insert(0, "</span>");
        }

        // Presentational elements rather than styled spans, because they survive being pasted
        // into applications that understand HTML only approximately.
        if (mark.Has(TextStyle.Bold))
        {
            Wrap("b");
        }

        if (mark.Has(TextStyle.Italic))
        {
            Wrap("i");
        }

        if (mark.Has(TextStyle.Underline))
        {
            Wrap("u");
        }

        if (mark.Has(TextStyle.Strikethrough))
        {
            Wrap("s");
        }

        if (mark.Has(TextStyle.Code))
        {
            Wrap("code");
        }

        if (mark.Has(TextStyle.Super))
        {
            Wrap("sup");
        }

        if (mark.Has(TextStyle.Sub))
        {
            Wrap("sub");
        }
    }

    private static void WriteImage(StringBuilder html, ImageEmbed image)
    {
        html.Append("<img src=\"").Append(Attribute(image.Source)).Append('"');

        if (image.AltText is { } alt)
        {
            html.Append(" alt=\"").Append(Attribute(alt)).Append('"');
        }

        if (image.Width is { } width)
        {
            html.Append(" width=\"")
                .Append(width.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('"');
        }

        if (image.Height is { } height)
        {
            html.Append(" height=\"")
                .Append(height.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('"');
        }

        html.Append('>');
    }

    private static void WriteStyleAttribute(StringBuilder html, IReadOnlyList<string> declarations)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        html.Append(" style=\"")
            .Append(Attribute(string.Join(';', declarations)))
            .Append('"');
    }

    private static string[] AlignStyle(TextAlign align) => align switch
    {
        TextAlign.Left => ["text-align:left"],
        TextAlign.Center => ["text-align:center"],
        TextAlign.Right => ["text-align:right"],
        TextAlign.Justify => ["text-align:justify"],
        _ => [],
    };

    private static string Tag(ParagraphKind kind) => kind switch
    {
        ParagraphKind.Heading1 => "h1",
        ParagraphKind.Heading2 => "h2",
        ParagraphKind.Heading3 => "h3",
        ParagraphKind.Heading4 => "h4",
        ParagraphKind.Heading5 => "h5",
        ParagraphKind.Heading6 => "h6",
        ParagraphKind.Quote => "blockquote",
        ParagraphKind.Code => "pre",
        _ => "p",
    };

    /// <summary>
    /// Formats a colour as CSS, using <c>rgba()</c> only when it is not opaque.
    /// </summary>
    /// <remarks>
    /// Hex is preferred for the opaque case because <c>#rrggbb</c> is understood by every reader
    /// including the very old ones, whereas eight-digit hex is not.
    /// </remarks>
    private static string Css(Rgba color)
    {
        if (color.A == 255)
        {
            return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        }

        var alpha = (color.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

        return $"rgba({color.R},{color.G},{color.B},{alpha})";
    }

    /// <summary>Quotes a font family name when CSS requires it.</summary>
    /// <remarks>
    /// An unquoted family may only be a sequence of identifiers, so anything with a space, a
    /// comma or a quote in it has to be quoted — and a name that arrived already quoted, or that
    /// is a list of fallbacks, must be left exactly as it is or the list becomes one long name.
    /// </remarks>
    private static string CssFontFamily(string family)
    {
        if (family.Contains(',', StringComparison.Ordinal)
            || family.StartsWith('"')
            || family.StartsWith('\''))
        {
            return family;
        }

        return family.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')
            ? $"\"{family.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : family;
    }

    private static string Attribute(string value)
    {
        var escaped = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            Escape(escaped, c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Escapes one character for both text content and double-quoted attribute values.
    /// </summary>
    /// <remarks>
    /// Deliberately one routine for both. Two would eventually disagree, and the disagreement
    /// would be an injection: this output is parsed again by the importer, and by Word.
    /// </remarks>
    private static void Escape(StringBuilder html, char c)
    {
        switch (c)
        {
            case '&':
                html.Append("&amp;");
                return;

            case '<':
                html.Append("&lt;");
                return;

            case '>':
                html.Append("&gt;");
                return;

            case '"':
                html.Append("&quot;");
                return;

            case '\'':
                html.Append("&#39;");
                return;

            default:
                html.Append(c);
                return;
        }
    }
}
