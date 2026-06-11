using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly IClipStoreService _clipStore;
    private readonly IEmbeddingService _embeddings;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Atomic reference to the current immutable cache snapshot. Written via Volatile.Write,
    // read via Volatile.Read to guarantee all readers see a coherent snapshot.
    private CacheSnapshot _cache = CacheSnapshot.Empty;
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
        Volatile.Write(ref _cache, BuildSnapshot(loaded));
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
            var slotById = new Dictionary<long, int>(existing.Count);
            for (var i = 0; i < existing.Count; i++)
            {
                slotById[existing.Ids[i]] = i;
            }

            // Partition into in-place updates (clip already cached — its vector may
            // have been recomputed, e.g. after OCR) and appends (new clips). A
            // re-embedded clip MUST overwrite its stale vector, not be skipped.
            var adds = new List<ClipEmbeddingRecord>(records.Count);
            var updates = new List<ClipEmbeddingRecord>();
            foreach (var r in records)
            {
                if (r.Vector is not { Length: > 0 } v || v.Length != dim)
                {
                    continue;
                }
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
            var newIds = new long[newCount];
            var newVectors = new float[newCount * dim];

            Array.Copy(existing.Ids, newIds, existing.Count);
            Buffer.BlockCopy(existing.Vectors, 0, newVectors, 0, existing.Count * dim * sizeof(float));

            foreach (var u in updates)
            {
                var slot = slotById[u.ClipId];
                Buffer.BlockCopy(u.Vector!, 0, newVectors, slot * dim * sizeof(float), dim * sizeof(float));
            }

            for (var i = 0; i < adds.Count; i++)
            {
                newIds[existing.Count + i] = adds[i].ClipId;
                Buffer.BlockCopy(adds[i].Vector!, 0, newVectors, (existing.Count + i) * dim * sizeof(float), dim * sizeof(float));
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
        var scores = new (long Id, float Score)[count];

        for (var i = 0; i < count; i++)
        {
            var offset = i * dim;
            float s = 0f;
            for (var j = 0; j < dim; j++)
            {
                s += flat[offset + j] * query[j];
            }
            scores[i] = (ids[i], s);
        }

        var take = Math.Min(topK, scores.Length);
        Array.Sort(scores, static (a, b) => b.Score.CompareTo(a.Score));
        var result = new List<(long, float)>(take);
        for (var i = 0; i < take; i++)
        {
            result.Add(scores[i]);
        }
        return result;
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
