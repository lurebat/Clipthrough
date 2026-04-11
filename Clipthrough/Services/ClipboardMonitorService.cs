using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly string[] HtmlFormats = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormats = ["Rich Text Format", "text/rtf", "public.rtf"];
    private static readonly string[] PngFormats = ["PNG", "image/png", "public.png"];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IClipStoreService _clipStoreService;
    private readonly WindowsSourceApplicationResolver _sourceApplicationResolver;
    private readonly IAppNotificationService _notificationService;
    private readonly Subject<ClipEntry> _capturedClips = new();

    private Window? _window;
    private bool _isStarted;
    private bool _isHookAttached;
    private bool _isDisposed;

    public ClipboardMonitorService(IClipStoreService clipStoreService, WindowsSourceApplicationResolver sourceApplicationResolver, IAppNotificationService notificationService)
    {
        _clipStoreService = clipStoreService;
        _sourceApplicationResolver = sourceApplicationResolver;
        _notificationService = notificationService;
    }

    public IObservable<ClipEntry> CapturedClips => _capturedClips.AsObservable();

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
        _capturedClips.OnCompleted();
        _capturedClips.Dispose();
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            HandleClipboardChanged();
        }

        return IntPtr.Zero;
    }

    private async void HandleClipboardChanged()
    {
        if (!_isStarted || _isDisposed || _window?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            using var clipboardData = await clipboard.TryGetDataAsync();
            if (clipboardData is null)
            {
                Trace.TraceInformation("Clipboard change ignored because no data transfer object was available.");
                return;
            }

            var availableFormats = DescribeFormats(clipboardData);
            Trace.TraceInformation($"Clipboard change detected. Formats: {availableFormats}");

            var captureRequest = await BuildCaptureRequestAsync(clipboardData).ConfigureAwait(false);
            if (captureRequest is null)
            {
                Trace.TraceInformation($"Clipboard change ignored because no supported payload was found. Formats: {availableFormats}");
                _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, AppText.ClipCaptureFailedUnsupportedPayload);
                return;
            }

            Trace.TraceInformation($"Clipboard capture selected {captureRequest.ContentType}/{captureRequest.ContentFormat} bytes={captureRequest.ContentBytes.Length} source={captureRequest.SourceApp ?? "Unknown"}");
            var capturedClip = await _clipStoreService.CaptureAsync(captureRequest).ConfigureAwait(false);
            if (capturedClip is not null)
            {
                _capturedClips.OnNext(capturedClip);
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
    }

    private async Task<ClipCaptureRequest?> BuildCaptureRequestAsync(IAsyncDataTransfer clipboardData)
    {
        var sourceInfo = _sourceApplicationResolver.TryResolve();
        var plainText = await clipboardData.TryGetTextAsync().ConfigureAwait(false);

        var files = await clipboardData.TryGetFilesAsync().ConfigureAwait(false);
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

        var bitmap = await clipboardData.TryGetBitmapAsync().ConfigureAwait(false);
        if (bitmap is not null)
        {
            return CreateImageRequest(bitmap, sourceInfo);
        }

        var pngBytes = await TryGetFirstBytesAsync(clipboardData, PngFormats).ConfigureAwait(false);
        if (pngBytes is { Length: > 0 })
        {
            Trace.TraceInformation($"Recovered bitmap clipboard payload from platform PNG bytes ({pngBytes.Length} bytes).");
            return CreateImageRequest(pngBytes, sourceInfo);
        }

        var rtf = await TryGetMarkupAsync(clipboardData, RtfFormats, ClipContentFormat.Rtf).ConfigureAwait(false);
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
                sourceInfo);
        }

        var html = await TryGetMarkupAsync(clipboardData, HtmlFormats, ClipContentFormat.Html).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(html))
        {
            var htmlFragment = ClipboardMarkupDecoder.ExtractHtmlFragment(html);
            var renderedText = !string.IsNullOrWhiteSpace(plainText)
                ? plainText
                : ClipDisplayFormatter.RenderRichContent(htmlFragment);
            return CreateRequest(
                renderedText,
                Encoding.UTF8.GetBytes(html),
                ContentType.RichText,
                ClipContentFormat.Html,
                sourceInfo);
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            return CreateRequest(
                plainText,
                Encoding.UTF8.GetBytes(plainText),
                ContentType.Text,
                ClipContentFormat.PlainText,
                sourceInfo);
        }

        if (filePaths.Length > 0)
        {
            return CreateFileRequest(filePaths, sourceInfo);
        }

        return null;
    }

    private static async Task<byte[]?> TryGetFirstBytesAsync(IAsyncDataTransfer clipboardData, string[] formatNames)
    {
        foreach (var formatName in formatNames)
        {
            var bytesValue = await clipboardData.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(formatName)).ConfigureAwait(false);
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
            var bytesValue = await clipboardData.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(formatName)).ConfigureAwait(false);
            if (bytesValue is { Length: > 0 })
            {
                var decodedFromBytes = ClipboardMarkupDecoder.DecodeMarkupBytes(bytesValue);
                if (!string.IsNullOrWhiteSpace(decodedFromBytes))
                {
                    Trace.TraceInformation($"Recovered {contentFormat} clipboard payload from {formatName} using raw bytes.");
                    return decodedFromBytes;
                }
            }

            var stringValue = await clipboardData.TryGetValueAsync(DataFormat.CreateStringPlatformFormat(formatName)).ConfigureAwait(false);
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

    private static string DescribeFormats(IAsyncDataTransfer clipboardData)
        => string.Join(", ", clipboardData.Formats.Select(static format => $"{format.Kind}:{format.Identifier}"));

    private static ClipCaptureRequest CreateImageRequest(Bitmap bitmap, ClipboardSourceApplicationInfo? sourceInfo)
    {
        using var bitmapStream = new MemoryStream();
        bitmap.Save(bitmapStream);
        return new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = null,
            ContentBytes = bitmapStream.ToArray(),
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
        };
    }

    private static ClipCaptureRequest? CreateImageRequest(byte[] imageBytes, ClipboardSourceApplicationInfo? sourceInfo)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            return new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentText = null,
                ContentBytes = imageBytes,
                ImageWidth = bitmap.PixelSize.Width,
                ImageHeight = bitmap.PixelSize.Height,
                SourceApp = sourceInfo?.Name,
                SourceAppPath = sourceInfo?.Path,
                SourceAppIconBytes = sourceInfo?.IconBytes,
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
        ClipboardSourceApplicationInfo? sourceInfo)
        => new()
        {
            ContentText = contentText,
            ContentBytes = contentBytes,
            ContentType = contentType,
            ContentFormat = contentFormat,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
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
