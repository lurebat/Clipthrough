namespace Vellum;

/// <summary>
/// Writes a document out in an interchange format.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the importers, and deliberately a much smaller contract than they have. An
/// importer returns an <see cref="ImportResult"/> because it is handed a format far larger than
/// this document model and has to say what it threw away. An exporter starts from the model, so
/// there is nothing it can be surprised by and nothing it has to report: every construct the
/// model can hold, the exporter is required to write. That is why there is no diagnostics
/// channel here — one would only ever carry an exporter bug.
/// </para>
/// <para>
/// Exporters are stateless and must be safe to share, because the clipboard path holds one of
/// each for the lifetime of the control.
/// </para>
/// </remarks>
public interface IDocumentExporter : IDocumentFormat
{
    /// <summary>Writes <paramref name="doc"/> out.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The document in this exporter's format.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    /// <remarks>
    /// Must not throw for any document the schema allows, including an empty one. A document is
    /// exported at the moment a user presses copy, and a copy that throws loses the selection
    /// they were trying to keep.
    /// </remarks>
    string Export(DocumentNode doc);
}
