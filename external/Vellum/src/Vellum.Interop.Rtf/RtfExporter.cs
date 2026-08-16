using System.Globalization;
using System.Text;

namespace Vellum.Interop.Rtf;

/// <summary>
/// Writes a document as Rich Text Format, the shape formatted text has to be in to reach a
/// Windows application through the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than by inverting <see cref="RtfImporter"/>, because that import runs
/// through RtfPipe and RtfPipe only reads. Going out through HTML and asking something to convert
/// it back would put a second, unrelated translator between the user's document and the
/// clipboard.
/// </para>
/// <para>
/// The dialect is deliberately conservative: the control words here are the ones that have been
/// understood since Word 97, because the receiving application is not known and the ones that
/// matter — Word, Outlook, WordPad, LibreOffice — disagree about everything newer. Where a
/// construct has a modern spelling and a legacy spelling, both are written when they can coexist,
/// which is why lists carry a <c>\pntext</c> group as well as their numbering definition.
/// </para>
/// <para>
/// RTF has no heading. A heading is written as its visual consequence — bold, larger, and
/// carrying <c>\outlinelevel</c> so that an application which does understand outlines can
/// recover it. Reading such a paragraph back gives body text that looks like a heading rather
/// than a heading, and the round-trip tests say so rather than pretending otherwise.
/// </para>
/// </remarks>
public sealed class RtfExporter : IDocumentExporter
{
    /// <summary>Twips per pixel, at the 96dpi the rest of the model measures in.</summary>
    /// <remarks>A twip is a twentieth of a point, so an inch is 1440 of them and 96 pixels.</remarks>
    private const double TwipsPerPixel = 1440.0 / 96.0;

    /// <summary>The width of one indent level in twips, which is Word's half inch.</summary>
    private const int IndentTwips = 720;

    /// <summary>How far a list item's marker hangs to the left of its text.</summary>
    private const int HangingIndentTwips = 360;

    /// <summary>The default text size in half-points, matching the model's default of 16px.</summary>
    private const int DefaultHalfPoints = 24;

    /// <summary>A shared instance. The exporter holds no state.</summary>
    public static RtfExporter Instance { get; } = new();

    /// <inheritdoc/>
    public string Format => "rtf";

    /// <inheritdoc/>
    public string MediaType => "text/rtf";

    /// <inheritdoc/>
    public string Export(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        // The tables have to be written before the body but are only known after walking it, so
        // the body is built first and the header is prepended.
        var fonts = new FontTable();
        var colours = new ColourTable();
        var body = new StringBuilder();

        var writer = new Writer(body, fonts, colours);

        writer.WriteBlocks(doc.Blocks);

        var rtf = new StringBuilder();

        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        fonts.Write(rtf);
        colours.Write(rtf);
        rtf.Append(body);
        rtf.Append('}');

        return rtf.ToString();
    }

    /// <summary>The fonts used by a document, in the order they were first asked for.</summary>
    /// <remarks>
    /// Index 0 is always present and is the document default, because <c>\deff0</c> in the header
    /// names it and a reader that finds no such font is entitled to do anything it likes.
    /// </remarks>
    private sealed class FontTable
    {
        private readonly List<string> _fonts = ["Segoe UI"];

        internal int IndexOf(string? family)
        {
            if (string.IsNullOrWhiteSpace(family))
            {
                return 0;
            }

            var index = _fonts.IndexOf(family);

            if (index >= 0)
            {
                return index;
            }

            _fonts.Add(family);

            return _fonts.Count - 1;
        }

        internal void Write(StringBuilder rtf)
        {
            rtf.Append(@"{\fonttbl");

            for (var i = 0; i < _fonts.Count; i++)
            {
                rtf.Append(@"{\f")
                    .Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append(@"\fnil ");

                // A font name is text like any other: it can contain a brace, a backslash or a
                // character outside the code page, and Word's own output regularly does.
                Text.Write(rtf, _fonts[i]);

                rtf.Append(";}");
            }

            rtf.Append('}');
        }
    }

    /// <summary>The colours used by a document, in the order they were first asked for.</summary>
    /// <remarks>
    /// Index 0 is the "auto" colour and has no definition of its own, which is what the empty
    /// first entry in the table means. Writing <c>\cf0</c> is how a run says it wants whatever
    /// the reader considers normal, rather than naming black and being wrong on a dark theme.
    /// </remarks>
    private sealed class ColourTable
    {
        private readonly List<Rgba> _colours = [];

