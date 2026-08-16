namespace Vellum;

/// <summary>
/// A value attached to a half-open range <c>[Start, End)</c> of a text buffer.
/// </summary>
/// <remarks>
/// <para>
/// Vellum.Core takes no dependency on Avalonia, so it cannot use
/// <c>Avalonia.Utilities.ValueSpan&lt;T&gt;</c>. The shape is deliberately the same, so
/// Vellum.Avalonia can convert without reinterpreting anything.
/// </para>
/// <para>
/// This type carries no opinion about how spans relate to each other. Sortedness,
/// non-overlap and equality-merging are invariants of the collections that hold spans,
/// not of an individual span.
/// </para>
/// </remarks>
/// <typeparam name="T">The attached value, normally a mark set.</typeparam>
public readonly record struct ValueSpan<T>
{
    /// <summary>Creates a span.</summary>
    /// <param name="start">The first offset covered. Must not be negative.</param>
    /// <param name="length">The number of offsets covered. Must not be negative.</param>
    /// <param name="value">The attached value.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> or <paramref name="length"/> is negative, or the two together
    /// overflow <see cref="int"/>.
    /// </exception>
    public ValueSpan(int start, int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // End is used in arithmetic throughout position mapping; a silently wrapped
        // end offset would be far harder to diagnose than a throw here.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, int.MaxValue - start);

        Start = start;
        Length = length;
        Value = value;
    }

    /// <summary>The first offset covered by this span.</summary>
    public int Start { get; }

    /// <summary>The number of offsets covered.</summary>
    public int Length { get; }

    /// <summary>The attached value.</summary>
    public T Value { get; }

    /// <summary>The offset just past the end of this span.</summary>
    public int End => Start + Length;

    /// <summary>Whether the span covers no offsets at all.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Whether <paramref name="offset"/> falls inside the half-open range.</summary>
    /// <param name="offset">The offset to test.</param>
    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>
    /// Whether this span shares at least one offset with <paramref name="other"/>. Empty
    /// spans overlap nothing, including each other.
    /// </summary>
    /// <param name="other">The span to test against.</param>
    public bool OverlapsWith(ValueSpan<T> other) =>
        !IsEmpty && !other.IsEmpty && Start < other.End && other.Start < End;

    /// <summary>Returns the same range carrying a different value.</summary>
    /// <param name="value">The replacement value.</param>
    public ValueSpan<T> WithValue(T value) => new(Start, Length, value);

    /// <summary>Returns the same value over a different range.</summary>
    /// <param name="start">The replacement start offset.</param>
    /// <param name="length">The replacement length.</param>
    public ValueSpan<T> WithRange(int start, int length) => new(start, length, Value);

    /// <summary>Splits the span into its three components.</summary>
    /// <param name="start">Receives <see cref="Start"/>.</param>
    /// <param name="length">Receives <see cref="Length"/>.</param>
    /// <param name="value">Receives <see cref="Value"/>.</param>
    public void Deconstruct(out int start, out int length, out T value)
    {
        start = Start;
        length = Length;
        value = Value;
    }

    /// <summary>Formats as <c>[start..end) = value</c>.</summary>
    public override string ToString() => $"[{Start}..{End}) = {Value}";
}
