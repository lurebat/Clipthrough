using System.Collections.Immutable;

namespace Vellum;

/// <summary>A horizontal rule. A leaf, so it occupies exactly one position.</summary>
public sealed class RuleNode : BlockNode
{
    /// <summary>The single instance — a rule carries no state.</summary>
    public static RuleNode Instance { get; } = new();

    private RuleNode()
    {
    }

    /// <inheritdoc/>
    public override int ContentSize => 0;

    /// <inheritdoc/>
    public override bool IsLeaf => true;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => ImmutableArray<Node>.Empty;

    /// <inheritdoc/>
    public override string TypeName => "rule";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always — a rule is a leaf.</exception>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        throw new InvalidOperationException("A rule is a leaf and holds no children.");

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other) => true;

    /// <inheritdoc/>
    public override int GetHashCode() => typeof(RuleNode).GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => "rule";
}

/// <summary>
/// An image occupying a whole block. A leaf, so it occupies exactly one position.
/// </summary>
public sealed class BlockImageNode : BlockNode
{
    /// <summary>Creates a block image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="align">Horizontal alignment within the block.</param>
    public BlockImageNode(ImageEmbed image, TextAlign align = TextAlign.Default)
    {
        ArgumentNullException.ThrowIfNull(image);
        NodeAttr.Require(align);

        Image = image;
        Align = align;
    }

    /// <summary>The image.</summary>
    public ImageEmbed Image { get; }

    /// <summary>Horizontal alignment within the block.</summary>
    public TextAlign Align { get; }

    /// <inheritdoc/>
    public override int ContentSize => 0;

    /// <inheritdoc/>
    public override bool IsLeaf => true;

    /// <inheritdoc/>
    public override IReadOnlyList<Node> Children => ImmutableArray<Node>.Empty;

    /// <inheritdoc/>
    public override string TypeName => "block-image";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always — a block image is a leaf.</exception>
    public override Node WithChildren(IReadOnlyList<Node> children) =>
        throw new InvalidOperationException("A block image is a leaf and holds no children.");

    /// <inheritdoc/>
    protected override bool EqualsCore(Node other)
    {
        var image = (BlockImageNode)other;

        return Image.Equals(image.Image) && Align == image.Align;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Image, Align);

    /// <inheritdoc/>
    public override string ToString() => $"block-image({Image.Source})";
}
