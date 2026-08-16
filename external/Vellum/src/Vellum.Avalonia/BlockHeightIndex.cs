namespace Vellum.Avalonia;

/// <summary>
/// The vertical position of every block in a document, whether or not it has been measured,
/// per architecture 4.6.
/// </summary>
/// <remarks>
/// <para>
/// Virtualization needs two answers that are cheap only if something keeps them: where does
/// block <c>i</c> start, and which block covers pixel <c>y</c>. Both are prefix sums over block
/// heights, so a Fenwick tree over the heights answers each in logarithmic time and updates a
/// height in the same. A running array would answer the first in constant time and the second
/// in logarithmic, but would cost linear time on every measurement, which is the operation
/// that happens most.
/// </para>
/// <para>
/// The subtlety is not the tree, it is the blocks nobody has measured. Their height has to be
/// <em>guessed</em>, because scrolling to the end of a document may not measure everything on
/// the way. The guess used here is the mean of the heights actually measured so far, which
/// starts as a caller-supplied estimate and improves as the user scrolls. That means a block's
/// stored height changes when its neighbours are measured, and the total moves under the
/// scrollbar — unavoidable with estimates, and the reason
/// <see cref="Measured"/> is exposed so a caller can tell a real answer from a guess.
/// </para>
/// </remarks>
public sealed class BlockHeightIndex
{
    private double[] _tree = [];
    private double[] _height = [];
    private bool[] _measured = [];
    private int _count;
    private int _measuredCount;
    private double _measuredTotal;
    private double _seed;

