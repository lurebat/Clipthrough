using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services.Search;

/// <summary>
/// Background worker that walks the clip table newest-first, claims batches of
/// pending clips, embeds them, and persists the vectors.
/// </summary>
public interface IEmbeddingWorker
{
    /// <summary>Pushes events whenever a batch has been successfully persisted, so UI/cache can react.</summary>
    IObservable<int> BatchCompleted { get; }

    /// <summary>Start the worker loop. No-op if already started.</summary>
    void Start();

    /// <summary>Stop the worker loop and wait for the current batch to finish.</summary>
    Task StopAsync();

    /// <summary>Wake the worker to immediately check for new work (e.g. after a clip was captured).</summary>
    void Poke();

    /// <summary>Query current coverage for UI display.</summary>
    Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default);

    /// <summary>Flag every clip embedding for re-embedding (used when the model version changes).</summary>
    Task RerunAllAsync(CancellationToken cancellationToken = default);
}
