using System.Collections.Immutable;

namespace Vellum;

/// <summary>What kind of paragraph a <see cref="ParagraphNode"/> is.</summary>
public enum ParagraphKind
{
    /// <summary>Ordinary body text.</summary>
    Body = 0,

    /// <summary>Heading level 1.</summary>
    Heading1,

    /// <summary>Heading level 2.</summary>
    Heading2,

    /// <summary>Heading level 3.</summary>
    Heading3,

    /// <summary>Heading level 4.</summary>
    Heading4,

    /// <summary>Heading level 5.</summary>
    Heading5,

    /// <summary>Heading level 6.</summary>
    Heading6,

    /// <summary>Block quotation.</summary>
    Quote,

    /// <summary>Preformatted code.</summary>
    Code,
}

/// <summary>Horizontal alignment of a paragraph.</summary>
public enum TextAlign
{
    /// <summary>Inherit — the reading direction's natural start edge.</summary>
    Default = 0,

    /// <summary>Aligned to the left edge.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Aligned to the right edge.</summary>
    Right,

    /// <summary>Stretched to both edges.</summary>
    Justify,
}

/// <summary>
/// A paragraph: the only node that holds text.
/// </summary>
/// <remarks>
/// A paragraph is not a leaf. Its interior is the inline content, so a paragraph of n code
/// units occupies n + 2 positions in its parent, the extra two being the boundaries a caret
/// can sit just inside.
/// </remarks>
public sealed class ParagraphNode : BlockNode
{
    /// <summary>An empty body paragraph.</summary>
    public static ParagraphNode Empty { get; } = new(InlineContent.Empty);

    /// <summary>Creates a paragraph.</summary>
    /// <param name="content">The inline content.</param>
    /// <param name="kind">What kind of paragraph it is.</param>
    /// <param name="align">Horizontal alignment.</param>
    /// <param name="indentLevel">Indent depth, in steps. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="indentLevel"/> is negative.</exception>
    public ParagraphNode(
        InlineContent content,
        ParagraphKind kind = ParagraphKind.Body,
        TextAlign align = TextAlign.Default,
        int indentLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(indentLevel);
        NodeAttr.Require(kind);
        NodeAttr.Require(align);

        Content = content;
        Kind = kind;
        Align = align;
        IndentLevel = indentLevel;
    }

    /// <summary>Creates a plain body paragraph from a string.</summary>
    /// <param name="text">The text.</param>
    public static ParagraphNode FromText(string text) => new(InlineContent.FromText(text));

    /// <summary>The inline content.</summary>
    public InlineContent Content { get; }

    /// <summary>What kind of paragraph this is.</summary>
    public ParagraphKind Kind { get; }

    /// <summary>Horizontal alignment.</summary>
    public TextAlign Align { get; }

    /// <summary>Indent depth, in steps.</summary>
    public int IndentLevel { get; }

    /// <inheritdoc/>
    public override int ContentSize => Content.Length;

    /// <inheritdoc/>
    public override bool IsLeaf => false;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => ImmutableArray<Node>.Empty;

    /// <inheritdoc/>
    public override string TypeName => "paragraph";

    /// <summary>Returns this paragraph with different inline content.</summary>
    /// <remarks>
    /// Content that is already what it is being set to gives back this very paragraph, so that
    /// a rewrite which changed nothing can be recognised by reference rather than by comparing
    /// whole documents.
    /// </remarks>
    /// <param name="content">The replacement content.</param>
    public ParagraphNode WithContent(InlineContent content) =>
        ReferenceEquals(content, Content) ? this : new(content, Kind, Align, IndentLevel);

    /// <summary>Returns this paragraph as a different kind.</summary>
    /// <param name="kind">The replacement kind.</param>
    public ParagraphNode WithKind(ParagraphKind kind) =>
        new(Content, kind, Align, IndentLevel);

    /// <summary>Returns this paragraph with different alignment.</summary>
    /// <param name="align">The replacement alignment.</param>
    public ParagraphNode WithAlign(TextAlign align) =>
        new(Content, Kind, align, IndentLevel);

    /// <summary>Returns this paragraph at a different indent depth.</summary>
    /// <param name="indentLevel">The replacement indent depth.</param>
    public ParagraphNode WithIndentLevel(int indentLevel) =>
        new(Content, Kind, Align, indentLevel);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always — a paragraph holds text, not nodes.</exception>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        throw new InvalidOperationException("A paragraph holds inline content, not child nodes.");

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other)
    {
        var p = (ParagraphNode)other;

        return Content.Equals(p.Content)
            && Kind == p.Kind
            && Align == p.Align
            && IndentLevel == p.IndentLevel;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Content, Kind, Align, IndentLevel);

    /// <inheritdoc/>
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}({Content})";
}
