using System.Collections.Immutable;
using System.Text.Json;

namespace Vellum.Interop.Json;

/// <summary>
/// Lossless JSON serialization of the Vellum document model.
/// </summary>
/// <remarks>
/// <para>
/// The other formats are <em>interchange</em>: HTML and RTF exist to trade documents with programs
/// that are not this one, and both lose something on the way. This one is <em>persistence</em>,
/// and loses nothing — a document written and read back is equal to the one written, which the
/// tests assert directly against <see cref="Node.Equals(Node)"/> rather than by comparing text.
/// </para>
/// <para>
/// <b>That makes it the one format with a compatibility obligation.</b> Nobody keeps the HTML a
/// paste produced, but an application that stores documents keeps these forever, and must still be
/// able to read them after the model gains a node type. So the payload carries a schema version,
/// and a reader refuses a major it does not know rather than silently dropping the parts it cannot
/// understand — a preview that quietly loses a table is worse than one that says it cannot open
/// the file.
/// </para>
/// </remarks>
public sealed class JsonFormat : IDocumentImporter, IDocumentExporter
{
    /// <summary>The schema version this build writes.</summary>
    /// <remarks>
    /// Bumped only for a change that an older reader could not understand. Adding an optional
    /// property is not one of those: a reader that does not know a property ignores it, which is
    /// why every property below is omitted when it holds its default.
    /// </remarks>
    public const int SchemaVersion = 1;

    /// <summary>The shared instance.</summary>
    public static readonly JsonFormat Instance = new();

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <inheritdoc/>
    public string Format => "json";

