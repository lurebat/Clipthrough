using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services.Search;

/// <summary>
/// In-memory semantic search over cached clip embeddings.
/// Encapsulates the query-side of sem-04: embed the user's query, rank all cached
/// clip vectors by cosine similarity, return the top-K ids for RRF fusion with FTS.
/// </summary>
public interface ISemanticSearchService
{
    /// <summary>True once the cache has been populated at least once and the embedding model is ready.</summary>
    bool IsReady { get; }

    /// <summary>Number of embeddings currently cached in memory.</summary>
    int CachedCount { get; }

    /// <summary>Reload the embedding cache from storage. Safe to call frequently.</summary>
    Task RefreshCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rank cached clips by semantic similarity to <paramref name="text"/>.
    /// Returns clip ids paired with a cosine score in [-1, 1], sorted descending.
    /// </summary>
    Task<IReadOnlyList<(long ClipId, float Score)>> QueryAsync(string text, int topK, CancellationToken cancellationToken = default);
}
