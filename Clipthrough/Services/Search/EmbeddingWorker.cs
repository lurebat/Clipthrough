using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Subject<int> _batchCompleted = new();
    private readonly Subject<IReadOnlyList<ClipEmbeddingRecord>> _batchRecordsCompleted = new();
    private CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    // Set to true the first time a FileNotFoundException is thrown from EmbedBatchAsync so the
    // worker stops hammering the DB while the ONNX model file is absent.
    private volatile bool _modelMissing;

    // Set whenever clips may be stranded in the 'processing' embedding state: at
    // every start, and again if releasing an unattempted batch fails. The loop
    // clears it only once the sweep actually succeeds, so a transient SQLite busy
    // can't strand those clips for the rest of the run.
    private volatile bool _claimsResetPending = true;

    public EmbeddingWorker(IClipStoreService clipStore, IEmbeddingService embeddingService, IBackgroundJobIndicator jobIndicator)
    {
        _clipStore = clipStore;
        _embeddingService = embeddingService;
    }

    public IObservable<int> BatchCompleted => _batchCompleted.AsObservable();

    public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted => _batchRecordsCompleted.AsObservable();

    public bool IsRunning => _started;

    public void Start()
    {
        if (_disposed) return;
        if (_started) return;
        _started = true;
        _claimsResetPending = true;
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
        // Clear the model-missing flag so a rerun after the model is placed can succeed.
        _modelMissing = false;
        await _clipStore.MarkAllEmbeddingsForRerunAsync(cancellationToken).ConfigureAwait(false);
        Poke();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Nothing this worker claimed is in flight at the top of an iteration,
            // so any row still marked 'processing' was stranded — by a stop, by a
            // crash, or by a release that failed. ClaimPendingEmbeddingsAsync never
            // re-selects 'processing', so without this sweep those clips would never
            // be embedded again while still counting as pending, leaving a coverage
            // figure that can never reach 100%. Retried until it succeeds rather
            // than attempted once, so a transient SQLite busy doesn't reinstate
            // exactly the bug this is here to prevent. The retry can't hot-loop:
            // an iteration that finds no work idles first.
            if (_claimsResetPending)
            {
                try
                {
                    var reset = await _clipStore.ResetStalledEmbeddingClaimsAsync(cancellationToken).ConfigureAwait(false);
                    _claimsResetPending = false;
                    if (reset > 0)
                    {
                        Trace.TraceInformation($"Reset {reset} stalled embedding claim(s) back to pending.");
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Trace.TraceError($"Resetting stalled embedding claims failed (will retry): {ex}");
                }
            }

            // Guard: if the ONNX model is missing, idle until poked/stopped, then
            // clear the flag and retry. The model may have been placed during the
            // idle window (or a Poke woke us); if it is still absent ProcessOnceAsync
            // re-sets the flag on the next FileNotFoundException. Without this reset
            // the worker would idle forever once the model was ever missing. (#14)
            if (_modelMissing)
            {
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
                _modelMissing = false;
                continue;
            }

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

            // A model-missing pass already returns 0. Falling into the idle below
            // would wait 30s here and another 30s in the model-missing branch at the
            // top of the next iteration — and, worse, would swallow the Poke that
            // says "the model is in place now", delaying the retry by a further
            // idle period. Let the model-missing branch own that wait.
            if (_modelMissing) continue;

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

        IReadOnlyList<float[]> vectors;
        try
        {
            var texts = candidates.Select(c => c.TextToEmbed ?? string.Empty).ToArray();
            vectors = await _embeddingService.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException ex)
        {
            // ONNX model file absent — stop spinning; wait for a Poke/restart.
            Trace.TraceError($"Embedding model file not found — worker will idle until restarted: {ex}");
            _modelMissing = true;

            // Don't mark the candidates as 'failed'; they'll be retried after the
            // model is placed. That only works if the claim is released: the batch
            // is currently 'processing', which the claim query never re-selects, so
            // leaving it would strand exactly the clips we promised to retry — and
            // each idle cycle would strand another batch.
            try
            {
                await _clipStore.ReleaseEmbeddingClaimsAsync(
                    candidates.Select(c => c.ClipId).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception releaseEx)
            {
                // Leave the sweep armed so the next loop iteration retries; otherwise
                // this batch stays stranded for the rest of the run.
                _claimsResetPending = true;
                Trace.TraceError($"Releasing the unattempted embedding batch failed: {releaseEx}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Embedding inference failed for batch of {candidates.Count}: {ex}");
            await FlagBatchFailedAsync(candidates, ex.Message, cancellationToken).ConfigureAwait(false);
            // Return 0 so RunAsync idles (30s back-off) instead of immediately re-claiming
            // the same failed clips in a tight CPU-pinning loop. (#4)
            return 0;
        }

        if (vectors.Count != candidates.Count)
        {
            Trace.TraceError($"Embedding returned {vectors.Count} vectors for {candidates.Count} inputs; failing batch.");
            // Count mismatch is an inference anomaly. Flag the claimed clips as
            // failed (bounded retry) rather than leaving them stuck in
            // 'processing' — the claim query never re-offers 'processing' rows,
            // so they would be orphaned until the next app restart. (#13)
            await FlagBatchFailedAsync(candidates, "embedding vector count mismatch", cancellationToken).ConfigureAwait(false);
            return 0;
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
            _batchRecordsCompleted.OnNext(records);
            return records.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceError($"Embedding persist failed: {ex}");
            await FlagBatchFailedAsync(candidates, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    // Flags every clip in a failed batch via SetEmbeddingFailureAsync (which
    // increments the bounded attempt counter), releasing the claimed 'processing'
    // rows instead of orphaning them until the next restart.
    private async Task FlagBatchFailedAsync(IReadOnlyList<ClipEmbeddingCandidate> candidates, string reason, CancellationToken cancellationToken)
    {
        foreach (var c in candidates)
        {
            try
            {
                await _clipStore.SetEmbeddingFailureAsync(c.ClipId, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                Trace.TraceError($"Failed to flag embedding failure for clip {c.ClipId}: {inner}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _wake.Dispose();
        _batchCompleted.Dispose();
        _batchRecordsCompleted.Dispose();
    }
}
