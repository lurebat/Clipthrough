using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class BackgroundOcrQueue : IBackgroundOcrQueue, IDisposable
{
    private readonly IClipStoreService _clipStoreService;
    private readonly IOcrService _ocrService;
    private readonly ISettingsService _settingsService;
    private Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<long, byte> _inflight = new();
    private readonly Subject<long> _completed = new();
    private readonly Subject<Unit> _queueChanged = new();
    private CancellationTokenSource _cts = new();
    private Task? _worker;
    private bool _started;
    private bool _disposed;

    public BackgroundOcrQueue(IClipStoreService clipStoreService, IOcrService ocrService, ISettingsService settingsService)
    {
        _clipStoreService = clipStoreService;
        _ocrService = ocrService;
        _settingsService = settingsService;
    }

    public IObservable<long> OcrCompleted => _completed.AsObservable();

    public IObservable<Unit> QueueChanged => _queueChanged.AsObservable();

    public void Start()
    {
        if (_disposed) return;
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            if (_worker is not null)
            {
                await _worker.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        _worker = null;
    }

    public void Enqueue(long clipId)
    {
        if (_disposed || clipId <= 0)
        {
            return;
        }
        if (!_inflight.TryAdd(clipId, 0))
        {
            return;
        }
        if (!_channel.Writer.TryWrite(clipId))
        {
            _inflight.TryRemove(clipId, out _);
            _queueChanged.OnNext(Unit.Default);
            return;
        }

        _queueChanged.OnNext(Unit.Default);
    }

    public async Task EnqueueBacklogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = await _clipStoreService.GetPendingOcrClipIdsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                Enqueue(id);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"OCR backlog enqueue failed: {ex}");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var clipId in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(clipId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"OCR worker failed for clip {clipId}: {ex}");
                }
                finally
                {
                    _inflight.TryRemove(clipId, out _);
                    _queueChanged.OnNext(Unit.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessAsync(long clipId, CancellationToken cancellationToken)
    {
        if (!_ocrService.IsAvailable)
        {
            return;
        }

        var claimed = await _clipStoreService.TryClaimForOcrAsync(clipId, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return;
        }

        ClipEntry? clip;
        try
        {
            clip = await _clipStoreService.GetByIdAsync(clipId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _clipStoreService.SetOcrFailureAsync(clipId, ex.Message, cancellationToken).ConfigureAwait(false);
            _completed.OnNext(clipId);
            return;
        }

        if (clip is null || clip.ContentType != ContentType.Image || clip.ContentBytes is null || clip.ContentBytes.Length == 0)
        {
            await _clipStoreService.SetOcrFailureAsync(clipId, "Clip no longer has image bytes", cancellationToken).ConfigureAwait(false);
            _completed.OnNext(clipId);
            return;
        }

        var languages = _settingsService.Current.OcrLanguages;
        try
        {
            var result = await _ocrService.ExtractTextAsync(clip.ContentBytes, languages, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                await _clipStoreService.SetOcrResultAsync(clipId, result.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _clipStoreService.SetOcrFailureAsync(clipId, result.Error ?? "OCR failed", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _clipStoreService.SetOcrFailureAsync(clipId, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        _completed.OnNext(clipId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _completed.Dispose();
        _queueChanged.Dispose();
    }
}
