using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// The accumulated position movement of a sequence of steps.
/// </summary>
/// <remarks>
/// <para>
/// A transaction is several steps, and anything holding a position from before it needs to be
/// rewritten once, through all of them, not step by step. That is what a mapping is: the maps
/// in application order, folded left to right.
/// </para>
/// <para>
/// One map is recorded per step even when the step moves nothing, so index <c>i</c> in a
/// mapping always corresponds to step <c>i</c> in the transaction.
/// </para>
/// </remarks>
public sealed class Mapping
{
    private readonly ImmutableArray<StepMap> _maps;

    private Mapping(ImmutableArray<StepMap> maps) => _maps = maps;

    /// <summary>A mapping over no steps, which moves nothing.</summary>
    public static Mapping Empty { get; } = new([]);

    /// <summary>Creates a mapping over a sequence of maps, in application order.</summary>
    /// <param name="maps">The maps.</param>
    public static Mapping Of(IEnumerable<StepMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);

        return new Mapping(maps.ToImmutableArray());
    }

    /// <summary>Creates a mapping over a sequence of maps, in application order.</summary>
    /// <param name="maps">The maps.</param>
    public static Mapping Of(params StepMap[] maps) => Of((IEnumerable<StepMap>)maps);

    /// <summary>The maps, in the order their steps were applied.</summary>
    public ImmutableArray<StepMap> Maps => _maps;

    /// <summary>How many maps — and therefore how many steps — this covers.</summary>
    public int Count => _maps.Length;

    /// <summary>Whether every position survives this mapping unmoved.</summary>
    public bool IsIdentity => _maps.All(map => map.IsIdentity);

    /// <summary>Returns this mapping with one more map on the end.</summary>
    /// <param name="map">The map to append.</param>
    public Mapping Append(StepMap map) => new(_maps.Add(map));

    /// <summary>Returns this mapping followed by another.</summary>
    /// <param name="other">The mapping to append.</param>
    public Mapping AppendMapping(Mapping other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.Count == 0 ? this : new Mapping(_maps.AddRange(other._maps));
    }

    /// <summary>Rewrites a position through every map in turn.</summary>
    /// <param name="pos">A position in the document before the first step.</param>
    /// <param name="assoc">Which side of the position to bind to.</param>
    public int Map(int pos, Assoc assoc = Assoc.After) => MapWithResult(pos, assoc).Pos;

    /// <summary>
    /// Rewrites a position through every map in turn, reporting whether any of them deleted
    /// what it pointed at.
    /// </summary>
    /// <param name="pos">A position in the document before the first step.</param>
    /// <param name="assoc">Which side of the position to bind to.</param>
    /// <remarks>
    /// Deletion is sticky. A position whose content is removed and whose location is then
    /// shifted by a later step is still pointing at content that no longer exists, and a
    /// caller checking identity needs to hear about the first step, not the last.
    /// </remarks>
    public MapResult MapWithResult(int pos, Assoc assoc = Assoc.After)
    {
        var deleted = false;

        foreach (var map in _maps)
        {
            var result = map.MapWithResult(pos, assoc);
            pos = result.Pos;
            deleted |= result.Deleted;
        }

        return new MapResult(pos, deleted);
    }

    /// <summary>Rewrites a position through a contiguous subrange of the maps.</summary>
    /// <param name="pos">A position in the document before map <paramref name="from"/>.</param>
    /// <param name="from">The first map to apply.</param>
    /// <param name="to">One past the last map to apply.</param>
    /// <param name="assoc">Which side of the position to bind to.</param>
    /// <remarks>
    /// Rebasing needs this: a step being replayed onto a document that has already seen some
    /// of these steps must only be moved by the ones it has not seen.
    /// </remarks>
    public MapResult MapRange(int pos, int from, int to, Assoc assoc = Assoc.After)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(to, Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);

        var deleted = false;

        for (var i = from; i < to; i++)
        {
            var result = _maps[i].MapWithResult(pos, assoc);
            pos = result.Pos;
            deleted |= result.Deleted;
        }

        return new MapResult(pos, deleted);
    }

    /// <summary>The mapping that undoes this one.</summary>
    /// <remarks>
    /// Both the order and each individual map have to be reversed. Undoing a sequence means
    /// walking back out of it, and each map's inverse is only correct relative to the document
    /// the later maps have already been backed out of.
    /// </remarks>
    public Mapping Invert()
    {
        var inverted = ImmutableArray.CreateBuilder<StepMap>(_maps.Length);

        for (var i = _maps.Length - 1; i >= 0; i--)
        {
            inverted.Add(_maps[i].Invert());
        }

        return new Mapping(inverted.MoveToImmutable());
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Count == 0 ? "mapping[]" : $"mapping[{string.Join(", ", _maps)}]";
}
