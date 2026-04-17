using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services.Search;

/// <summary>
/// Computes sentence embeddings for semantic search.
/// Embeddings are L2-normalized so cosine similarity reduces to a dot product.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Dimensionality of the embedding vectors produced by <see cref="EmbedBatchAsync"/>.</summary>
    int Dimensions { get; }

    /// <summary>True if the underlying model is loaded and ready. Loading may happen lazily on first use.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Embed one or more input strings. Returns a list of L2-normalized float vectors of length <see cref="Dimensions"/>,
    /// one per input, in the same order. Empty/whitespace inputs return a zero-filled vector.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>Convenience wrapper around <see cref="EmbedBatchAsync"/> for a single input.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
