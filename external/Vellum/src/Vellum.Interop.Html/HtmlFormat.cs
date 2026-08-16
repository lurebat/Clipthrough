namespace Vellum.Interop.Html;

/// <summary>
/// HTML as a clipboard flavour: the importer and the exporter behind one registrable object.
/// </summary>
/// <remarks>
/// <para>
/// Handing this to <c>RichTextView.Formats</c> is what makes copy write HTML and paste read it.
/// The control cannot reach <see cref="HtmlImporter"/> or <see cref="HtmlExporter"/> itself
/// without dragging an HTML parser into every application that only wanted an editor.
/// </para>
/// <para>
/// This is the clipboard's HTML, so it is <c>CF_HTML</c>: the payload carries the preamble and
/// byte offsets Windows requires and every other application on Windows expects. Reading tolerates
/// its absence, because plenty of applications write bare HTML and a paste that refused them would
/// be worse than useless.
/// </para>
/// </remarks>
public sealed class HtmlFormat : IDocumentImporter, IDocumentExporter
{
    /// <summary>The one instance. Importing and exporting are pure, so more would be waste.</summary>
    public static readonly HtmlFormat Instance = new();

    private HtmlFormat()
    {
    }

    /// <inheritdoc/>
    public string Format => "html";

    /// <inheritdoc/>
    public string MediaType => "text/html";

    /// <inheritdoc/>
    public ImportResult Import(string text) => HtmlImporter.ImportClipboard(text);

    /// <inheritdoc/>
    public string Export(DocumentNode doc) => ClipboardHtml.Wrap(HtmlExporter.Instance.Export(doc));
}
