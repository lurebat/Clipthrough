using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services.Search;

/// <summary>
/// Connects clip capture to the embedding worker, and the worker's finished
/// batches to the in-memory semantic cache.
///
/// Neither link is optional: without the first, newly captured clips wait for
/// the worker's idle poll instead of being embedded promptly; without the
/// second, the cache only ever grows at a full <see cref="ISemanticSearchService.RefreshCacheAsync"/>,
/// so semantic search cannot find anything captured since the last reload.
///
/// This exists as a service rather than as subscriptions in <c>App</c> because
/// wiring that lives only in the application object is wiring that no test can
/// construct: the whole suite ran against a pipeline whose two halves were
/// never connected, and would have gone on passing had either been deleted.
/// </summary>
public interface ISemanticIndexCoordinator : IDisposable
{
    /// <summary>Connect the pipeline. Idempotent; a second call does nothing.</summary>
    void Start();
}

/// <inheritdoc cref="ISemanticIndexCoordinator"/>
public sealed class SemanticIndexCoordinator : ISemanticIndexCoordinator
{
    private readonly IClipboardMonitorService _clipboardMonitor;
    private readonly IEmbeddingWorker _embeddingWorker;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly object _gate = new();

    private IDisposable? _captureSubscription;
    private IDisposable? _batchSubscription;
    private bool _isDisposed;

    public SemanticIndexCoordinator(
        IClipboardMonitorService clipboardMonitor,
        IEmbeddingWorker embeddingWorker,
        ISemanticSearchService semanticSearch)
    {
        _clipboardMonitor = clipboardMonitor;
        _embeddingWorker = embeddingWorker;
        _semanticSearch = semanticSearch;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_isDisposed || _captureSubscription is not null)
            {
                return;
            }

            _captureSubscription = Observable
                .Merge(_clipboardMonitor.CapturedClips, _clipboardMonitor.UpdatedClips)
                .Subscribe(_ => _embeddingWorker.Poke());

            _batchSubscription = _embeddingWorker.BatchRecordsCompleted
                .Subscribe(records => _ = AppendAsync(records));
        }
    }

    // The append is fire-and-forget by nature - the worker must not be held up
    // waiting on the cache - so its faults have nowhere to surface. An
    // unobserved failure here degrades search silently, which is precisely the
    // failure mode this class exists to make impossible, so it is traced.
    private async Task AppendAsync(IReadOnlyList<ClipEmbeddingRecord> records)
    {
        try
        {
            await _semanticSearch.AppendEmbeddingsAsync(records);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Appending {records.Count} embedding(s) to the semantic cache failed: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _isDisposed = true;
            _captureSubscription?.Dispose();
            _captureSubscription = null;
            _batchSubscription?.Dispose();
            _batchSubscription = null;
        }
    }
}
