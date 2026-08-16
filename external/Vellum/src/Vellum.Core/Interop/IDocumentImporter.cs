namespace Vellum;

/// <summary>
/// Reads a document in from an interchange format.
/// </summary>
/// <remarks>
/// <para>
/// The importers themselves are static classes, because importing is a pure function and nothing
/// about it wants an object. This interface exists for the one caller that cannot name them: the
/// editor control, which must not reference the interop packages — a consumer who wants a plain
/// editor should not acquire an HTML parser and an RTF reader to get one. Registering a format
/// with the control is how those packages get in, and this is the shape they get in as.
/// </para>
/// <para>
/// Importers are stateless and must be safe to share, because the clipboard path holds one of
/// each for the lifetime of the control.
/// </para>
/// </remarks>
public interface IDocumentImporter : IDocumentFormat
{
    /// <summary>Reads <paramref name="text"/> in.</summary>
    /// <param name="text">The document in this importer's format.</param>
    /// <returns>What was understood, and what had to be given up to understand it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <remarks>
    /// Must not throw for any input at all, including input that is not this format. A paste is
    /// handed whatever another application put on the clipboard, and an application that dies
    /// on a malformed paste is worse than one that pastes nothing.
    /// </remarks>
    ImportResult Import(string text);
}