        internal int IndexOf(Rgba? colour)
        {
            if (colour is not { } value)
            {
                return 0;
            }

            var index = _colours.IndexOf(value);

            if (index < 0)
            {
                _colours.Add(value);
                index = _colours.Count - 1;
            }

            // Offset by the auto entry, which occupies index 0 without appearing in the list.
            return index + 1;
        }

        internal void Write(StringBuilder rtf)
        {
            rtf.Append(@"{\colortbl;");

            foreach (var colour in _colours)
            {
                rtf.Append(@"\red").Append(colour.R.ToString(CultureInfo.InvariantCulture))
                    .Append(@"\green").Append(colour.G.ToString(CultureInfo.InvariantCulture))
                    .Append(@"\blue").Append(colour.B.ToString(CultureInfo.InvariantCulture))
                    .Append(';');
            }

            rtf.Append('}');
        }
    }

    /// <summary>Escaping, which is the same everywhere text appears.</summary>
    private static class Text
    {
        internal static void Write(StringBuilder rtf, string text)
        {
            foreach (var c in text)
            {
                Write(rtf, c);
            }
        }

        internal static void Write(StringBuilder rtf, char c)
        {
            switch (c)
            {
                case '\\':
                    rtf.Append(@"\\");
                    return;

                case '{':
                    rtf.Append(@"\{");
                    return;

                case '}':
                    rtf.Append(@"\}");
                    return;

                case '\n':
                    rtf.Append(@"\line ");
                    return;

                case '\t':
                    rtf.Append(@"\tab ");
                    return;

                case '\r':
                    // A stray carriage return is not a second line break; the newline beside it
                    // already was one.
                    return;
            }

            if (c is >= ' ' and <= '~')
            {
                rtf.Append(c);
                return;
            }

            // \u takes a signed 16-bit integer, so anything above U+7FFF is written negative --
            // and a surrogate pair is written as its two halves, which is what a reader that
            // understands \u at all expects. The trailing "?" is the replacement for readers too
            // old to understand it, and \uc1 in the header is what says there is exactly one.
            rtf.Append(@"\u")
                .Append(((short)c).ToString(CultureInfo.InvariantCulture))
                .Append('?');
        }
    }

    /// <summary>Walks the document, holding the state RTF needs to be told about explicitly.</summary>
    private sealed class Writer(StringBuilder rtf, FontTable fonts, ColourTable colours)
    {
        internal void WriteBlocks(IEnumerable<BlockNode> blocks)
        {
            foreach (var block in blocks)
            {
                WriteBlock(block);
            }
        }

        private void WriteBlock(BlockNode block)
        {
            switch (block)
            {
                case ParagraphNode paragraph:
                    WriteParagraph(paragraph);
                    return;

                case ListNode list:
                    WriteList(list, depth: 0);
                    return;

                case TableNode table:
                    WriteTable(table);
                    return;

                case RuleNode:
                    // There is no horizontal rule in RTF, only a paragraph with a rule drawn under
                    // it. \pard afterwards is what keeps the border off the paragraph that follows.
                    rtf.Append(@"\pard\brdrb\brdrs\brdrw10\brsp20\par\pard");
                    return;

                case BlockImageNode image:
                    rtf.Append(@"\pard");
                    WriteAlignment(image.Align);
                    WriteImage(image.Image);
                    rtf.Append(@"\par");
                    return;

                default:
                    // Reachable only by growing the schema without extending this exporter, which
                    // is exactly when silence would lose a user's content without a trace.
                    throw new NotSupportedException(
                        $"The RTF exporter does not know how to write a '{block.TypeName}' block.");
            }
        }

        private void WriteParagraph(ParagraphNode paragraph)
        {
            rtf.Append(@"\pard");
            WriteParagraphProperties(paragraph);
            WriteContent(paragraph.Content, HeadingScale(paragraph.Kind), Emphasised(paragraph.Kind));
            rtf.Append(@"\par");
        }

