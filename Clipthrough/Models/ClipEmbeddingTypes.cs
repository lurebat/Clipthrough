namespace Clipthrough.Models;

/// <summary>A clip that has been claimed for embedding, with the text to feed to the model.</summary>
public sealed record ClipEmbeddingCandidate(long ClipId, string TextToEmbed);

/// <summary>Result of embedding a clip: the raw L2-normalized vector to persist.</summary>
public sealed record ClipEmbeddingRecord(long ClipId, float[] Vector);

/// <summary>A row from <c>clip_embeddings</c> for loading into the in-memory cache.</summary>
public sealed record ClipEmbedding(long ClipId, float[] Vector, string ModelVersion);

/// <summary>Coverage snapshot for the semantic index.</summary>
public sealed record EmbeddingCoverage(long EligibleTotal, long Embedded, long Pending, long Failed, long Excluded)
{
    public double FractionReady => EligibleTotal <= 0 ? 1.0 : (double)Embedded / EligibleTotal;
}
