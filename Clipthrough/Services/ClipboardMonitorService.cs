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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IClipStoreService _clipStoreService;
    private readonly ISourceApplicationResolver _sourceApplicationResolver;
    private readonly IAppNotificationService _notificationService;
    private readonly Subject<ClipEntry> _capturedClips = new();

    private Window? _window;
    private bool _isStarted;
    private bool _isHookAttached;
    private bool _isDisposed;
    private int _suppressCount;

    public ClipboardMonitorService(IClipStoreService clipStoreService, ISourceApplicationResolver sourceApplicationResolver, IAppNotificationService notificationService)
    {
        _clipStoreService = clipStoreService;
        _sourceApplicationResolver = sourceApplicationResolver;
        _notificationService = notificationService;
    }

    public IObservable<ClipEntry> CapturedClips => _capturedClips.AsObservable();

    public void SuppressNext() => Interlocked.Increment(ref _suppressCount);

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

        if (Interlocked.Exchange(ref _suppressCount, 0) > 0)
        {
            return;
        }

        try
        {
            var capturedClip = await CaptureClipboardChangeAsync(clipboard);
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

    private async Task<ClipEntry?> CaptureClipboardChangeAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var clipboardData = await clipboard.TryGetDataAsync();
                if (clipboardData is null)
                {
                    Trace.TraceInformation("Clipboard change ignored because no data transfer object was available.");
                    return null;
                }

                var availableFormats = DescribeFormats(clipboardData);
                Trace.TraceInformation($"Clipboard change detected. Formats: {availableFormats}");

                var captureRequest = await BuildCaptureRequestAsync(clipboardData);
                if (captureRequest is null)
                {
                    Trace.TraceInformation($"Clipboard change ignored because no supported payload was found. Formats: {availableFormats}");
                    _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, AppText.ClipCaptureFailedUnsupportedPayload);
                    return null;
                }

                Trace.TraceInformation($"Clipboard capture selected {captureRequest.ContentType}/{captureRequest.ContentFormat} bytes={captureRequest.ContentBytes.Length} source={captureRequest.SourceApp ?? "Unknown"}");
                return await _clipStoreService.CaptureAsync(captureRequest);
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

    private async Task<ClipCaptureRequest?> BuildCaptureRequestAsync(IAsyncDataTransfer clipboardData)
    {
        var sourceInfo = _sourceApplicationResolver.TryResolve();
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

        var bitmap = await clipboardData.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            return CreateImageRequest(bitmap, sourceInfo, GetRelatedImageLabel(filePaths, relatedSourceUrl, plainText, sourceInfo?.WindowTitle));
        }

        var pngBytes = await TryGetFirstBytesAsync(clipboardData, PngFormats);
        if (pngBytes is { Length: > 0 })
        {
            Trace.TraceInformation($"Recovered bitmap clipboard payload from platform PNG bytes ({pngBytes.Length} bytes).");
            return CreateImageRequest(pngBytes, sourceInfo, GetRelatedImageLabel(filePaths, relatedSourceUrl, plainText, sourceInfo?.WindowTitle));
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

    private static ClipCaptureRequest CreateImageRequest(Bitmap bitmap, ClipboardSourceApplicationInfo? sourceInfo, string? imageLabel)
    {
        using var bitmapStream = new MemoryStream();
        bitmap.Save(bitmapStream);
        return new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = imageLabel,
            ContentBytes = bitmapStream.ToArray(),
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
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
        if (TryGetFileLabel(relatedFilePaths.FirstOrDefault()) is { } fileLabel)
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
