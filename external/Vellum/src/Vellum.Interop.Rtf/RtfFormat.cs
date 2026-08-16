namespace Vellum.Interop.Rtf;

/// <summary>
/// RTF as a clipboard flavour: the importer and the exporter behind one registrable object.
/// </summary>
/// <remarks>
/// Handing this to <c>RichTextView.Formats</c> is what makes copy write RTF and paste read it.
/// The control cannot reach <see cref="RtfImporter"/> or <see cref="RtfExporter"/> itself without
/// dragging an RTF reader into every application that only wanted an editor.
/// </remarks>
public sealed class RtfFormat : IDocumentImporter, IDocumentExporter
{
    /// <summary>The one instance. Importing and exporting are pure, so more would be waste.</summary>
    public static readonly RtfFormat Instance = new();

    private RtfFormat()
    {
    }

    /// <inheritdoc/>
    public string Format => "rtf";

    /// <inheritdoc/>
    public string MediaType => "text/rtf";

    /// <inheritdoc/>
    public ImportResult Import(string text) => RtfImporter.Import(text);

    /// <inheritdoc/>
    public string Export(DocumentNode doc) => RtfExporter.Instance.Export(doc);
}
