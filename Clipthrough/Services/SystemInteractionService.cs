using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Microsoft.Win32;

namespace Clipthrough.Services;

public sealed class SystemInteractionService : ISystemInteractionService
{
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const string WindowsRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValueName = "Clipthrough";
    private static readonly string[] HtmlFormats = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormats = ["Rich Text Format", "text/rtf", "public.rtf"];
    private WindowsGlobalHotKeyRegistration? _globalHotKeyRegistration;

    public async Task CopyTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException(AppText.ClipboardAccessUnavailable);
        }

        await clipboard.SetTextAsync(text);
        await clipboard.FlushAsync();
    }

    public async Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat)
    {
        var effectivePlainText = string.IsNullOrWhiteSpace(plainText) ? ClipDisplayFormatter.RenderRichContent(richContent) : plainText;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (TryCopyRichContentToWindowsClipboard(richContent, effectivePlainText, contentFormat, out var richCopyError))
            {
                return;
            }

            throw new InvalidOperationException(richCopyError ?? "Unable to copy rich text to the Windows clipboard.");
        }

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException(AppText.ClipboardAccessUnavailable);
        }

        var item = new DataTransferItem();
        item.Set(DataFormat.Text, effectivePlainText);

        var formats = contentFormat switch
        {
            ClipContentFormat.Html => HtmlFormats,
            ClipContentFormat.Rtf => RtfFormats,
            _ => [],
        };

        foreach (var format in formats)
        {
            item.Set(DataFormat.CreateStringPlatformFormat(format), richContent);
        }

        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
        await clipboard.FlushAsync();
    }

    public async Task CopyBitmapAsync(Bitmap bitmap)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (TryCopyBitmapToWindowsClipboard(bitmap, out var bitmapCopyError))
            {
                return;
            }

            throw new InvalidOperationException(bitmapCopyError ?? "Unable to copy image data to the Windows clipboard.");
        }

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException(AppText.ClipboardAccessUnavailable);
        }

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetBitmap(bitmap);
        data.Add(item);

        await clipboard.SetDataAsync(data);
        await clipboard.FlushAsync();
    }

    public Task OpenPathAsync(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
        {
            throw new FileNotFoundException(AppText.FormatMissingPath(normalizedPath), normalizedPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = normalizedPath,
                UseShellExecute = true,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { normalizedPath },
                UseShellExecute = false,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { normalizedPath },
                UseShellExecute = false,
            });
        }

        return Task.CompletedTask;
    }

    public Task OpenContainingDirectoryAsync(string path)
    {
        var normalizedPath = NormalizePath(path);
        var directoryPath = Directory.Exists(normalizedPath)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath);

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(AppText.ContainingDirectoryNotFound);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(normalizedPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{normalizedPath}\"",
                UseShellExecute = true,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { directoryPath },
                UseShellExecute = false,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { directoryPath },
                UseShellExecute = false,
            });
        }

        return Task.CompletedTask;
    }

    public bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback)
    {
        UnregisterGlobalHotKey();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle?.Handle is not { } windowHandle || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!hotkey.TryGetWindowsRegistration(out var modifiers, out var virtualKey))
        {
            return false;
        }

        _globalHotKeyRegistration = WindowsGlobalHotKeyRegistration.TryCreate(windowHandle, modifiers, virtualKey, callback);
        return _globalHotKeyRegistration is not null;
    }

    public void UnregisterGlobalHotKey()
    {
        _globalHotKeyRegistration?.Dispose();
        _globalHotKeyRegistration = null;
    }

    public void SyncStartWithWindows(bool enabled)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            SyncStartWithWindowsCore(enabled);
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"Start-with-Windows registration failed: {ex.Message}");
        }
        catch (System.Security.SecurityException ex)
        {
            Trace.TraceWarning($"Start-with-Windows registration failed: {ex.Message}");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(AppText.PathRequired, nameof(path));
        }

        return path.Trim().Trim('"');
    }

    [SupportedOSPlatform("windows")]
    private static void SyncStartWithWindowsCore(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(WindowsRunRegistryPath, writable: true);
        if (runKey is null)
        {
            return;
        }

        if (!enabled)
        {
            runKey.DeleteValue(WindowsRunValueName, throwOnMissingValue: false);
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        var command = QuoteCommandArgument(processPath);
        var existingValue = runKey.GetValue(WindowsRunValueName) as string;
        if (string.Equals(existingValue, command, StringComparison.Ordinal))
        {
            return;
        }

        runKey.SetValue(WindowsRunValueName, command, RegistryValueKind.String);
    }

    private static string QuoteCommandArgument(string value)
        => $"\"{value}\"";

    private static Avalonia.Input.Platform.IClipboard? GetClipboard()
        => ClipboardAccess.GetClipboard();

    [SupportedOSPlatform("windows")]
    private static bool TryCopyRichContentToWindowsClipboard(string richContent, string plainText, ClipContentFormat contentFormat, out string? error)
    {
        error = null;

        try
        {
            OpenWindowsClipboard(() =>
            {
                SetClipboardDataOrThrow(CfUnicodeText, CreateGlobalTextHandle(plainText, Encoding.Unicode), static handle => _ = GlobalFree(handle));

                if (contentFormat == ClipContentFormat.Rtf)
                {
                    var rtfFormat = RegisterClipboardFormat("Rich Text Format");
                    var normalizedRtf = NormalizeRtfForClipboard(richContent);
                    SetClipboardDataOrThrow(rtfFormat, CreateGlobalTextHandle(normalizedRtf, Encoding.ASCII), static handle => _ = GlobalFree(handle));
                    return;
                }

                if (contentFormat == ClipContentFormat.Html)
                {
                    var htmlFormat = RegisterClipboardFormat("HTML Format");
                    var cfHtml = LooksLikeCfHtml(richContent) ? richContent : BuildCfHtml(richContent);
                    SetClipboardDataOrThrow(htmlFormat, CreateGlobalTextHandle(cfHtml, Encoding.UTF8), static handle => _ = GlobalFree(handle));
                }
            });

            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (OutOfMemoryException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryCopyBitmapToWindowsClipboard(Bitmap bitmap, out string? error)
    {
        error = null;

        try
        {
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream);
            var pngBytes = pngStream.ToArray();

            using var drawingStream = new MemoryStream(pngBytes, writable: false);
            using var drawingBitmap = new System.Drawing.Bitmap(drawingStream);
            using var dibStream = new MemoryStream();
            drawingBitmap.Save(dibStream, ImageFormat.Bmp);

            var dibBytes = dibStream.ToArray();
            if (dibBytes.Length <= 14)
            {
                throw new InvalidOperationException("Unable to create device-independent bitmap data.");
            }

            var pngFormat = RegisterClipboardFormat("PNG");
            OpenWindowsClipboard(() =>
            {
                SetClipboardDataOrThrow(pngFormat, CreateGlobalBinaryHandle(pngBytes), static handle => _ = GlobalFree(handle));
                SetClipboardDataOrThrow(CfDib, CreateGlobalBinaryHandle(dibBytes[14..]), static handle => _ = GlobalFree(handle));
            });

            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (OutOfMemoryException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void OpenWindowsClipboard(Action setClipboardData)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        throw new InvalidOperationException("Unable to clear the clipboard.");
                    }

                    setClipboardData();
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException("Unable to access the clipboard.");
    }

    [SupportedOSPlatform("windows")]
    private static void SetClipboardDataOrThrow(uint format, nint handle, Action<nint> releaseOnFailure)
    {
        if (format == 0 || handle == IntPtr.Zero)
        {
            if (handle != IntPtr.Zero)
            {
                releaseOnFailure(handle);
            }

            throw new InvalidOperationException("Invalid clipboard payload.");
        }

        if (SetClipboardData(format, handle) != IntPtr.Zero)
        {
            return;
        }

        releaseOnFailure(handle);
        throw new InvalidOperationException("Failed to place data on the clipboard.");
    }

    [SupportedOSPlatform("windows")]
    private static nint CreateGlobalTextHandle(string text, Encoding encoding)
    {
        var payload = encoding.Equals(Encoding.Unicode)
            ? encoding.GetBytes(text + '\0')
            : encoding.GetBytes(text + "\0");

        var handle = GlobalAlloc(GmemMoveable | GmemZeroinit, (nuint)payload.Length);
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Failed to allocate clipboard memory.");
        }

        var address = GlobalLock(handle);
        if (address == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("Failed to lock clipboard memory.");
        }

        try
        {
            Marshal.Copy(payload, 0, address, payload.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static nint CreateGlobalBinaryHandle(byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable | GmemZeroinit, (nuint)bytes.Length);
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Failed to allocate clipboard memory.");
        }

        var address = GlobalLock(handle);
        if (address == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("Failed to lock clipboard memory.");
        }

        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static string NormalizeRtfForClipboard(string richContent)
    {
        var builder = new StringBuilder(richContent.Length);
        foreach (var character in richContent)
        {
            if (character <= sbyte.MaxValue)
            {
                builder.Append(character);
                continue;
            }

            builder.Append("\\u");
            builder.Append(unchecked((short)character));
            builder.Append('?');
        }

        return builder.ToString();
    }

    private static bool LooksLikeCfHtml(string content)
        => content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
           && content.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase)
           && content.Contains("StartFragment:", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string BuildCfHtml(string html)
    {
        const string startFragmentMarker = "<!--StartFragment-->";
        const string endFragmentMarker = "<!--EndFragment-->";
        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

        var fragment = html;
        if (LooksLikeCfHtml(fragment))
        {
            return fragment;
        }

        if (!LooksLikeHtml(fragment))
        {
            fragment = System.Net.WebUtility.HtmlEncode(fragment).Replace(Environment.NewLine, "<br>", StringComparison.Ordinal);
        }

        var document = $"<html><body>{startFragmentMarker}{fragment}{endFragmentMarker}</body></html>";
        var header = string.Format(headerTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(header);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body>");
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(startFragmentMarker) + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(document);

        header = string.Format(headerTemplate, startHtml, endHtml, startFragment + Encoding.UTF8.GetByteCount(startFragmentMarker), endFragment);
        return header + document;
    }

    private static bool LooksLikeHtml(string content)
        => !string.IsNullOrWhiteSpace(content)
           && Regex.IsMatch(content, @"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksLikeRtf(string content)
        => !string.IsNullOrWhiteSpace(content)
           && content.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    private sealed class WindowsGlobalHotKeyRegistration : IDisposable
    {
        private const int GwlWndProc = -4;
        private const int WmHotKey = 0x0312;

        private static readonly ConcurrentDictionary<nint, WindowsGlobalHotKeyRegistration> Registrations = new();

        private readonly nint _windowHandle;
        private readonly int _hotKeyId;
        private readonly Action _callback;
        private readonly WndProc _wndProcDelegate;
        private readonly nint _newWndProc;
        private readonly nint _previousWndProc;
        private bool _isDisposed;

        private WindowsGlobalHotKeyRegistration(nint windowHandle, int hotKeyId, Action callback)
        {
            _windowHandle = windowHandle;
            _hotKeyId = hotKeyId;
            _callback = callback;
            _wndProcDelegate = WindowProc;
            _newWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _previousWndProc = SetWindowLongPtr(windowHandle, GwlWndProc, _newWndProc);
        }

        public static WindowsGlobalHotKeyRegistration? TryCreate(nint windowHandle, uint modifiers, uint virtualKey, Action callback)
        {
            var hotKeyId = RuntimeHelpers.GetHashCode(callback);
            if (!RegisterHotKey(windowHandle, hotKeyId, modifiers, virtualKey))
            {
                return null;
            }

            var registration = new WindowsGlobalHotKeyRegistration(windowHandle, hotKeyId, callback);
            Registrations[windowHandle] = registration;
            return registration;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Registrations.TryRemove(_windowHandle, out _);
            UnregisterHotKey(_windowHandle, _hotKeyId);
            SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWndProc);
        }

        private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WmHotKey && wParam.ToInt32() == _hotKeyId)
            {
                Dispatcher.UIThread.Post(_callback);
                return 0;
            }

            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

        private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

        private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc)
            => IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, newProc)
                : SetWindowLong32(hWnd, nIndex, newProc.ToInt32());
    }
}

