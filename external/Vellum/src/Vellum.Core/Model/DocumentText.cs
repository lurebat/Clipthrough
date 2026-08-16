using System.Text;

namespace Vellum;

/// <summary>
/// The plain text of a document or of part of one.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, because there are three callers who must agree: the clipboard's plain-text
/// flavour, the accessibility peers that read a document out to a screen reader, and anything
/// searching it. Two of them disagreeing is a bug nobody notices until a blind user and a sighted
/// user describe the same document differently.
/// </para>
/// <para>
/// An embed contributes its <see cref="InlineEmbed.PlainTextFallback"/>, not the
/// <see cref="InlineContent.Placeholder"/> that stands in for it in the text. Reading out
/// "object replacement character" instead of an image's alt text is not an approximation of the
/// document, it is noise.
/// </para>
/// <para>
/// Blocks are separated by a newline, and nothing else: no bullet glyphs, no cell delimiters. A
/// marker is drawn by the view and is not in the model, and inventing separators here would put
/// them on the clipboard too.
/// </para>
/// </remarks>
public static class DocumentText
{
    /// <summary>The plain text of a whole document.</summary>
    /// <param name="doc">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    public static string Of(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        return Of(doc.Blocks);
    }

    /// <summary>The plain text of a run of nodes, at any depth.</summary>
    /// <param name="nodes">The nodes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is null.</exception>
    public static string Of(IEnumerable<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var builder = new StringBuilder();
        var any = false;

        Append(builder, nodes, ref any);

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, IEnumerable<Node> nodes, ref bool any)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ParagraphNode paragraph:
                    if (any)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(paragraph.Content.ToPlainText());
                    any = true;
                    break;

                default:
                    Append(builder, node.Children, ref any);
                    break;
            }
        }
    }
}
