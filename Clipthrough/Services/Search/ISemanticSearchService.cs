using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services.Search;

/// <summary>
/// In-memory semantic search over cached clip embeddings.
/// Encapsulates the query-side of sem-04: embed the user's query, rank all cached
/// clip vectors by cosine similarity, return the top-K ids for RRF fusion with FTS.
/// </summary>
public interface ISemanticSearchService
{
    /// <summary>
    /// True unless the embedding model has proven unusable.
    ///
    /// Deliberately NOT "the model is loaded". The model loads lazily, and the
    /// only things that load it are the backlog worker and <see cref="QueryAsync"/>
    /// itself - so a caller that skipped the query while the model was unloaded
    /// would guarantee it stayed unloaded forever. On a history that is already
    /// fully embedded the worker never runs, which made semantic search silently
    /// dead from launch.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Number of embeddings currently cached in memory.</summary>
    int CachedCount { get; }

    /// <summary>Reload the embedding cache from storage. Safe to call frequently.</summary>
    Task RefreshCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Incrementally append newly-persisted embedding records to the in-memory cache without a full reload.
    /// Skips records whose clip id is already in the cache or whose vector dimension does not match.
    /// If the cache is empty or has no established dimension, falls back to a full <see cref="RefreshCacheAsync"/>.
    /// </summary>
    Task AppendEmbeddingsAsync(IReadOnlyList<ClipEmbeddingRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop the cached vectors for clips that no longer exist.
    ///
    /// The database sheds them on its own - <c>clip_embeddings.clip_id</c> cascades
    /// from <c>clips</c> - but this cache is an in-memory snapshot that is only
    /// otherwise rebuilt when the sensitivity rules change. Without this, a deleted
    /// clip stays semantically searchable for the rest of the session and keeps
    /// occupying a slot in every top-K it scores well in, displacing a result the
    /// user can actually open.
    /// </summary>
    Task RemoveEmbeddingsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rank cached clips by semantic similarity to <paramref name="text"/>.
    /// Returns clip ids paired with a cosine score in [-1, 1], sorted descending.
    /// </summary>
    Task<IReadOnlyList<(long ClipId, float Score)>> QueryAsync(string text, int topK, CancellationToken cancellationToken = default);
}