        private void WriteParagraphProperties(ParagraphNode paragraph)
        {
            WriteAlignment(paragraph.Align);

            var indent = paragraph.IndentLevel;

            // A quote is an indent as far as RTF is concerned. Saying so keeps a quoted paragraph
            // looking quoted in an application that has no notion of one.
            if (paragraph.Kind == ParagraphKind.Quote)
            {
                indent++;
            }

            if (indent > 0)
            {
                rtf.Append(@"\li").Append((indent * IndentTwips).ToString(CultureInfo.InvariantCulture));
            }

            if (OutlineLevel(paragraph.Kind) is { } level)
            {
                rtf.Append(@"\outlinelevel").Append(level.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void WriteAlignment(TextAlign align)
        {
            rtf.Append(align switch
            {
                TextAlign.Center => @"\qc",
                TextAlign.Right => @"\qr",
                TextAlign.Justify => @"\qj",
                TextAlign.Left => @"\ql",
                _ => string.Empty,
            });
        }

        /// <summary>Writes inline content as a sequence of runs, each in its own group.</summary>
        /// <param name="content">The content.</param>
        /// <param name="scale">A multiplier on the text size, for headings.</param>
        /// <param name="alwaysBold">Whether the whole run is bold regardless of its marks.</param>
        /// <remarks>
        /// A group is used per run rather than turning each property off again afterwards.
        /// Switching off by hand means every new property needs a matching reset in every other
        /// branch, and the one that gets forgotten leaks formatting across the rest of the
        /// document. A group cannot leak.
        /// </remarks>
        private void WriteContent(InlineContent content, double scale, bool alwaysBold)
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

                var link = mark.Link;

                if (link is not null)
                {
                    rtf.Append(@"{\field{\*\fldinst{HYPERLINK """);
                    Text.Write(rtf, link.Href);
                    rtf.Append(@"""}}{\fldrslt");
                }

                rtf.Append('{');
                WriteRunProperties(mark, scale, alwaysBold, underlined: link is not null);

                for (var i = start; i < end; i++)
                {
                    var c = content.Text[i];

                    if (c == InlineContent.Placeholder)
                    {
                        if (embed < content.Embeds.Length && content.Embeds[embed] is ImageEmbed image)
                        {
                            WriteImage(image);
                        }

                        embed++;
                        continue;
                    }

                    Text.Write(rtf, c);
                }

                rtf.Append('}');

                if (link is not null)
                {
                    rtf.Append("}}");
                }

                start = end;
            }
        }

        private void WriteRunProperties(MarkSet mark, double scale, bool alwaysBold, bool underlined)
        {
            var start = rtf.Length;

            if (mark.FontFamily is { } family)
            {
                rtf.Append(@"\f").Append(fonts.IndexOf(family).ToString(CultureInfo.InvariantCulture));
            }

            var halfPoints = (int)Math.Round(
                (mark.FontSize is { } size ? size * 1.5 : DefaultHalfPoints) * scale,
                MidpointRounding.AwayFromZero);

            if (halfPoints != DefaultHalfPoints)
            {
                rtf.Append(@"\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture));
            }

            if (mark.Foreground is not null)
            {
                rtf.Append(@"\cf").Append(colours.IndexOf(mark.Foreground).ToString(CultureInfo.InvariantCulture));
            }

            if (mark.Highlight is not null)
            {
                rtf.Append(@"\highlight").Append(colours.IndexOf(mark.Highlight).ToString(CultureInfo.InvariantCulture));
            }

            if (alwaysBold || mark.Has(TextStyle.Bold))
            {
                rtf.Append(@"\b");
            }

            if (mark.Has(TextStyle.Italic))
            {
                rtf.Append(@"\i");
            }

            // A link is underlined because that is what a link looks like, not because the model
            // said so -- and a reader that drops the field keeps the appearance.
            if (underlined || mark.Has(TextStyle.Underline))
            {
                rtf.Append(@"\ul");
            }

            if (mark.Has(TextStyle.Strikethrough))
            {
                rtf.Append(@"\strike");
            }

            if (mark.Has(TextStyle.Super))
            {
                rtf.Append(@"\super");
            }

            if (mark.Has(TextStyle.Sub))
            {
                rtf.Append(@"\sub");
            }

            // Monospace is the whole of what inline code means visually, and RTF has no way to
            // say "code" other than to name a font that looks like it.
            if (mark.Has(TextStyle.Code) && mark.FontFamily is null)
            {
                rtf.Append(@"\f").Append(fonts.IndexOf("Consolas").ToString(CultureInfo.InvariantCulture));
            }

            // Separates the last control word from the text, and is consumed as part of it. Only
            // when there was a control word: a group with no properties needs no delimiter, and a
            // space written anyway is not a delimiter but a space in the user's text.
            if (rtf.Length > start)
            {
                rtf.Append(' ');
            }
        }

        private void WriteList(ListNode list, int depth)
        {
            var number = list.Start;

            foreach (var item in list.Items)
            {
                var marker = list.Kind == ListKind.Ordered
                    ? $"{number.ToString(CultureInfo.InvariantCulture)}."
                    : "\u2022";

                WriteListItem(item, marker, depth, list.Kind, list.Start);
                number++;
            }
        }

        private void WriteListItem(ListItemNode item, string marker, int depth, ListKind kind, int start)
        {
            var first = true;

            foreach (var block in item.Blocks)
            {
                // A nested list is a list, not a paragraph of this one: it carries its own
                // markers and its own depth.
                if (block is ListNode nested)
                {
                    WriteList(nested, depth + 1);
                    continue;
                }

                if (block is not ParagraphNode paragraph)
                {
                    WriteBlock(block);
                    continue;
                }

                var indent = ((depth + 1) * IndentTwips) + (paragraph.IndentLevel * IndentTwips);

                rtf.Append(@"\pard");
                WriteAlignment(paragraph.Align);
                rtf.Append(@"\li").Append(indent.ToString(CultureInfo.InvariantCulture));

                if (first)
                {
                    // The marker hangs to the left of the text, which is what a list looks like,
                    // and is written both as a numbering definition and as literal text. Readers
                    // that understand \pn use the definition; the rest still see the bullet.
                    rtf.Append(@"\fi-").Append(HangingIndentTwips.ToString(CultureInfo.InvariantCulture));

                    if (kind == ListKind.Ordered)
                    {
                        rtf.Append(@"{\*\pn\pnlvlbody\pndec\pnindent0\pnstart")
                            .Append(start.ToString(CultureInfo.InvariantCulture))
                            .Append(@"{\pntxta .}}");
                    }
                    else
                    {
                        // \'B7 is the bullet in the code page named by the header, which is what
                        // a numbering definition has to use: \u is not allowed inside one.
                        rtf.Append(@"{\*\pn\pnlvlblt\pnf0\pnindent0{\pntxtb\'B7}}");
                    }

                    rtf.Append(@"{\pntext ");
                    Text.Write(rtf, marker);
                    rtf.Append(@"\tab}");
                }

                WriteContent(paragraph.Content, HeadingScale(paragraph.Kind), Emphasised(paragraph.Kind));
                rtf.Append(@"\par");
                first = false;
            }
        }

        /// <summary>Writes a table as rows of cells, each row declaring its own geometry.</summary>
        /// <remarks>
        /// RTF has no table element. A row is a run of paragraphs that happens to be preceded by
        /// a row definition and punctuated by <c>\cell</c>, and the definition has to be repeated
        /// in front of every row because a reader keeps no memory of the last one.
        /// </remarks>
        private void WriteTable(TableNode table)
        {
            const int DefaultCellTwips = 1440;

            foreach (var row in table.Rows)
            {
                rtf.Append(@"\trowd\trgaph108\trleft0");

                var edge = 0;

                foreach (var cell in row.Cells)
                {
                    var width = cell.ColumnSpan * DefaultCellTwips;

                    edge += width;

                    if (cell.Background is { } background)
                    {
                        rtf.Append(@"\clcbpat")
                            .Append(colours.IndexOf(background).ToString(CultureInfo.InvariantCulture));
                    }

                    rtf.Append(@"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs");
                    rtf.Append(@"\cellx").Append(edge.ToString(CultureInfo.InvariantCulture));
                }

                foreach (var cell in row.Cells)
                {
                    rtf.Append(@"\pard\intbl");

                    foreach (var block in cell.Blocks)
                    {
                        if (block is ParagraphNode paragraph)
                        {
                            WriteContent(
                                paragraph.Content,
                                HeadingScale(paragraph.Kind),
                                Emphasised(paragraph.Kind) || cell.IsHeader);
                        }
                        else
                        {
                            WriteBlock(block);
                        }
                    }

                    rtf.Append(@"\cell");
                }

                rtf.Append(@"\row");
            }

            rtf.Append(@"\pard");
        }

        /// <summary>Writes an image, when its bytes are something RTF can be given.</summary>
        /// <remarks>
        /// Only a data URI can be embedded. RTF carries picture data inline and has no way to
        /// reference one, so an image held as an <c>http</c> URL would have to be fetched — and an
        /// exporter that reaches out to the network to serialise a document is an exporter that
        /// hangs, leaks the fact that the document was copied, and cannot be used offline. Its
        /// alternative text is written instead, so the reader is told what is missing.
        /// </remarks>
        private void WriteImage(ImageEmbed image)
        {
            if (!DataUri.TryDecode(image.Source, out var bytes, out var kind))
            {
                if (image.AltText is { Length: > 0 } alt)
                {
                    Text.Write(rtf, alt);
                }

                return;
            }

            rtf.Append(@"{\pict").Append(kind);

            if (image.Width is { } width)
            {
                rtf.Append(@"\picwgoal")
                    .Append(((int)Math.Round(width * TwipsPerPixel)).ToString(CultureInfo.InvariantCulture));
            }

            if (image.Height is { } height)
            {
                rtf.Append(@"\pichgoal")
                    .Append(((int)Math.Round(height * TwipsPerPixel)).ToString(CultureInfo.InvariantCulture));
            }

            rtf.Append(' ');

            foreach (var b in bytes)
            {
                rtf.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            rtf.Append('}');
        }
    }

    /// <summary>How much larger than body text a heading is drawn.</summary>
    /// <remarks>
    /// These are the ratios the editor itself uses. RTF cannot say "heading", so the size is the
    /// only part of the meaning that can be carried, and it has to agree with what the user was
    /// looking at when they pressed copy.
    /// </remarks>
    private static double HeadingScale(ParagraphKind kind) => kind switch
    {
        ParagraphKind.Heading1 => 2.0,
        ParagraphKind.Heading2 => 1.5,
        ParagraphKind.Heading3 => 1.25,
        ParagraphKind.Heading4 => 1.0,
        ParagraphKind.Heading5 => 0.875,
        ParagraphKind.Heading6 => 0.85,
        _ => 1.0,
    };

    private static bool Emphasised(ParagraphKind kind) => kind
        is ParagraphKind.Heading1
        or ParagraphKind.Heading2
        or ParagraphKind.Heading3
        or ParagraphKind.Heading4
        or ParagraphKind.Heading5
        or ParagraphKind.Heading6;

    private static int? OutlineLevel(ParagraphKind kind) => kind switch
    {
        ParagraphKind.Heading1 => 0,
        ParagraphKind.Heading2 => 1,
        ParagraphKind.Heading3 => 2,
        ParagraphKind.Heading4 => 3,
        ParagraphKind.Heading5 => 4,
        ParagraphKind.Heading6 => 5,
        _ => null,
    };

    /// <summary>Reads the bytes out of a <c>data:</c> URI.</summary>
    private static class DataUri
    {
        internal static bool TryDecode(string source, out byte[] bytes, out string kind)
        {
            bytes = [];
            kind = string.Empty;

            if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var comma = source.IndexOf(',', StringComparison.Ordinal);

            if (comma < 0)
            {
                return false;
            }

            var header = source[5..comma];

            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Only the two formats every RTF reader has a control word for. A GIF or an SVG would
            // have to be re-encoded, and an exporter is the wrong place to be decoding images.
            kind = header switch
            {
                var h when h.StartsWith("image/png", StringComparison.OrdinalIgnoreCase) => @"\pngblip",
                var h when h.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) => @"\jpegblip",
                var h when h.StartsWith("image/jpg", StringComparison.OrdinalIgnoreCase) => @"\jpegblip",
                _ => string.Empty,
            };

            if (kind.Length == 0)
            {
                return false;
            }

            return TryReadBase64(source[(comma + 1)..], out bytes);
        }

        private static bool TryReadBase64(string text, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(text);
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }
    }
}
