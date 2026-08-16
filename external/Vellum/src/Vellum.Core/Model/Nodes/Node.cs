using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Vellum;

/// <summary>
/// A node in the block-level document tree.
/// </summary>
/// <remarks>
/// <para>
/// The block level is a typed tree because tables, list nesting and blockquotes are
/// genuinely structural. The inline level inside a paragraph is deliberately not a tree —
/// see <see cref="InlineContent"/>.
/// </para>
/// <para>
/// Nodes are immutable and compare structurally. Structural equality is what the undo
/// round-trip property test rests on, and it has to be written by hand: records compare
/// <see cref="ImmutableArray{T}"/> fields by reference, which would make two identical
/// documents compare unequal.
/// </para>
/// </remarks>
public abstract class Node : IEquatable<Node>
{
    /// <summary>
    /// The number of positions inside this node, excluding its own boundary tokens.
    /// </summary>
    /// <remarks>
    /// Each character counts 1, each leaf node counts 1, and each non-leaf child counts its
    /// content plus the 2 positions of its open and close boundaries.
    /// </remarks>
    public abstract int ContentSize { get; }

    /// <summary>Whether this node has no interior at all — a rule or a block image.</summary>
    public abstract bool IsLeaf { get; }

    /// <summary>
    /// The number of positions this node occupies in its parent: 1 for a leaf, otherwise its
    /// content plus its two boundary tokens.
    /// </summary>
    public int NodeSize => IsLeaf ? 1 : ContentSize + 2;

    /// <summary>The child nodes, empty for paragraphs and leaves.</summary>
    public abstract IReadOnlyList<Node> Children { get; }

    /// <summary>A short name used in diagnostics and validation messages.</summary>
    public abstract string TypeName { get; }

    /// <summary>Returns this node with different children.</summary>
    /// <param name="children">The replacement children.</param>
    /// <exception cref="ArgumentException">A child has a type this node cannot hold.</exception>
    /// <exception cref="InvalidOperationException">This node cannot hold children at all.</exception>
    public abstract Node WithChildren(IReadOnlyList<Node> children);

    /// <inheritdoc/>
    public bool Equals(Node? other) =>
        ReferenceEquals(this, other) || (other is not null && GetType() == other.GetType() && EqualsCore(other));

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj) => Equals(obj as Node);

    /// <inheritdoc/>
    public abstract override int GetHashCode();

    /// <summary>
    /// Compares against a node already known to be of the same runtime type.
    /// </summary>
    /// <param name="other">The node to compare against.</param>
    protected abstract bool EqualsCore(Node other);

    /// <summary>Compares two child lists element by element.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    protected static bool ChildrenEqual(IReadOnlyList<Node> left, IReadOnlyList<Node> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Folds a child list into a hash.</summary>
    /// <param name="hash">The hash to add to.</param>
    /// <param name="children">The children to fold in.</param>
    protected static void HashChildren(ref HashCode hash, IReadOnlyList<Node> children)
    {
        hash.Add(children.Count);

        foreach (var child in children)
        {
            hash.Add(child);
        }
    }

    /// <summary>Sums the sizes the children occupy in this node.</summary>
    /// <param name="children">The children to measure.</param>
    protected static int SumOfNodeSizes(IReadOnlyList<Node> children)
    {
        var total = 0;

        foreach (var child in children)
        {
            total += child.NodeSize;
        }

        return total;
    }

    /// <summary>
    /// Copies <paramref name="children"/> into a typed array, rejecting anything of the
    /// wrong type.
    /// </summary>
    /// <typeparam name="T">The child type this node accepts.</typeparam>
    /// <param name="children">The children to check.</param>
    /// <param name="parameterName">The parameter to blame in the exception.</param>
    /// <exception cref="ArgumentException">A child is null or of the wrong type.</exception>
    protected static ImmutableArray<T> RequireAll<T>(
        IReadOnlyList<Node> children, string parameterName)
        where T : Node
    {
        ArgumentNullException.ThrowIfNull(children);

        var builder = ImmutableArray.CreateBuilder<T>(children.Count);

        foreach (var child in children)
        {
            if (child is not T typed)
            {
                throw new ArgumentException(
                    $"Expected {typeof(T).Name} but found {child?.TypeName ?? "null"}.",
                    parameterName);
            }

            builder.Add(typed);
        }

        return builder.ToImmutable();
    }
}

/// <summary>A node that can appear where block content is expected.</summary>
public abstract class BlockNode : Node;

/// <summary>Guards for the enum-valued attributes nodes carry.</summary>
/// <remarks>
/// A cast is not a check. <c>(TextAlign)999</c> compiles, so an attribute set by a caller,
/// deserialized from a file, or read back from an older version can be a value no renderer has
/// a case for. Node constructors enforce their own attributes for the same reason they enforce
/// child types: it is the only place that cannot be bypassed (architecture §7).
/// </remarks>
internal static class NodeAttr
{
    /// <summary>Throws unless <paramref name="value"/> is a named member of its enum.</summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="value">The value to check.</param>
    /// <param name="name">The parameter being checked, supplied by the compiler.</param>
    internal static void Require<T>(
        T value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                name, value, $"{typeof(T).Name} has no member with this value.");
        }
    }
}
