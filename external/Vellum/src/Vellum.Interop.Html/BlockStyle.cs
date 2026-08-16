namespace Vellum.Interop.Html;

/// <summary>
/// The paragraph formatting in force at a point in the element tree.
/// </summary>
/// <param name="Kind">The paragraph kind enclosing elements have asked for.</param>
/// <param name="Align">The alignment.</param>
/// <param name="Indent">The indent level.</param>
/// <param name="Preformatted">
/// Whether whitespace is content. Inside <c>&lt;pre&gt;</c> every space and newline was written on
/// purpose; everywhere else runs of whitespace are just how the source happens to be laid out.
/// </param>
internal readonly record struct BlockStyle(
    ParagraphKind Kind,
    TextAlign Align,
    int Indent,
    bool Preformatted)
{
    /// <summary>An ordinary paragraph.</summary>
    internal static BlockStyle Default => new(ParagraphKind.Body, TextAlign.Default, 0, false);
}