    /// <inheritdoc/>
    public string MediaType => "application/vnd.vellum+json";

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is <see langword="null"/>.</exception>
    public string Export(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("vellum", SchemaVersion);
            writer.WritePropertyName("blocks");
            WriteBlocks(writer, doc.Blocks);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Never throws, like every importer: a document that cannot be read yields an empty result
    /// carrying a diagnostic saying why. A store full of files written by a future version should
    /// degrade into "cannot open these", not into an unhandled exception on a background thread.
    /// </remarks>
    public ImportResult Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Failed("The payload was empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed("The payload is not a Vellum document.");
            }

            if (!root.TryGetProperty("vellum", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var major))
            {
                return Failed("The payload is not a Vellum document: it has no schema version.");
            }

            if (major > SchemaVersion)
            {
                return Failed(
                    $"This document was written by a newer version of Vellum (schema {major}; " +
                    $"this build reads {SchemaVersion}).");
            }

            if (major < 1)
            {
                return Failed($"Schema version {major} is not a version this build can read.");
            }

            if (!root.TryGetProperty("blocks", out var blocks))
            {
                return new ImportResult(new DocumentNode([]));
            }

            if (blocks.ValueKind != JsonValueKind.Array)
            {
                return Failed("The payload's blocks are not a list.");
            }

            return new ImportResult(new DocumentNode(ReadBlocks(blocks)));
        }
        catch (JsonException error)
        {
            return Failed($"The payload is not valid JSON: {error.Message}");
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or OverflowException or ArgumentException)
        {
            // A payload whose shape is legal JSON but wrong for a document - a number where an
            // object belongs, a span running past the text it marks. Importers never throw, and a
            // hand-edited or corrupted file must not take a host down from a background thread.
            return Failed($"The payload is not a readable Vellum document: {error.Message}");
        }
    }

    private static ImportResult Failed(string reason) =>
        new(new DocumentNode([]), [new ImportDiagnostic(DiagnosticSeverity.Malformed, reason)]);

    // ---- writing ----------------------------------------------------------------------------

    private static void WriteBlocks(Utf8JsonWriter writer, IEnumerable<BlockNode> blocks)
    {
        writer.WriteStartArray();

        foreach (var block in blocks)
        {
            WriteBlock(writer, block);
        }

        writer.WriteEndArray();
    }

    private static void WriteBlock(Utf8JsonWriter writer, BlockNode block)
    {
        writer.WriteStartObject();
        writer.WriteString("type", block.TypeName);

        switch (block)
        {
            case ParagraphNode paragraph:
                WriteEnum(writer, "kind", paragraph.Kind, ParagraphKind.Body);
                WriteEnum(writer, "align", paragraph.Align, TextAlign.Default);

                if (paragraph.IndentLevel != 0)
                {
                    writer.WriteNumber("indent", paragraph.IndentLevel);
                }

                writer.WritePropertyName("content");
                WriteContent(writer, paragraph.Content);

                break;

            case ListNode list:
                WriteEnum(writer, "kind", list.Kind, ListKind.Unordered);

                if (list.Start != 1)
                {
                    writer.WriteNumber("start", list.Start);
                }

                writer.WritePropertyName("items");
                writer.WriteStartArray();

                foreach (var item in list.Items)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("blocks");
                    WriteBlocks(writer, item.Blocks);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                break;

            case TableNode table:
                if (!table.ColumnWidths.IsEmpty)
                {
                    writer.WritePropertyName("columns");
                    writer.WriteStartArray();

                    foreach (var width in table.ColumnWidths)
                    {
                        writer.WriteNumberValue(width);
                    }

                    writer.WriteEndArray();
                }

                writer.WritePropertyName("rows");
                writer.WriteStartArray();

                foreach (var row in table.Rows)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("cells");
                    writer.WriteStartArray();

                    foreach (var cell in row.Cells)
                    {
                        WriteCell(writer, cell);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                break;

            case BlockImageNode image:
                WriteEnum(writer, "align", image.Align, TextAlign.Default);
                writer.WritePropertyName("image");
                WriteImage(writer, image.Image);

                break;

            case RuleNode:
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteCell(Utf8JsonWriter writer, TableCellNode cell)
    {
        writer.WriteStartObject();

        if (cell.RowSpan != 1)
        {
            writer.WriteNumber("rowSpan", cell.RowSpan);
        }

        if (cell.ColumnSpan != 1)
        {
            writer.WriteNumber("columnSpan", cell.ColumnSpan);
        }

        if (cell.IsHeader)
        {
            writer.WriteBoolean("header", true);
        }

        if (cell.Background is { } background)
        {
            writer.WriteString("background", background.ToString());
        }

        writer.WritePropertyName("blocks");
        WriteBlocks(writer, cell.Blocks);
        writer.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter writer, InlineContent content)
    {
        writer.WriteStartObject();

        if (content.Text.Length > 0)
        {
            writer.WriteString("text", content.Text);
        }

        if (!content.Marks.IsEmpty)
        {
            writer.WritePropertyName("marks");
            writer.WriteStartArray();

            foreach (var span in content.Marks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("start", span.Start);
                writer.WriteNumber("length", span.Length);
                writer.WritePropertyName("mark");
                WriteMark(writer, span.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (!content.Embeds.IsEmpty)
        {
            writer.WritePropertyName("embeds");
            writer.WriteStartArray();

            foreach (var embed in content.Embeds)
            {
                switch (embed)
                {
                    case ImageEmbed image:
                        WriteImage(writer, image);

                        break;

                    default:
                        // An embed type this build does not know cannot be written faithfully, and
                        // writing an approximation of it would make the round trip silently lossy.
                        writer.WriteStartObject();
                        writer.WriteString("type", "unknown");
                        writer.WriteEndObject();

                        break;
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteImage(Utf8JsonWriter writer, ImageEmbed image)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteString("source", image.Source);

        if (image.Width is { } width)
        {
            writer.WriteNumber("width", width);
        }

        if (image.Height is { } height)
        {
            writer.WriteNumber("height", height);
        }

        if (image.AltText is { } alt)
        {
            writer.WriteString("alt", alt);
        }

        writer.WriteEndObject();
    }

    private static void WriteMark(Utf8JsonWriter writer, MarkSet mark)
    {
        writer.WriteStartObject();

        if (mark.Style != TextStyle.None)
        {
            writer.WriteString("style", mark.Style.ToString());
        }

        if (mark.FontFamily is { } family)
        {
            writer.WriteString("fontFamily", family);
        }

        if (mark.FontSize is { } size)
        {
            writer.WriteNumber("fontSize", size);
        }

        if (mark.Foreground is { } foreground)
        {
            writer.WriteString("foreground", foreground.ToString());
        }

        if (mark.Highlight is { } highlight)
        {
            writer.WriteString("highlight", highlight.ToString());
        }

        if (mark.Link is { } link)
        {
            writer.WritePropertyName("link");
            writer.WriteStartObject();
            writer.WriteString("href", link.Href);

            if (link.Title is { } title)
            {
                writer.WriteString("title", title);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteEnum<T>(Utf8JsonWriter writer, string name, T value, T fallback)
        where T : struct, Enum
    {
        if (!value.Equals(fallback))
        {
            writer.WriteString(name, value.ToString());
        }
    }

    // ---- reading ----------------------------------------------------------------------------

    private static ImmutableArray<BlockNode> ReadBlocks(JsonElement blocks)
    {
        if (blocks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<BlockNode>();

        foreach (var block in blocks.EnumerateArray())
        {
            if (ReadBlock(block) is { } node)
            {
                result.Add(node);
            }
        }

        return result.ToImmutable();
    }

    private static BlockNode? ReadBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return String(block, "type") switch
        {
            "paragraph" => new ParagraphNode(
                block.TryGetProperty("content", out var content) ? ReadContent(content) : InlineContent.Empty,
                EnumOr<ParagraphKind>(block, "kind"),
                EnumOr<TextAlign>(block, "align"),
                Int(block, "indent") ?? 0),

            "list" => new ListNode(
                block.TryGetProperty("items", out var items) ? ReadItems(items) : [],
                EnumOr<ListKind>(block, "kind"),
                Int(block, "start") ?? 1),

            "table" => ReadTable(block),

            "block-image" => block.TryGetProperty("image", out var image) && ReadImage(image) is { } embed
                ? new BlockImageNode(embed, EnumOr<TextAlign>(block, "align"))
                : null,

            "rule" => RuleNode.Instance,

            // A node type this build has never heard of. The schema version is what stops this
            // being reached for a *newer* document; reaching it anyway means a hand-edited file.
            _ => null,
        };
    }

    private static ImmutableArray<ListItemNode> ReadItems(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<ListItemNode>();

        foreach (var item in items.EnumerateArray())
        {
            result.Add(new ListItemNode(
                item.TryGetProperty("blocks", out var blocks) ? ReadBlocks(blocks) : []));
        }

        return result.ToImmutable();
    }

    private static TableNode? ReadTable(JsonElement block)
    {
        if (!block.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var built = ImmutableArray.CreateBuilder<TableRowNode>();

        foreach (var row in rows.EnumerateArray())
        {
            var cells = ImmutableArray.CreateBuilder<TableCellNode>();

            if (row.TryGetProperty("cells", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in list.EnumerateArray())
                {
                    cells.Add(new TableCellNode(
                        cell.TryGetProperty("blocks", out var blocks) ? ReadBlocks(blocks) : [],
                        Int(cell, "rowSpan") ?? 1,
                        Int(cell, "columnSpan") ?? 1,
                        Colour(cell, "background"),
                        cell.TryGetProperty("header", out var header)
                            && header.ValueKind == JsonValueKind.True));
                }
            }

            built.Add(new TableRowNode(cells.ToImmutable()));
        }

        var widths = ImmutableArray.CreateBuilder<double>();

        if (block.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            foreach (var width in columns.EnumerateArray())
            {
                widths.Add(width.GetDouble());
            }
        }

        return new TableNode(built.ToImmutable(), widths.Count > 0 ? widths.ToImmutable() : null);
    }

    private static InlineContent ReadContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return InlineContent.Empty;
        }

        var text = String(content, "text") ?? string.Empty;

        if (text.Length == 0)
        {
            return InlineContent.Empty;
        }

        var marks = ImmutableArray.CreateBuilder<ValueSpan<MarkSet>>();

        if (content.TryGetProperty("marks", out var spans) && spans.ValueKind == JsonValueKind.Array)
        {
            foreach (var span in spans.EnumerateArray())
            {
                if (Int(span, "start") is { } start
                    && Int(span, "length") is { } length
                    && span.TryGetProperty("mark", out var mark))
                {
                    marks.Add(new ValueSpan<MarkSet>(start, length, ReadMark(mark)));
                }
            }
        }

        var embeds = ImmutableArray.CreateBuilder<InlineEmbed>();

        if (content.TryGetProperty("embeds", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in list.EnumerateArray())
            {
                embeds.Add(ReadImage(embed) ?? new ImageEmbed(string.Empty));
            }
        }

        return InlineContent.Create(text, marks.ToImmutable(), embeds.ToImmutable());
    }

    private static ImageEmbed? ReadImage(JsonElement embed)
    {
        if (embed.ValueKind != JsonValueKind.Object || String(embed, "source") is not { } source)
        {
            return null;
        }

        return new ImageEmbed(
            source,
            Double(embed, "width"),
            Double(embed, "height"),
            String(embed, "alt"));
    }

    private static MarkSet ReadMark(JsonElement mark)
    {
        if (mark.ValueKind != JsonValueKind.Object)
        {
            return MarkSet.Empty;
        }

        LinkMark? link = null;

        if (mark.TryGetProperty("link", out var element)
            && element.ValueKind == JsonValueKind.Object
            && String(element, "href") is { } href)
        {
            link = new LinkMark(href, String(element, "title"));
        }

        return new MarkSet(
            String(mark, "style") is { } style && Enum.TryParse<TextStyle>(style, out var parsed)
                ? parsed
                : TextStyle.None,
            String(mark, "fontFamily"),
            Double(mark, "fontSize"))
        {
            Foreground = Colour(mark, "foreground"),
            Highlight = Colour(mark, "highlight"),
            Link = link,
        };
    }

    private static Rgba? Colour(JsonElement element, string name) =>
        String(element, name) is { } text
            && text.StartsWith('#')
            && uint.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out var argb)
            ? Rgba.FromArgb(argb)
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            ? number
            : null;

    private static T EnumOr<T>(JsonElement element, string name)
        where T : struct, Enum =>
        String(element, name) is { } text && System.Enum.TryParse<T>(text, out var value)
            ? value
            : default;
}