    /// <summary>Creates an index with no blocks.</summary>
    /// <param name="estimate">
    /// The height to assume for a block before anything has been measured. Must be positive:
    /// a zero estimate would put every unmeasured block at the same pixel and make
    /// <see cref="IndexAt"/> meaningless.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="estimate"/> is not positive and finite.</exception>
    public BlockHeightIndex(double estimate = DefaultEstimate)
    {
        if (!(estimate > 0) || double.IsInfinity(estimate))
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimate), estimate, "An estimated height must be positive and finite.");
        }

        _seed = estimate;
    }

    /// <summary>The height assumed for a block before any block has been measured.</summary>
    public const double DefaultEstimate = 20;

    /// <summary>The number of blocks.</summary>
    public int Count => _count;

    /// <summary>The height currently assumed for a block nobody has measured.</summary>
    /// <remarks>The mean of the measured heights, or the seed estimate until there are any.</remarks>
    public double Estimate => _measuredCount == 0 ? _seed : _measuredTotal / _measuredCount;

    /// <summary>The height of every block together, measured and estimated alike.</summary>
    public double TotalHeight => SumTo(_count);

    /// <summary>Whether a block's height is a measurement rather than an estimate.</summary>
    /// <param name="index">The block's index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a block.</exception>
    public bool Measured(int index)
    {
        CheckIndex(index);

        return _measured[index];
    }

    /// <summary>The height of one block.</summary>
    /// <param name="index">The block's index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a block.</exception>
    public double HeightOf(int index)
    {
        CheckIndex(index);

        return _height[index];
    }

    /// <summary>The distance from the top of the document to the top of a block.</summary>
    /// <param name="index">The block's index. <see cref="Count"/> is allowed and gives the total.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public double OffsetOf(int index)
    {
        if (index < 0 || index > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a block in this index.");
        }

        return SumTo(index);
    }

    /// <summary>
    /// The block covering a vertical position, clamped to the document.
    /// </summary>
    /// <remarks>
    /// The block whose half-open range <c>[offset, offset + height)</c> contains
    /// <paramref name="y"/>. A position past the end reports the last block rather than
    /// nothing, because a drag below the document selects to its end rather than to nowhere.
    /// </remarks>
    /// <param name="y">The distance from the top of the document.</param>
    /// <exception cref="InvalidOperationException">There are no blocks.</exception>
    public int IndexAt(double y)
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("An empty index covers no position.");
        }

        if (!(y > 0))
        {
            return 0;
        }

        // Fenwick descent: walk the tree from its highest power of two down, taking a branch
        // whenever the prefix sum there still fits under y. What is left is the count of whole
        // blocks that end at or before y, which is the index of the one covering it.
        var index = 0;
        var remaining = y;

        for (var step = HighestPowerOfTwo(_tree.Length); step > 0; step >>= 1)
        {
            var next = index + step;

            if (next < _tree.Length && _tree[next] <= remaining)
            {
                index = next;
                remaining -= _tree[next];
            }
        }

        return Math.Min(index, _count - 1);
    }

    /// <summary>
    /// Records a block's measured height.
    /// </summary>
    /// <param name="index">The block's index.</param>
    /// <param name="height">The measured height.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is not a block, or <paramref name="height"/> is negative or not finite.
    /// </exception>
    public void SetHeight(int index, double height)
    {
        CheckIndex(index);

        if (height < 0 || double.IsNaN(height) || double.IsInfinity(height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, "A measured height must be zero or more, and finite.");
        }

        if (_measured[index])
        {
            _measuredTotal -= _height[index];
        }
        else
        {
            _measuredCount++;
            _measured[index] = true;
        }

        _measuredTotal += height;

        Assign(index, height);

        // The estimate has moved, so every block still carrying one is now wrong. Rewriting
        // them here keeps the invariant that a stored height is always the current best answer,
        // which is what lets OffsetOf and IndexAt be simple prefix sums.
        Reestimate();
    }

    /// <summary>Inserts blocks before an index, all of them unmeasured.</summary>
    /// <param name="index">Where the first new block goes. <see cref="Count"/> appends.</param>
    /// <param name="count">How many to insert.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range or the count is negative.</exception>
    public void Insert(int index, int count)
    {
        if (index < 0 || index > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a place in this index.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
        {
            return;
        }

        var heights = new double[_count + count];
        var measured = new bool[_count + count];

        Array.Copy(_height, 0, heights, 0, index);
        Array.Copy(_measured, 0, measured, 0, index);
        Array.Copy(_height, index, heights, index + count, _count - index);
        Array.Copy(_measured, index, measured, index + count, _count - index);

        var estimate = Estimate;

        for (var i = index; i < index + count; i++)
        {
            heights[i] = estimate;
        }

        Rebuild(heights, measured);
    }

    /// <summary>Removes blocks.</summary>
    /// <param name="index">The first block to remove.</param>
    /// <param name="count">How many to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside the index.</exception>
    public void Remove(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (index < 0 || index + count > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a range in this index.");
        }

        if (count == 0)
        {
            return;
        }

        var heights = new double[_count - count];
        var measured = new bool[_count - count];

        Array.Copy(_height, 0, heights, 0, index);
        Array.Copy(_measured, 0, measured, 0, index);
        Array.Copy(_height, index + count, heights, index, _count - index - count);
        Array.Copy(_measured, index + count, measured, index, _count - index - count);

        for (var i = index; i < index + count; i++)
        {
            if (_measured[i])
            {
                _measuredCount--;
                _measuredTotal -= _height[i];
            }
        }

        Rebuild(heights, measured);
    }

    /// <summary>Sets the number of blocks, forgetting every measurement.</summary>
    /// <remarks>
    /// For a document replaced outright, where nothing is known to correspond. The estimate
    /// learned so far is kept as the seed, because it is still the best guess available about
    /// how tall a block in this control tends to be.
    /// </remarks>
    /// <param name="count">The number of blocks.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Reset(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        _seed = Estimate;
        _measuredCount = 0;
        _measuredTotal = 0;

        var heights = new double[count];
        var measured = new bool[count];

        Array.Fill(heights, _seed);

        Rebuild(heights, measured);
    }

    private void Reestimate()
    {
        var estimate = Estimate;

        for (var i = 0; i < _count; i++)
        {
            if (!_measured[i] && _height[i] != estimate)
            {
                Assign(i, estimate);
            }
        }
    }

    private void Rebuild(double[] heights, bool[] measured)
    {
        _height = heights;
        _measured = measured;
        _count = heights.Length;
        _tree = new double[_count + 1];

        // Building in place: each node adds itself to its parent, which is linear rather than
        // the n log n of inserting one at a time.
        for (var i = 0; i < _count; i++)
        {
            _tree[i + 1] += heights[i];

            var parent = i + 1 + ((i + 1) & -(i + 1));

            if (parent <= _count)
            {
                _tree[parent] += _tree[i + 1];
            }
        }

        Reestimate();
    }

    private void Assign(int index, double height)
    {
        var delta = height - _height[index];

        _height[index] = height;

        if (delta == 0)
        {
            return;
        }

        for (var i = index + 1; i <= _count; i += i & -i)
        {
            _tree[i] += delta;
        }
    }

    private double SumTo(int count)
    {
        var sum = 0d;

        for (var i = count; i > 0; i -= i & -i)
        {
            sum += _tree[i];
        }

        return sum;
    }

    private void CheckIndex(int index)
    {
        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a block in this index.");
        }
    }

    private static int HighestPowerOfTwo(int value)
    {
        var power = 1;

        while (power << 1 < value)
        {
            power <<= 1;
        }

        return value <= 1 ? 0 : power;
    }
}
