using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Vellum.Avalonia;

/// <summary>
/// One clipboard flavour a rich document travels in: the platform format, and the encoding that
/// format insists on.
/// </summary>
/// <param name="Format">The short name, as on <see cref="IDocumentFormat.Format"/>.</param>
/// <param name="DataFormat">The platform format to read and write it under.</param>
/// <param name="Encoding">The encoding its bytes are in.</param>
/// <remarks>
/// The encoding is carried alongside the format rather than left to the caller because the two are
/// not independent: <c>CF_HTML</c> is UTF-8 by definition and its byte offsets are only correct in
/// it, while RTF is seven-bit by construction and a multi-byte guess turns it into mojibake.
/// </remarks>
public sealed record ClipboardFlavor(string Format, DataFormat<byte[]> DataFormat, Encoding Encoding);

/// <summary>
/// Reading and writing rich documents on the system clipboard, in the formats other applications
/// publish them in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every flavour here is a bytes format, and that is the whole point of this type.</b> Asking a
/// clipboard backend for a native format <em>as a string</em> lets it reinterpret the bytes with
/// whatever encoding it guesses. For RTF that guess produces convincing mojibake which then parses
/// to an empty document — a failure that looks like an unsupported payload rather than a decoding
/// bug, and costs an afternoon to find. So there is deliberately no overload anywhere on this type
/// that takes a clipboard payload as a <see cref="string"/>: bytes in, deliberate decode after.
/// </para>
/// <para>
/// <see cref="ReadAsync(IClipboard, IEnumerable{IDocumentImporter})"/> and <see cref="WriteAsync"/>
/// are the whole API for an ordinary
/// application, and neither exposes a byte. <see cref="Decode"/> and <see cref="Encode"/> exist
/// for applications that hold the payload themselves — a clipboard manager storing what it
/// captured, to parse later or never — and <see cref="Flavors"/> gives them the platform formats
/// to capture under, including for formats they do not intend to parse at all.
/// </para>
/// <para>
/// This assembly cannot see the interop packages, by design: an application that wants a text
/// editor should not acquire an HTML parser to get one. So the importers and exporters are passed
/// in. <c>HtmlFormat.Instance</c> and <c>RtfFormat.Instance</c> are what you pass.
/// </para>
/// </remarks>
public static class RichTextClipboard
{
    /// <summary>The formats, in the order a paste should prefer them.</summary>
    /// <remarks>
    /// HTML first because it carries more than RTF does and because it is what a browser and an
    /// online editor put on the clipboard. RTF second because it is what word processors put
    /// there. Plain text is not here: it is the floor that is fallen back to on its own.
    /// </remarks>
    private static readonly string[] PreferenceOrder = ["html", "rtf"];

    /// <summary>The formats, in the order a paste should prefer them.</summary>
    public static IReadOnlyList<string> Preference { get; } = PreferenceOrder;

    private static readonly ClipboardFlavor HtmlFlavor = new(
        "html", Platform("HTML Format", "public.html", "text/html"), Encoding.UTF8);

    // Latin1 over ASCII because it cannot substitute: RTF is seven-bit by construction, so if
    // anything non-ASCII ever did escape an exporter the bytes stay wrong rather than silently
    // becoming question marks.
    private static readonly ClipboardFlavor RtfFlavor = new(
        "rtf", Platform("Rich Text Format", "public.rtf", "text/rtf"), Encoding.Latin1);

    /// <summary>Every flavour a document can travel in, richest first.</summary>
    public static IReadOnlyList<ClipboardFlavor> Flavors { get; } = [HtmlFlavor, RtfFlavor];

