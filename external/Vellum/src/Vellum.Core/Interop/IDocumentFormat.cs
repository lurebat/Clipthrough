namespace Vellum;

/// <summary>
/// An interchange format the editor can move a document through — what
/// <see cref="IDocumentImporter"/> and <see cref="IDocumentExporter"/> have in common.
/// </summary>
/// <remarks>
/// This exists so that a format can be handed to the editor as one thing. Reading and writing are
/// separate contracts because a format can genuinely support one and not the other, but a caller
/// registering HTML support is not registering two unrelated objects, and should not have to say
/// so twice.
/// </remarks>
public interface IDocumentFormat
{
    /// <summary>A short stable name for the format, such as <c>html</c> or <c>rtf</c>.</summary>
    string Format { get; }

    /// <summary>
    /// The IANA media type, which is also what most clipboard backends want as a format name.
    /// </summary>
    string MediaType { get; }
}
