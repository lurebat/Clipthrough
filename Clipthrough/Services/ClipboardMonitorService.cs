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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Services;

public sealed class ClipboardMonitorService : IClipboardMonitorService, IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly string[] HtmlFormats = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormats = ["Rich Text Format", "text/rtf", "public.rtf"];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IClipStoreService _clipStoreService;
    private readonly WindowsSourceApplicationResolver _sourceApplicationResolver;
    private readonly Subject<ClipEntry> _capturedClips = new();

    private Window? _window;
    private bool _isStarted;
    private bool _isHookAttached;
    private bool _isDisposed;

    public ClipboardMonitorService(IClipStoreService clipStoreService, WindowsSourceApplicationResolver sourceApplicationResolver)
    {
        _clipStoreService = clipStoreService;
        _sourceApplicationResolver = sourceApplicationResolver;
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
                return;
            }

            var captureRequest = await BuildCaptureRequestAsync(clipboardData).ConfigureAwait(false);
            if (captureRequest is null)
            {
                return;
            }

            var capturedClip = await _clipStoreService.CaptureAsync(captureRequest).ConfigureAwait(false);
            if (capturedClip is not null)
            {
                _capturedClips.OnNext(capturedClip);
            }
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceError($"Clipboard capture failed: {ex}");
        }
        catch (NotSupportedException ex)
        {
            Trace.TraceError($"Clipboard capture failed: {ex}");
        }
    }

    private async Task<ClipCaptureRequest?> BuildCaptureRequestAsync(IAsyncDataTransfer clipboardData)
    {
        var sourceInfo = _sourceApplicationResolver.TryResolve();

        var files = await clipboardData.TryGetFilesAsync().ConfigureAwait(false);
        if (files is { Length: > 0 })
        {
            var paths = files
                .Select(static file => file.TryGetLocalPath())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray();

            if (paths.Length > 0)
            {
                var content = string.Join(Environment.NewLine, paths);
                return CreateRequest(
                    content,
                    Encoding.UTF8.GetBytes(content),
                    ContentType.Files,
                    ClipContentFormat.FileList,
                    sourceInfo);
            }
        }

        var plainText = await clipboardData.TryGetTextAsync().ConfigureAwait(false);
        var rtf = await TryGetStringAsync(clipboardData, RtfFormats).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            return CreateRequest(
                ClipDisplayFormatter.RenderRichContent(rtf),
                Encoding.UTF8.GetBytes(rtf),
                ContentType.RichText,
                ClipContentFormat.Rtf,
                sourceInfo);
        }

        var html = await TryGetStringAsync(clipboardData, HtmlFormats).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(html))
        {
            return CreateRequest(
                ClipDisplayFormatter.RenderRichContent(html),
                Encoding.UTF8.GetBytes(html),
                ContentType.RichText,
                ClipContentFormat.Html,
                sourceInfo);
        }

        var bitmap = await clipboardData.TryGetBitmapAsync().ConfigureAwait(false);
        if (bitmap is not null)
        {
            await using var bitmapStream = new MemoryStream();
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

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            return CreateRequest(
                plainText,
                Encoding.UTF8.GetBytes(plainText),
                ContentType.Text,
                ClipContentFormat.PlainText,
                sourceInfo);
        }

        return null;
    }

    private static async Task<string?> TryGetStringAsync(IAsyncDataTransfer clipboardData, string[] formatNames)
    {
        foreach (var formatName in formatNames)
        {
            var value = await clipboardData.TryGetValueAsync(DataFormat.CreateStringPlatformFormat(formatName)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
