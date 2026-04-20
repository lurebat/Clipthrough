using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;

namespace Clipthrough.Services.Search;

/// <summary>
/// Default <see cref="IEmbeddingWorker"/> implementation. Walks pending clips newest-first,
/// embeds them in batches, and writes the vectors back to storage.
///
/// Kept intentionally simple: one worker loop driven by a poke-able event, no cross-worker
/// coordination (single writer). Claim is transactional in <see cref="IClipStoreService"/> so
/// restarting mid-batch does not lose work.
/// </summary>
public sealed class EmbeddingWorker : IEmbeddingWorker, IDisposable
{
    /// <summary>Bumped when the embedding format or model changes so existing vectors get re-run on upgrade.</summary>
    public const string ModelVersion = "minilm-l6-v2-int8-v1";

    private const int BatchSize = 32;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly IClipStoreService _clipStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IBackgroundJobIndicator _jobIndicator;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Subject<int> _batchCompleted = new();
    private CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    public EmbeddingWorker(IClipStoreService clipStore, IEmbeddingService embeddingService, IBackgroundJobIndicator jobIndicator)
    {
        _clipStore = clipStore;
        _embeddingService = embeddingService;
        _jobIndicator = jobIndicator;
    }

    public IObservable<int> BatchCompleted => _batchCompleted.AsObservable();

    public void Start()
    {
        if (_disposed) return;
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        _cts.Cancel();
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _loop = null;
    }

    public void Poke()
    {
        if (_disposed) return;
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
    }

    public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
        => _clipStore.GetEmbeddingCoverageAsync(cancellationToken);

    public async Task RerunAllAsync(CancellationToken cancellationToken = default)
    {
        await _clipStore.MarkAllEmbeddingsForRerunAsync(cancellationToken).ConfigureAwait(false);
        Poke();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await ProcessOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Trace.TraceError($"Embedding worker error: {ex}");
                processed = 0;
            }

            if (processed > 0) continue;

            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(IdleDelay);
                await _wake.WaitAsync(idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return;
            }
        }
    }

    private async Task<int> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ClipEmbeddingCandidate> candidates;
        try
        {
            candidates = await _clipStore.ClaimPendingEmbeddingsAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceError($"Embedding claim failed: {ex}");
            return 0;
        }

        if (candidates.Count == 0) return 0;

        using var job = _jobIndicator.Begin($"Embedding {candidates.Count} clip{(candidates.Count == 1 ? "" : "s")}");

        IReadOnlyList<float[]> vectors;
        try
        {
            var texts = candidates.Select(c => c.TextToEmbed ?? string.Empty).ToArray();
            vectors = await _embeddingService.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceError($"Embedding inference failed for batch of {candidates.Count}: {ex}");
            foreach (var c in candidates)
            {
                try
                {
                    await _clipStore.SetEmbeddingFailureAsync(c.ClipId, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    Trace.TraceError($"Failed to flag embedding failure for clip {c.ClipId}: {inner}");
                }
            }
            return candidates.Count;
        }

        if (vectors.Count != candidates.Count)
        {
            Trace.TraceError($"Embedding returned {vectors.Count} vectors for {candidates.Count} inputs; skipping batch.");
            return candidates.Count;
        }

        var records = new List<ClipEmbeddingRecord>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            records.Add(new ClipEmbeddingRecord(candidates[i].ClipId, vectors[i]));
        }

        try
        {
            await _clipStore.SaveEmbeddingBatchAsync(records, ModelVersion, cancellationToken).ConfigureAwait(false);
            _batchCompleted.OnNext(records.Count);
            return records.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceError($"Embedding persist failed: {ex}");
            foreach (var c in candidates)
            {
                try
                {
                    await _clipStore.SetEmbeddingFailureAsync(c.ClipId, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    Trace.TraceError($"Failed to flag embedding persist failure for clip {c.ClipId}: {inner}");
                }
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _wake.Dispose();
        _batchCompleted.Dispose();
    }
}