    /// <summary>The flavour a short format name travels in, if it has one.</summary>
    /// <param name="format">A short name, as on <see cref="IDocumentFormat.Format"/>.</param>
    /// <returns>The flavour, or <see langword="null"/> for a format with no clipboard presence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    public static ClipboardFlavor? FlavorFor(string format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format switch
        {
            "html" => HtmlFlavor,
            "rtf" => RtfFlavor,
            _ => null,
        };
    }

    /// <summary>Turns a document into the bytes its format travels in on the clipboard.</summary>
    /// <param name="exporter">The exporter to write with.</param>
    /// <param name="doc">The document to write.</param>
    /// <returns>The payload, or <see langword="null"/> if the format has no clipboard flavour.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The encoding is chosen from the exporter's own format rather than taken as an argument,
    /// which is what makes the wrong one unreachable.
    /// </remarks>
    public static byte[]? Encode(IDocumentExporter exporter, DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(doc);

        return FlavorFor(exporter.Format) is { } flavour
            ? flavour.Encoding.GetBytes(exporter.Export(doc))
            : null;
    }

    /// <summary>Reads a clipboard payload back into a document.</summary>
    /// <param name="importer">The importer to read with.</param>
    /// <param name="payload">The bytes exactly as they came off the clipboard.</param>
    /// <returns>The result, or <see langword="null"/> if the format has no clipboard flavour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="importer"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// This is the method a clipboard manager wants: it stored the bytes when the capture
    /// happened, and parses them later or never.
    /// </para>
    /// <para>
    /// There is no overload taking the payload as a <see cref="string"/>, deliberately. Decoding
    /// the bytes is this method's job precisely because doing it at the call site is the mistake
    /// this type exists to prevent.
    /// </para>
    /// </remarks>
    public static ImportResult? Decode(IDocumentImporter importer, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(importer);

        if (FlavorFor(importer.Format) is not { } flavour)
        {
            return null;
        }

        return importer.Import(flavour.Encoding.GetString(payload));
    }

    /// <summary>
    /// The clipboard payload for a document: plain text, plus a flavour for each exporter that has
    /// one.
    /// </summary>
    /// <param name="doc">The document to write.</param>
    /// <param name="exporters">The formats to write beyond plain text.</param>
    /// <param name="text">
    /// The plain text flavour, or <see langword="null"/> to take it from <paramref name="doc"/>.
    /// </param>
    /// <returns>The payload, ready for <see cref="IClipboard.SetDataAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="doc"/> or <paramref name="exporters"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// An exporter that throws costs its own flavour and nothing else. Exporters are contractually
    /// forbidden from throwing, so reaching that is a bug in one — but losing the whole copy over
    /// it would be a worse answer than losing one of its flavours.
    /// </remarks>
    public static DataTransfer Build(
        DocumentNode doc, IEnumerable<IDocumentExporter> exporters, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(exporters);

        var item = new DataTransferItem();

        item.SetText(text ?? DocumentText.Of(doc));

        foreach (var exporter in exporters)
        {
            if (FlavorFor(exporter.Format) is not { } flavour)
            {
                continue;
            }

            try
            {
                item.Set(flavour.DataFormat, flavour.Encoding.GetBytes(exporter.Export(doc)));
            }
            catch (Exception)
            {
            }
        }

        var data = new DataTransfer();

        data.Add(item);

        return data;
    }

    /// <summary>Puts a document on the clipboard in every format that has a flavour.</summary>
    /// <param name="clipboard">The clipboard to write to.</param>
    /// <param name="doc">The document to write.</param>
    /// <param name="exporters">The formats to write beyond plain text.</param>
    /// <returns><see langword="true"/> if the write completed.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static async Task<bool> WriteAsync(
        IClipboard clipboard, DocumentNode doc, IEnumerable<IDocumentExporter> exporters)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(exporters);

        try
        {
            await clipboard.SetDataAsync(Build(doc, exporters)).ConfigureAwait(true);

            return true;
        }
        catch (Exception)
        {
            // The clipboard belongs to the whole desktop and can fail for reasons that are nobody's
            // business here - another process holding it open is the usual one.
            return false;
        }
    }

    /// <summary>Reads the clipboard's richest readable flavour as a document.</summary>
    /// <param name="clipboard">The clipboard to read.</param>
    /// <param name="importers">The formats to try, in any order.</param>
    /// <returns>
    /// The result, or <see langword="null"/> when the clipboard holds nothing any of them could
    /// read. Plain text counts: it arrives as a document of one paragraph per line.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The importers are tried in <see cref="Preference"/> order rather than the order given,
    /// because the order is the whole behaviour: copying from a word processor puts RTF, HTML and
    /// plain text on the clipboard at once, and taking whichever answers first would silently keep
    /// the poorest of the three.
    /// </remarks>
    public static async Task<ImportResult?> ReadAsync(
        IClipboard clipboard, IEnumerable<IDocumentImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(importers);

        try
        {
            using var data = await clipboard.TryGetDataAsync().ConfigureAwait(true);

            return data is null ? null : await ReadAsync(data, importers).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the richest readable flavour of a transfer as a document.</summary>
    /// <param name="data">The transfer, from a clipboard or a drop.</param>
    /// <param name="importers">The formats to try, in any order.</param>
    /// <returns>
    /// The result, or <see langword="null"/> when the transfer holds nothing any of them could
    /// read. Plain text counts: it arrives as a document of one paragraph per line.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// This overload is the one a drop handler wants, since a drop hands over a transfer and never
    /// touches the clipboard. It is also the only one that can be tested: <see cref="IClipboard"/>
    /// cannot be implemented outside Avalonia, and a headless window does not have one.
    /// </remarks>
    public static async Task<ImportResult?> ReadAsync(
        IAsyncDataTransfer data, IEnumerable<IDocumentImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(importers);

        try
        {
            foreach (var importer in InPreferenceOrder(importers))
            {
                if (FlavorFor(importer.Format) is not { } flavour)
                {
                    continue;
                }

                // Bytes, deliberately, and never as a string. See the type's remarks.
                if (await data.TryGetValueAsync(flavour.DataFormat).ConfigureAwait(true)
                    is not { Length: > 0 } bytes)
                {
                    continue;
                }

                var result = importer.Import(flavour.Encoding.GetString(bytes));

                if (!result.Doc.Blocks.IsEmpty)
                {
                    return result;
                }
            }

            return await data.TryGetTextAsync().ConfigureAwait(true) is { Length: > 0 } text
                ? new ImportResult(DocumentNode.FromParagraphs(text.ReplaceLineEndings("\n").Split('\n')))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the richest readable flavour of a transfer as a document.</summary>
    /// <param name="data">A transfer whose contents are already in hand, as a drop's are.</param>
    /// <param name="importers">The formats to try, in any order.</param>
    /// <returns>
    /// The result, or <see langword="null"/> when the transfer holds nothing any of them could
    /// read. Plain text counts: it arrives as a document of one paragraph per line.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The synchronous half of the pair, for <see cref="DragEventArgs.DataTransfer"/>, which hands
    /// over data that has already crossed the process boundary and so needs no awaiting.
    /// </remarks>
    public static ImportResult? Read(IDataTransfer data, IEnumerable<IDocumentImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(importers);

        try
        {
            foreach (var importer in InPreferenceOrder(importers))
            {
                if (FlavorFor(importer.Format) is not { } flavour)
                {
                    continue;
                }

                // Bytes, deliberately, and never as a string. See the type's remarks.
                if (data.TryGetValue(flavour.DataFormat) is not { Length: > 0 } bytes)
                {
                    continue;
                }

                var result = importer.Import(flavour.Encoding.GetString(bytes));

                if (!result.Doc.Blocks.IsEmpty)
                {
                    return result;
                }
            }

            return data.TryGetText() is { Length: > 0 } text
                ? new ImportResult(DocumentNode.FromParagraphs(text.ReplaceLineEndings("\n").Split('\n')))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The importers, richest first.</summary>
    internal static IEnumerable<IDocumentImporter> InPreferenceOrder(
        IEnumerable<IDocumentImporter> importers) =>
        importers.OrderBy(importer => Array.IndexOf(PreferenceOrder, importer.Format) switch
        {
            < 0 => int.MaxValue,
            var rank => rank,
        });

    private static DataFormat<byte[]> Platform(string windows, string mac, string other) =>
        DataFormat.CreateBytesPlatformFormat(
            OperatingSystem.IsWindows() ? windows :
            OperatingSystem.IsMacOS() ? mac : other);
}
