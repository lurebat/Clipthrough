using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services.Search;

/// <summary>
/// Contiguous-array cache + cosine scorer over clip embeddings. Embedding vectors
/// coming from <see cref="IEmbeddingService"/> are already L2-normalized, so cosine
/// similarity reduces to a simple dot product.
///
/// The cache is published as a single immutable <see cref="CacheSnapshot"/> reference swapped
/// atomically via <see cref="Volatile"/>. <see cref="QueryAsync"/> captures a local reference
/// once at the top of its call so a concurrent <see cref="RefreshCacheAsync"/> can never
/// produce a torn read (mismatched ids/vectors/count/dim). (U14)
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    /// <summary>
    /// Immutable snapshot of the current embedding cache. All fields are final once constructed;
    /// the reference is swapped atomically via <c>Volatile</c> so consumers never see a partial state.
    /// </summary>
    /// <remarks>
    /// <see cref="Ids"/> and <see cref="Vectors"/> may be longer than <see cref="Count"/>
    /// needs: they carry spare capacity so appends do not have to reallocate every time.
    /// Everything at or past <see cref="Count"/> is reserved space and must be ignored.
    /// </remarks>
    private sealed class CacheSnapshot
    {
        public static readonly CacheSnapshot Empty = new(Array.Empty<long>(), Array.Empty<float>(), 0, 0);

        public CacheSnapshot(long[] ids, float[] vectors, int count, int dim)
        {
            Ids = ids;
            Vectors = vectors;
            Count = count;
            Dim = dim;
        }

        public long[] Ids { get; }
        public float[] Vectors { get; }
        public int Count { get; }
        public int Dim { get; }
    }

    // Buffer.BlockCopy counts bytes in an int, so a float array longer than this
    // cannot be copied at all. Growth stops short of it rather than overflowing.
    private const int MaxVectorFloats = int.MaxValue / sizeof(float);

    private readonly IClipStoreService _clipStore;
    private readonly IEmbeddingService _embeddings;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Atomic reference to the current immutable cache snapshot. Written via Volatile.Write,
    // read via Volatile.Read to guarantee all readers see a coherent snapshot.
    private CacheSnapshot _cache = CacheSnapshot.Empty;

    // Clip id -> its slot in _cache. Only ever touched while _refreshGate is held, so it
    // is not part of the published snapshot. Kept across appends rather than rebuilt per
    // batch: rebuilding it was O(N) work and an O(N) allocation for every batch of 32,
    // which is the same quadratic backfill cost as copying the vectors was.
    private Dictionary<long, int> _slotById = new();
    private bool _hasLoaded;

    public SemanticSearchService(IClipStoreService clipStore, IEmbeddingService embeddings)
    {
        _clipStore = clipStore ?? throw new ArgumentNullException(nameof(clipStore));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
    }

    public bool IsReady => _hasLoaded && _embeddings.IsReady;

    public int CachedCount => Volatile.Read(ref _cache).Count;

    public async Task RefreshCacheAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReloadLocked(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Reloads the whole cache from the store. Caller MUST hold _refreshGate.
    private async Task ReloadLocked(CancellationToken cancellationToken)
    {
        var loaded = await _clipStore.LoadAllEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = BuildSnapshot(loaded);

        // Built from the snapshot rather than from `loaded`, because BuildSnapshot drops
        // records whose vector has the wrong length - indexing off `loaded` would assign
        // every id after the first bad one a slot holding some other clip's vector.
        var slots = new Dictionary<long, int>(snapshot.Count);
        for (var i = 0; i < snapshot.Count; i++)
        {
            slots[snapshot.Ids[i]] = i;
        }

        _slotById = slots;
        Volatile.Write(ref _cache, snapshot);
        _hasLoaded = true;
    }

    public async Task AppendEmbeddingsAsync(IReadOnlyList<ClipEmbeddingRecord> records, CancellationToken cancellationToken = default)
    {
        if (records is null || records.Count == 0) return;

        // Hold the same gate as RefreshCacheAsync: the read-modify-write of _cache
        // below must be atomic against a concurrent refresh or a second append, or
        // one writer's snapshot silently clobbers the other's (lost embeddings).
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = Volatile.Read(ref _cache);

            // No established dimension yet — a full (locked) reload is the only way
            // to learn it, and it subsumes these records once they are persisted.
            if (existing.Count == 0 || existing.Dim == 0)
            {
                await ReloadLocked(cancellationToken).ConfigureAwait(false);
                return;
            }

            var dim = existing.Dim;
            var slotById = _slotById;

            // Collapse repeats within the batch, newest wins. The slot map holds one
            // entry per clip, so letting a duplicate id through would append the same
            // clip twice and leave the older copy permanently unreachable from the map
            // - a stale vector that no later re-embedding could ever overwrite.
            var latest = new Dictionary<long, ClipEmbeddingRecord>(records.Count);
            foreach (var r in records)
            {
                if (r.Vector is not { Length: > 0 } v || v.Length != dim)
                {
                    continue;
                }
                latest[r.ClipId] = r;
            }

            // Partition into in-place updates (clip already cached — its vector may
            // have been recomputed, e.g. after OCR) and appends (new clips). A
            // re-embedded clip MUST overwrite its stale vector, not be skipped.
            var adds = new List<ClipEmbeddingRecord>(latest.Count);
            var updates = new List<ClipEmbeddingRecord>();
            foreach (var r in latest.Values)
            {
                if (slotById.ContainsKey(r.ClipId))
                {
                    updates.Add(r);
                }
                else
                {
                    adds.Add(r);
                }
            }

            if (adds.Count == 0 && updates.Count == 0) return;

            var newCount = existing.Count + adds.Count;

            // Appending into the spare capacity of the live arrays is what keeps a
            // backfill linear: the alternative copies the entire cache once per
            // batch of 32, which is O(N^2) and hundreds of gigabytes of memcpy over
            // a 100k library. It is safe only for pure appends. A reader holding an
            // older snapshot never looks past its own Count, so writing beyond that
            // point is invisible to it, and the Volatile.Write below publishes the
            // larger Count only after those writes are complete. An in-place *update*
            // has no such protection - it would rewrite a vector a reader is scoring
            // right now - so any update forces the copy.
            var canAppendInPlace = updates.Count == 0
                && newCount <= existing.Ids.Length
                && (long)newCount * dim <= existing.Vectors.Length;

            long[] newIds;
            float[] newVectors;
            if (canAppendInPlace)
            {
                newIds = existing.Ids;
                newVectors = existing.Vectors;
            }
            else
            {
                var capacity = GrowCapacity(existing.Ids.Length, newCount, dim);
                newIds = new long[capacity];
                newVectors = new float[capacity * dim];

                Array.Copy(existing.Ids, newIds, existing.Count);
                Buffer.BlockCopy(existing.Vectors, 0, newVectors, 0, existing.Count * dim * sizeof(float));

                foreach (var u in updates)
                {
                    var slot = slotById[u.ClipId];
                    Buffer.BlockCopy(u.Vector!, 0, newVectors, slot * dim * sizeof(float), dim * sizeof(float));
                }
            }

            for (var i = 0; i < adds.Count; i++)
            {
                var slot = existing.Count + i;
                newIds[slot] = adds[i].ClipId;
                Buffer.BlockCopy(adds[i].Vector!, 0, newVectors, slot * dim * sizeof(float), dim * sizeof(float));
                slotById[adds[i].ClipId] = slot;
            }

            Volatile.Write(ref _cache, new CacheSnapshot(newIds, newVectors, newCount, dim));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<IReadOnlyList<(long ClipId, float Score)>> QueryAsync(
        string text,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || topK <= 0)
        {
            return Array.Empty<(long, float)>();
        }
        if (!_embeddings.IsReady)
        {
            return Array.Empty<(long, float)>();
        }
        if (!_hasLoaded)
        {
            await RefreshCacheAsync(cancellationToken).ConfigureAwait(false);
        }

        // Capture the snapshot reference ONCE so a concurrent RefreshCacheAsync cannot
        // produce a torn read between ids/vectors/count/dim checks. (U14)
        var snapshot = Volatile.Read(ref _cache);
        if (snapshot.Count == 0 || snapshot.Dim == 0)
        {
            return Array.Empty<(long, float)>();
        }

        var query = await _embeddings.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        if (query is null || query.Length != snapshot.Dim)
        {
            return Array.Empty<(long, float)>();
        }

        // Rank via dot product (vectors are L2-normalized => equals cosine similarity).
        var count = snapshot.Count;
        var ids = snapshot.Ids;
        var flat = snapshot.Vectors;
        var dim = snapshot.Dim;

        var selector = new TopKSelector(Math.Min(topK, count));
        var queryVector = query.AsSpan();
        var vectors = flat.AsSpan();

        for (var i = 0; i < count; i++)
        {
            selector.Offer(ids[i], Dot(vectors.Slice(i * dim, dim), queryVector));
        }

        return selector.ToDescendingList();
    }

    /// <summary>
    /// Dot product of two equal-length vectors, widened to whatever SIMD the machine has.
    /// This is the whole cost of a semantic query: one pass over every cached embedding,
    /// on every throttled keystroke. Scalar, a 100k library of 384-dimension vectors is
    /// 38 million multiply-adds and measured 44 ms per keystroke; widened it is 24 ms.
    /// </summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var width = Vector<float>.Count;
        var accumulator = Vector<float>.Zero;
        var i = 0;

        for (; i <= a.Length - width; i += width)
        {
            accumulator += new Vector<float>(a.Slice(i, width)) * new Vector<float>(b.Slice(i, width));
        }

        var sum = Vector.Sum(accumulator);

        // Dimensions are not guaranteed to be a multiple of the SIMD width.
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Keeps the highest-scoring <c>capacity</c> candidates seen so far, as a min-heap keyed
    /// on score. The weakest survivor sits at the root, so rejecting a candidate - which is
    /// what happens to almost all of them - costs one comparison.
    /// </summary>
    /// <remarks>
    /// Replaces sorting every candidate to keep the top fifty: that was O(N log N) plus a
    /// twelve-byte-per-clip array allocated on every keystroke. Ties are broken arbitrarily,
    /// as they were by the unstable sort this replaced.
    /// </remarks>
    private struct TopKSelector(int capacity)
    {
        private readonly long[] _ids = new long[capacity];
        private readonly float[] _scores = new float[capacity];
        private int _count = 0;

        public void Offer(long id, float score)
        {
            if (_count < _ids.Length)
            {
                _ids[_count] = id;
                _scores[_count] = score;
                SiftUp(_count++);
                return;
            }

            // NaN fails this comparison, so a degenerate vector is dropped rather than
            // poisoning the ranking.
            if (_ids.Length == 0 || score <= _scores[0])
            {
                return;
            }

            _ids[0] = id;
            _scores[0] = score;
            SiftDown();
        }

        public readonly IReadOnlyList<(long ClipId, float Score)> ToDescendingList()
        {
            var result = new (long ClipId, float Score)[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = (_ids[i], _scores[i]);
            }

            Array.Sort(result, static (a, b) => b.Score.CompareTo(a.Score));
            return result;
        }

        private readonly void SiftUp(int child)
        {
            while (child > 0)
            {
                var parent = (child - 1) / 2;
                if (_scores[parent] <= _scores[child])
                {
                    return;
                }

                Swap(parent, child);
                child = parent;
            }
        }

        private readonly void SiftDown()
        {
            var parent = 0;
            while (true)
            {
                var left = (2 * parent) + 1;
                var right = left + 1;
                var smallest = parent;

                if (left < _count && _scores[left] < _scores[smallest])
                {
                    smallest = left;
                }

                if (right < _count && _scores[right] < _scores[smallest])
                {
                    smallest = right;
                }

                if (smallest == parent)
                {
                    return;
                }

                Swap(parent, smallest);
                parent = smallest;
            }
        }

        private readonly void Swap(int x, int y)
        {
            (_scores[x], _scores[y]) = (_scores[y], _scores[x]);
            (_ids[x], _ids[y]) = (_ids[y], _ids[x]);
        }
    }

    /// <summary>
    /// Grows the cache by a fraction of its current size rather than to exactly what this
    /// batch needs, so a long backfill reallocates O(log N) times instead of once per batch.
    /// </summary>
    /// <remarks>
    /// A quarter is a deliberate compromise. Growing by a constant factor r copies about
    /// N/(r-1) rows in total, so 1.25 turns a 100k backfill in batches of 32 from ~223 GiB
    /// of memcpy into under a gigabyte, while wasting at most 25% of the cache - which for
    /// a 154 MB embedding cache matters as much as the copying does. Doubling would trade
    /// another 0.2% of that copying for a further 150 MB of resident memory.
    /// </remarks>
    private static int GrowCapacity(int currentCapacity, int required, int dim)
    {
        var ceiling = MaxVectorFloats / dim;
        if (required >= ceiling)
        {
            return required;
        }

        var headroom = Math.Max(64, currentCapacity / 4);
        var grown = currentCapacity <= int.MaxValue - headroom ? currentCapacity + headroom : int.MaxValue;
        return Math.Min(Math.Max(required, grown), ceiling);
    }

    private static CacheSnapshot BuildSnapshot(IReadOnlyList<ClipEmbedding> loaded)
    {
        if (loaded.Count == 0) return CacheSnapshot.Empty;

        var dim = loaded[0].Vector?.Length ?? 0;
        if (dim == 0) return CacheSnapshot.Empty;

        var ids = new long[loaded.Count];
        var flat = new float[loaded.Count * dim];
        var written = 0;
        for (var i = 0; i < loaded.Count; i++)
        {
            var emb = loaded[i];
            if (emb.Vector is null || emb.Vector.Length != dim) continue;
            ids[written] = emb.ClipId;
            Buffer.BlockCopy(emb.Vector, 0, flat, written * dim * sizeof(float), dim * sizeof(float));
            written++;
        }

        if (written != ids.Length)
        {
            Array.Resize(ref ids, written);
            Array.Resize(ref flat, written * dim);
        }

        return new CacheSnapshot(ids, flat, written, dim);
    }
}
