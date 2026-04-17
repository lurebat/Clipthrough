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
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly IClipStoreService _clipStore;
    private readonly IEmbeddingService _embeddings;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Contiguous storage: dim-size float blocks aligned with _ids.
    private long[] _ids = Array.Empty<long>();
    private float[] _vectors = Array.Empty<float>();
    private int _count;
    private int _dim;
    private bool _hasLoaded;

    public SemanticSearchService(IClipStoreService clipStore, IEmbeddingService embeddings)
    {
        _clipStore = clipStore ?? throw new ArgumentNullException(nameof(clipStore));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
    }

    public bool IsReady => _hasLoaded && _embeddings.IsReady;

    public int CachedCount => _count;

    public async Task RefreshCacheAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _clipStore.LoadAllEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                _ids = Array.Empty<long>();
                _vectors = Array.Empty<float>();
                _count = 0;
                _dim = 0;
                _hasLoaded = true;
                return;
            }

            var dim = loaded[0].Vector?.Length ?? 0;
            if (dim == 0)
            {
                _ids = Array.Empty<long>();
                _vectors = Array.Empty<float>();
                _count = 0;
                _dim = 0;
                _hasLoaded = true;
                return;
            }

            var ids = new long[loaded.Count];
            var flat = new float[loaded.Count * dim];
            var written = 0;
            for (var i = 0; i < loaded.Count; i++)
            {
                var emb = loaded[i];
                if (emb.Vector is null || emb.Vector.Length != dim)
                {
                    continue;
                }
                ids[written] = emb.ClipId;
                Buffer.BlockCopy(emb.Vector, 0, flat, written * dim * sizeof(float), dim * sizeof(float));
                written++;
            }

            if (written != ids.Length)
            {
                Array.Resize(ref ids, written);
                Array.Resize(ref flat, written * dim);
            }

            _ids = ids;
            _vectors = flat;
            _count = written;
            _dim = dim;
            _hasLoaded = true;
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
        if (_count == 0 || _dim == 0)
        {
            return Array.Empty<(long, float)>();
        }

        var query = await _embeddings.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        if (query is null || query.Length != _dim)
        {
            return Array.Empty<(long, float)>();
        }

        // Rank via dot product (vectors are L2-normalized ⇒ equals cosine similarity).
        var scores = new (long Id, float Score)[_count];
        var ids = _ids;
        var flat = _vectors;
        var dim = _dim;
        for (var i = 0; i < _count; i++)
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
}
