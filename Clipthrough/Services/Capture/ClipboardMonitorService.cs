using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Services;

public sealed class ClipboardMonitorService : IClipboardMonitorService, IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly TimeSpan[] ClipboardRetryDelays = [TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(140)];
    private static readonly string[] HtmlFormats = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormats = ["Rich Text Format", "text/rtf", "public.rtf"];
    private static readonly string[] PngFormats = ["PNG", "image/png", "public.png"];
    private static readonly string[] SourceUrlFormats = ["Chromium internal source URL", "UniformResourceLocatorW", "UniformResourceLocator"];
    private static readonly string[] ClipboardIgnoreFormats = ["Clipboard Viewer Ignore", "ExcludeClipboardContentFromMonitorProcessing"];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IClipStoreService _clipStoreService;
    private readonly ISourceApplicationResolver _sourceApplicationResolver;
    private readonly IAppNotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly Subject<ClipEntry> _capturedClips = new();
    private readonly Subject<ClipEntry> _updatedClips = new();
    private readonly BehaviorSubject<bool> _captureBusy = new(false);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly ClipboardSuppressionGate _suppressionGate = new();
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(60);

    private Window? _window;
    private bool _isStarted;
    private bool _isHookAttached;
    private bool _isDisposed;
    private DispatcherTimer? _coalesceTimer;
    private int _pendingCaptureRequests;

    public ClipboardMonitorService(IClipStoreService clipStoreService, ISourceApplicationResolver sourceApplicationResolver, IAppNotificationService notificationService, ISettingsService settingsService)
    {
        _clipStoreService = clipStoreService;
        _sourceApplicationResolver = sourceApplicationResolver;
        _notificationService = notificationService;
        _settingsService = settingsService;
    }

    public IObservable<ClipEntry> CapturedClips => _capturedClips.AsObservable();

    public IObservable<ClipEntry> UpdatedClips => _updatedClips.AsObservable();

    public IObservable<bool> CaptureBusy => _captureBusy.AsObservable();

    public void SuppressNext() => _suppressionGate.Arm();

    public bool IsRunning => _isStarted;

    public void Start()
    {
        if (_isDisposed || _isStarted || !OperatingSystem.IsWindows())
        {
            return;
        }

        _isStarted = true;

        if (Dispatcher.UIThread.CheckAccess())
        {
            AttachToMainWindow();
            return;
        }

        Dispatcher.UIThread.Post(AttachToMainWindow);
    }

    public void Stop()
    {
        if (!_isStarted && _window is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            StopCore();
            return;
        }

        Dispatcher.UIThread.Post(StopCore);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();

        // Completing the subjects drops every subscriber, which is all the
        // teardown they hold. Disposing them as well would make a later OnNext
        // throw ObjectDisposedException - and the only writers left are an
        // in-flight `async void` capture and its fire-and-forget enrichment,
        // whose continuations run after this method returns and have no catch
        // above them. The busy subject in particular is written from an
        // unguarded `finally`, so that throw would go straight to the
        // dispatcher's unhandled-exception path.
        //
        // The capture gate is left alone for the same reason: its Release()
        // runs in that continuation's finally, and SemaphoreSlim only owns a
        // disposable resource once AvailableWaitHandle has been read, which
        // this class never does.
        //
        // Blocking here until the capture finishes is not an alternative -
        // Dispose runs on the UI thread and those continuations need that same
        // thread, so waiting deadlocks instead of racing.
        _capturedClips.OnCompleted();
        _updatedClips.OnCompleted();
        _captureBusy.OnCompleted();
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // Coalesce: multiple WM_CLIPBOARDUPDATE messages typically fire
            // for a single user copy (one per format the source app sets).
            // Restart a short timer; only when it elapses do we actually
            // read the clipboard. This collapses 3-N events into one full
            // COM read + DB write, cutting capture latency by ~2-3x.
            Interlocked.Increment(ref _pendingCaptureRequests);
            RestartCoalesceTimer();
        }

        return IntPtr.Zero;
    }

    private void RestartCoalesceTimer()
    {
        if (_coalesceTimer is null)
        {
            _coalesceTimer = new DispatcherTimer { Interval = CoalesceDelay };
            _coalesceTimer.Tick += OnCoalesceTick;
        }
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }

    private void OnCoalesceTick(object? sender, EventArgs e)
    {
        _coalesceTimer?.Stop();
        if (Interlocked.Exchange(ref _pendingCaptureRequests, 0) == 0)
        {
            return;
        }
        HandleClipboardChanged();
    }

    private async void HandleClipboardChanged()
    {
        if (!_isStarted || _isDisposed || _window?.Clipboard is not { } clipboard)
        {
            return;
        }

        if (_suppressionGate.ShouldSuppress())
        {
            return;
        }

        var publishedBusy = false;
        try
        {
            await _captureGate.WaitAsync();
            try
            {
                _captureBusy.OnNext(true);
                publishedBusy = true;

                // Read clipboard data on the UI thread (COM requirement),
                // but persist to DB on a background thread to avoid blocking.
                var captureStopwatch = Stopwatch.StartNew();
                var captureResult = await BuildCaptureRequestFromClipboardAsync(clipboard);
                if (captureResult is null)
                {
                    return;
                }

                var (captureRequest, deferredContent) = captureResult.Value;
                if (!_isStarted || _isDisposed)
                {
                    // The monitor was stopped (e.g. a database maintenance op began)
                    // while we were reading the clipboard. Skip the write so we don't
                    // persist into a database that's being swapped/rekeyed out from
                    // under us — that write would be lost or hit a half-swapped file.
                    return;
                }
                var capturedClip = await Task.Run(() => _clipStoreService.CaptureFastAsync(captureRequest));
                if (capturedClip is not null)
                {
                    Trace.TraceInformation($"Clipboard fast capture completed in {captureStopwatch.ElapsedMilliseconds} ms for clip {capturedClip.Id}.");
                    _capturedClips.OnNext(capturedClip);
                    _ = EnrichCapturedClipAsync(capturedClip, deferredContent);
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceError($"Clipboard capture failed: {ex}");
            _notificationService.PublishError(AppText.ClipCaptureFailedTitle, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            Trace.TraceError($"Clipboard capture failed: {ex}");
            _notificationService.PublishError(AppText.ClipCaptureFailedTitle, ex.Message);
        }
        catch (COMException ex)
        {
            Trace.TraceWarning($"Clipboard snapshot skipped because the platform data object could not be enumerated (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
            _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, AppText.FormatClipCaptureFailedComSnapshot(ex.HResult));
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress — not an error.
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Clipboard capture failed unexpectedly: {ex}");
            _notificationService.PublishError(AppText.ClipCaptureFailedTitle, ex.Message);
        }
        finally
        {
            if (publishedBusy)
            {
                _captureBusy.OnNext(false);
            }

            // If another WM_CLIPBOARDUPDATE arrived during this capture (e.g.
            // user copied again while we were reading an image), schedule a
            // follow-up rather than dropping it.
            if (Volatile.Read(ref _pendingCaptureRequests) > 0 && !_isDisposed && _isStarted)
            {
                Dispatcher.UIThread.Post(RestartCoalesceTimer);
            }
        }
    }

    private async Task<(ClipCaptureRequest Primary, ClipCaptureRequest? Deferred)?> BuildCaptureRequestFromClipboardAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Resolve the owning application before touching the payload.
                // For an excluded app (a password manager, say) the secret must
                // never enter this process at all, so the check has to happen
                // ahead of the clipboard read rather than at persist time.
                var sourceInfo = ResolveCaptureSource(out var isExcluded);
                if (isExcluded)
                {
                    Trace.TraceInformation($"Clipboard change ignored because the source application '{sourceInfo?.Name ?? sourceInfo?.Path}' is on the capture exclusion list.");
                    return null;
                }

                using var clipboardData = await clipboard.TryGetDataAsync();
                if (clipboardData is null)
                {
                    // Source app may not have finished publishing the data
                    // yet. Retry a couple of times before giving up.
                    if (attempt < ClipboardRetryDelays.Length)
                    {
                        Trace.TraceInformation($"Clipboard data transfer not yet available (attempt {attempt + 1}); retrying.");
                        await Task.Delay(ClipboardRetryDelays[attempt]);
                        continue;
                    }

                    Trace.TraceInformation("Clipboard change ignored because no data transfer object was available.");
                    return null;
                }

                var availableFormats = DescribeFormats(clipboardData);
                Trace.TraceInformation($"Clipboard change detected. Formats: {availableFormats}");

                if (ShouldIgnoreClipboard(clipboardData))
                {
                    Trace.TraceInformation($"Clipboard change ignored because the source app set a clipboard-viewer-ignore format. Formats: {availableFormats}");
                    return null;
                }

                var captureRequest = await BuildCaptureRequestAsync(clipboardData, sourceInfo);
                if (captureRequest is null)
                {
                    Trace.TraceInformation($"Clipboard change ignored because no supported payload was found. Formats: {availableFormats}");
                    return null;
                }

                // Read deferred HTML/RTF in the same clipboard snapshot when
                // the primary capture is plain text. Avoids re-opening the
                // clipboard in EnrichCapturedClipAsync, which used to double
                // the COM round-trip cost and significantly slowed capture
                // of rich text (e.g. copies from VS Code or browsers).
                ClipCaptureRequest? deferred = null;
                if (captureRequest.ContentType == ContentType.Text && captureRequest.ContentFormat == ClipContentFormat.PlainText)
                {
                    deferred = await BuildDeferredContentFromSnapshotAsync(clipboardData, captureRequest);
                }

                Trace.TraceInformation($"Clipboard capture selected {captureRequest.ContentType}/{captureRequest.ContentFormat} bytes={captureRequest.ContentBytes.Length} source={captureRequest.SourceApp ?? "Unknown"} deferred={deferred?.ContentFormat.ToString() ?? "none"}");
                return (captureRequest, deferred);
            }
            catch (COMException ex) when (attempt < ClipboardRetryDelays.Length)
            {
                Trace.TraceInformation($"Clipboard read retry {attempt + 1}/{ClipboardRetryDelays.Length + 1} after COM failure 0x{ex.HResult:X8}: {ex.Message}");
                await Task.Delay(ClipboardRetryDelays[attempt]);
            }
            catch (InvalidOperationException ex) when (attempt < ClipboardRetryDelays.Length && IsTransientClipboardFailure(ex))
            {
                Trace.TraceInformation($"Clipboard read retry {attempt + 1}/{ClipboardRetryDelays.Length + 1} after transient failure: {ex.Message}");
                await Task.Delay(ClipboardRetryDelays[attempt]);
            }
        }
    }

    /// <summary>
    /// Resolves the application that owns the current clipboard contents and
    /// reports whether the user has excluded it from capture.
    /// </summary>
    /// <remarks>
    /// Internal rather than inlined so the wiring between the saved exclusion
    /// list and <see cref="CaptureExclusionPolicy"/> can be tested without a
    /// live clipboard: everything that reaches it needs a real window.
    /// </remarks>
    internal ClipboardSourceApplicationInfo? ResolveCaptureSource(out bool isExcluded)
    {
        var sourceInfo = _sourceApplicationResolver.TryResolve(includeIcon: false);
        isExcluded = CaptureExclusionPolicy.IsExcluded(sourceInfo, _settingsService.Current.ExcludedCaptureApps);
        return sourceInfo;
    }

    private async Task<ClipCaptureRequest?> BuildCaptureRequestAsync(IAsyncDataTransfer clipboardData, ClipboardSourceApplicationInfo? sourceInfo)
    {
        var plainText = await clipboardData.TryGetTextAsync();
        var relatedSourceUrl = await TryGetPlatformStringAsync(clipboardData, SourceUrlFormats);

        var files = await clipboardData.TryGetFilesAsync();
        var filePaths = Array.Empty<string>();
        if (files is { Length: > 0 })
        {
            filePaths = files
                .Select(static file => file.TryGetLocalPath())
                .OfType<string>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (filePaths.Length > 0 && !ShouldPreferImageContent(filePaths, sourceInfo))
            {
                return CreateFileRequest(filePaths, sourceInfo);
            }
        }

        // Avalonia's Bitmap owns a native surface. Left undisposed, every image copy
        // leaks one full-resolution surface for the life of the process - the vendored
        // ShareX clipboard handler disposes its own for the same reason.
        using var bitmap = await clipboardData.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            return await CreateImageRequestAsync(bitmap, sourceInfo, GetRelatedImageLabel(filePaths, relatedSourceUrl, plainText, sourceInfo?.WindowTitle));
        }

        var pngBytes = await TryGetFirstBytesAsync(clipboardData, PngFormats);
        if (pngBytes is { Length: > 0 })
        {
            Trace.TraceInformation($"Recovered bitmap clipboard payload from platform PNG bytes ({pngBytes.Length} bytes).");
            return CreateImageRequest(pngBytes, sourceInfo, GetRelatedImageLabel(filePaths, relatedSourceUrl, plainText, sourceInfo?.WindowTitle));
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            return CreateRequest(
                plainText,
                Encoding.UTF8.GetBytes(plainText),
                ContentType.Text,
                ClipContentFormat.PlainText,
                sourceInfo,
                relatedSourceUrl);
        }

        var html = await TryGetMarkupAsync(clipboardData, HtmlFormats, ClipContentFormat.Html);
        if (!string.IsNullOrWhiteSpace(html))
        {
            var normalizedHtml = ClipboardMarkupDecoder.NormalizePlatformMarkupString(html, ClipContentFormat.Html);
            var htmlFragment = ClipboardMarkupDecoder.ExtractHtmlFragment(normalizedHtml);
            var renderedText = !string.IsNullOrWhiteSpace(plainText)
                ? plainText
                : ClipDisplayFormatter.RenderRichContent(htmlFragment);
            return CreateRequest(
                renderedText,
                Encoding.UTF8.GetBytes(normalizedHtml),
                ContentType.RichText,
                ClipContentFormat.Html,
                sourceInfo,
                relatedSourceUrl);
        }

        var rtf = await TryGetMarkupAsync(clipboardData, RtfFormats, ClipContentFormat.Rtf);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            var renderedText = !string.IsNullOrWhiteSpace(plainText)
                ? plainText
                : ClipDisplayFormatter.RenderRichContent(rtf);
            return CreateRequest(
                renderedText,
                Encoding.UTF8.GetBytes(rtf),
                ContentType.RichText,
                ClipContentFormat.Rtf,
                sourceInfo,
                relatedSourceUrl);
        }

        if (filePaths.Length > 0)
        {
            return CreateFileRequest(filePaths, sourceInfo);
        }

        return null;
    }

    private async Task EnrichCapturedClipAsync(ClipEntry capturedClip, ClipCaptureRequest? deferredContent)
    {
        try
        {
            var enrichmentStopwatch = Stopwatch.StartNew();
            if (deferredContent is not null)
            {
                if (_isDisposed)
                {
                    return;
                }

                var updated = await Task.Run(() => _clipStoreService.UpdateDeferredContentAsync(capturedClip.Id, deferredContent));
                PublishUpdatedClip(updated);
            }

            if (_isDisposed)
            {
                return;
            }

            var sensitivityUpdated = await Task.Run(() => _clipStoreService.ApplySensitivityAsync(capturedClip.Id));
            PublishUpdatedClip(sensitivityUpdated);

            var iconBytes = await Task.Run(() => _sourceApplicationResolver.TryResolveIcon(capturedClip.SourceAppPath));
            if (iconBytes is { Length: > 0 } && !_isDisposed)
            {
                var iconUpdated = await Task.Run(() => _clipStoreService.UpdateSourceAppIconAsync(capturedClip.Id, iconBytes));
                PublishUpdatedClip(iconUpdated);
            }

            if (_isDisposed)
            {
                return;
            }

            await Task.Run(() => _clipStoreService.ApplyMaintenanceAsync());
            Trace.TraceInformation($"Clipboard background enrichment completed in {enrichmentStopwatch.ElapsedMilliseconds} ms for clip {capturedClip.Id}.");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — suppress.
        }
        catch (Exception ex)
        {
            // The deferred scan is what makes the clip's content safe to show
            // and to index. Losing it silently is a security-relevant failure,
            // not noise — surface it. sensitivity_scanned_at stays null, so
            // ApplyPendingSensitivityAsync retries this clip on next startup.
            Trace.TraceError($"Clipboard background enrichment failed for clip {capturedClip.Id}; sensitivity classification is deferred to the next startup: {ex}");
        }
    }

    private async Task<ClipCaptureRequest?> BuildDeferredContentFromSnapshotAsync(IAsyncDataTransfer clipboardData, ClipCaptureRequest primary)
    {
        // Reuse the already-open clipboard snapshot to pull HTML/RTF in the
        // same capture pass instead of re-opening the clipboard inside
        // EnrichCapturedClipAsync. Source-url + source-app info already live
        // on the primary request.
        var sourceInfo = new ClipboardSourceApplicationInfo(
            primary.SourceApp,
            primary.SourceAppPath,
            null,
            primary.SourceWindowTitle);

        var html = await TryGetMarkupAsync(clipboardData, HtmlFormats, ClipContentFormat.Html);
        if (!string.IsNullOrWhiteSpace(html))
        {
            var normalizedHtml = ClipboardMarkupDecoder.NormalizePlatformMarkupString(html, ClipContentFormat.Html);
            var htmlFragment = ClipboardMarkupDecoder.ExtractHtmlFragment(normalizedHtml);
            var renderedText = !string.IsNullOrWhiteSpace(primary.ContentText)
                ? primary.ContentText
                : ClipDisplayFormatter.RenderRichContent(htmlFragment);
            return CreateRequest(
                renderedText,
                Encoding.UTF8.GetBytes(normalizedHtml),
                ContentType.RichText,
                ClipContentFormat.Html,
                sourceInfo,
                primary.SourceUrl);
        }

        var rtf = await TryGetMarkupAsync(clipboardData, RtfFormats, ClipContentFormat.Rtf);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            var renderedText = !string.IsNullOrWhiteSpace(primary.ContentText)
                ? primary.ContentText
                : ClipDisplayFormatter.RenderRichContent(rtf);
            return CreateRequest(
                renderedText,
                Encoding.UTF8.GetBytes(rtf),
                ContentType.RichText,
                ClipContentFormat.Rtf,
                sourceInfo,
                primary.SourceUrl);
        }

        return null;
    }

    private void PublishUpdatedClip(ClipEntry? clip)
    {
        if (clip is not null)
        {
            _updatedClips.OnNext(clip);
        }
    }

    private static async Task<byte[]?> TryGetFirstBytesAsync(IAsyncDataTransfer clipboardData, string[] formatNames)
    {
        foreach (var formatName in formatNames)
        {
            var bytesValue = await clipboardData.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(formatName));
            if (bytesValue is { Length: > 0 })
            {
                return bytesValue;
            }
        }

        return null;
    }

    private static async Task<string?> TryGetMarkupAsync(IAsyncDataTransfer clipboardData, string[] formatNames, ClipContentFormat contentFormat)
    {
        foreach (var formatName in formatNames)
        {
            var bytesValue = await clipboardData.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(formatName));
            if (bytesValue is { Length: > 0 })
            {
                var decodedFromBytes = ClipboardMarkupDecoder.DecodeMarkupBytes(bytesValue);
                if (!string.IsNullOrWhiteSpace(decodedFromBytes))
                {
                    Trace.TraceInformation($"Recovered {contentFormat} clipboard payload from {formatName} using raw bytes.");
                    return decodedFromBytes;
                }
            }

            var stringValue = await clipboardData.TryGetValueAsync(DataFormat.CreateStringPlatformFormat(formatName));
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                var normalizedValue = ClipboardMarkupDecoder.NormalizePlatformMarkupString(stringValue, contentFormat);
                if (!string.Equals(normalizedValue, stringValue, StringComparison.Ordinal))
                {
                    Trace.TraceInformation($"Recovered {contentFormat} clipboard payload from {formatName} using string normalization.");
                }

                return normalizedValue;
            }
        }

        return null;
    }

    private static async Task<string?> TryGetPlatformStringAsync(IAsyncDataTransfer clipboardData, string[] formatNames)
    {
        foreach (var formatName in formatNames)
        {
            var stringValue = await clipboardData.TryGetValueAsync(DataFormat.CreateStringPlatformFormat(formatName));
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }

            var bytesValue = await clipboardData.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(formatName));
            if (bytesValue is { Length: > 0 })
            {
                var decodedValue = Encoding.UTF8.GetString(bytesValue).Trim('\0', ' ', '\r', '\n', '\t');
                if (!string.IsNullOrWhiteSpace(decodedValue))
                {
                    return decodedValue;
                }
            }
        }

        return null;
    }

    private static string DescribeFormats(IAsyncDataTransfer clipboardData)
        => string.Join(", ", clipboardData.Formats.Select(static format => $"{format.Kind}:{format.Identifier}"));

    private static bool ShouldIgnoreClipboard(IAsyncDataTransfer clipboardData)
    {
        var formatIds = clipboardData.Formats.Select(static f => f.Identifier).ToArray();
        return ClipboardIgnoreFormats.Any(ignore => formatIds.Any(id => string.Equals(id, ignore, StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<ClipCaptureRequest> CreateImageRequestAsync(Bitmap bitmap, ClipboardSourceApplicationInfo? sourceInfo, string? imageLabel)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var bytes = await Task.Run(() =>
        {
            using var bitmapStream = new MemoryStream();
            bitmap.Save(bitmapStream, PngBitmapEncoderOptions.Default);
            return bitmapStream.ToArray();
        }).ConfigureAwait(false);
        return new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = imageLabel,
            ContentBytes = bytes,
            ImageWidth = width,
            ImageHeight = height,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
            SourceWindowTitle = sourceInfo?.WindowTitle,
        };
    }

    private static ClipCaptureRequest? CreateImageRequest(byte[] imageBytes, ClipboardSourceApplicationInfo? sourceInfo, string? imageLabel)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            return new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentText = imageLabel,
                ContentBytes = imageBytes,
                ImageWidth = bitmap.PixelSize.Width,
                ImageHeight = bitmap.PixelSize.Height,
                SourceApp = sourceInfo?.Name,
                SourceAppPath = sourceInfo?.Path,
                SourceAppIconBytes = sourceInfo?.IconBytes,
                SourceWindowTitle = sourceInfo?.WindowTitle,
            };
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"PNG clipboard payload decode failed: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"PNG clipboard payload decode failed: {ex.Message}");
            return null;
        }
    }

    private static ClipCaptureRequest CreateRequest(
        string contentText,
        byte[] contentBytes,
        ContentType contentType,
        ClipContentFormat contentFormat,
        ClipboardSourceApplicationInfo? sourceInfo,
        string? sourceUrl = null)
        => new()
        {
            ContentText = contentText,
            ContentBytes = contentBytes,
            ContentType = contentType,
            ContentFormat = contentFormat,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
            SourceWindowTitle = sourceInfo?.WindowTitle,
            SourceUrl = sourceUrl,
        };

    private static ClipCaptureRequest CreateFileRequest(string[] paths, ClipboardSourceApplicationInfo? sourceInfo)
    {
        var content = string.Join(Environment.NewLine, paths);
        return CreateRequest(
            content,
            Encoding.UTF8.GetBytes(content),
            ContentType.Files,
            ClipContentFormat.FileList,
            sourceInfo);
    }

    private static bool ShouldPreferImageContent(string[] filePaths, ClipboardSourceApplicationInfo? sourceInfo)
    {
        if (filePaths.Length == 0 || sourceInfo?.Name is not { Length: > 0 } sourceAppName)
        {
            return false;
        }

        if (!sourceAppName.Contains("Photos", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filePaths.Any(static path => Path.GetExtension(path) is { Length: > 0 } extension && IsImageExtension(extension));
    }

    private static bool IsImageExtension(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase);

    private static string? GetRelatedImageLabel(IReadOnlyList<string> relatedFilePaths, string? sourceUrl, string? plainText, string? windowTitle)
    {
        if (TryGetFileLabel(relatedFilePaths.Count > 0 ? relatedFilePaths[0] : null) is { } fileLabel)
        {
            return fileLabel;
        }

        if (TryGetUrlLabel(sourceUrl) is { } urlLabel)
        {
            return urlLabel;
        }

        if (TryGetFileLabel(plainText) is { } plainTextLabel)
        {
            return plainTextLabel;
        }

        // Use the source window title as a last-resort label
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            return windowTitle.Trim();
        }

        return null;
    }

    private static string? TryGetFileLabel(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim().Trim('"');
        var fileName = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(trimmed);
        }

        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static string? TryGetUrlLabel(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(lastSegment);
        return string.IsNullOrWhiteSpace(fileName) ? WebUtility.UrlDecode(lastSegment) : WebUtility.UrlDecode(fileName);
    }

    private static bool IsTransientClipboardFailure(InvalidOperationException exception)
        => exception.Message.Contains("clipboard", StringComparison.OrdinalIgnoreCase);

    private void AttachToMainWindow()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        var mainWindow = ResolveMainWindow();
        if (mainWindow is null)
        {
            return;
        }

        _window = mainWindow;
        AttachHook();
    }

    private void StopCore()
    {
        _isStarted = false;
        if (_coalesceTimer is not null)
        {
            _coalesceTimer.Stop();
            _coalesceTimer.Tick -= OnCoalesceTick;
            _coalesceTimer = null;
        }
        Interlocked.Exchange(ref _pendingCaptureRequests, 0);
        DetachWindow();
    }

    private void DetachWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_isHookAttached)
        {
            try
            {
                var hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(hwnd);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Clipboard listener removal failed: {ex.Message}");
            }

            try
            {
                Win32Properties.RemoveWndProcHookCallback(_window, WndProcHook);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Clipboard hook removal failed: {ex.Message}");
            }

            _isHookAttached = false;
        }

        _window = null;
    }

    private void AttachHook()
    {
        if (_window is null || _isHookAttached)
        {
            return;
        }

        try
        {
            Win32Properties.AddWndProcHookCallback(_window, WndProcHook);

            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                _ = AddClipboardFormatListener(hwnd);
            }

            _isHookAttached = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Clipboard listener attachment failed: {ex.Message}");
        }
    }

    private Window? ResolveMainWindow()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }
            ? mainWindow
            : null;
}
