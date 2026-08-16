using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Microsoft.Win32;

namespace Clipthrough.Services.Platform;

public sealed class SystemInteractionService : ISystemInteractionService, IDisposable
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const string WindowsRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValueName = "Clipthrough";
    private static readonly string[] HtmlFormats = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormats = ["Rich Text Format", "text/rtf", "public.rtf"];
    private WindowsGlobalHotKeyRegistration? _globalHotKeyRegistration;
    private readonly Dictionary<string, WindowsGlobalHotKeyRegistration> _namedHotKeys = new(StringComparer.Ordinal);
    private WindowsBalloonNotificationHost? _notificationHost;
    private nint _capturedPasteTarget;

    public async Task CopyTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException(AppText.ClipboardAccessUnavailable);
        }

        await clipboard.SetTextAsync(text);
    }

    public async Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat)
    {
        var effectivePlainText = string.IsNullOrWhiteSpace(plainText) ? ClipDisplayFormatter.RenderRichContent(richContent) : plainText;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var (richCopySuccess, richCopyError) = await Task.Run(() =>
            {
                var ok = TryCopyRichContentToWindowsClipboard(richContent, effectivePlainText, contentFormat, out var err);
                return (ok, err);
            });
            if (richCopySuccess) return;
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
    }

    public async Task CopyBitmapAsync(Bitmap bitmap)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var (bitmapCopySuccess, bitmapCopyError) = await Task.Run(() =>
            {
                var ok = TryCopyBitmapToWindowsClipboard(bitmap, out var err);
                return (ok, err);
            });
            if (bitmapCopySuccess) return;
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
    }

    public Task OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must not be empty.", nameof(url));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            throw new ArgumentException($"Unsupported URL scheme: {url}", nameof(url));
        }

        var target = uri.ToString();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { target }, UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { target }, UseShellExecute = false });
        }

        return Task.CompletedTask;
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

    public Task OpenInEditorAsync(string filePath, string editorPath)
    {
        if (string.IsNullOrWhiteSpace(editorPath))
        {
            return OpenPathAsync(filePath);
        }

        var (exe, args) = ParseCommandTemplate(editorPath, new[] { ("{file}", filePath) }, fallbackAppend: new[] { filePath });
        if (!File.Exists(exe))
        {
            return OpenPathAsync(filePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
        return Task.CompletedTask;
    }

    public Task OpenInDiffToolAsync(string leftPath, string rightPath, string diffToolPath)
    {
        if (string.IsNullOrWhiteSpace(diffToolPath))
        {
            throw new FileNotFoundException($"Diff tool not configured.");
        }

        var (exe, args) = ParseCommandTemplate(diffToolPath,
            new[] { ("{left}", leftPath), ("{right}", rightPath), ("{file}", leftPath) },
            fallbackAppend: new[] { leftPath, rightPath });
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"Diff tool not found: {exe}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses a command template like 'C:\path\code.exe --diff {left} {right}'.
    /// Honors double-quoted path segments. If no placeholder in <paramref name="substitutions"/>
    /// appears in the template, the paths in <paramref name="fallbackAppend"/> are appended
    /// as additional arguments.
    /// </summary>
    private static (string Executable, System.Collections.Generic.List<string> Args) ParseCommandTemplate(
        string template,
        (string Token, string Value)[] substitutions,
        string[] fallbackAppend)
    {
        var tokens = TokenizeCommandLine(template);
        if (tokens.Count == 0)
        {
            return (template, new System.Collections.Generic.List<string>(fallbackAppend));
        }

        var exe = tokens[0];
        var args = new System.Collections.Generic.List<string>();
        var anyPlaceholderSeen = false;
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            foreach (var (token, value) in substitutions)
            {
                if (t.Contains(token, StringComparison.Ordinal))
                {
                    t = t.Replace(token, value, StringComparison.Ordinal);
                    anyPlaceholderSeen = true;
                }
            }
            args.Add(t);
        }

        if (!anyPlaceholderSeen)
        {
            args.AddRange(fallbackAppend);
        }

        return (exe, args);
    }

    private static System.Collections.Generic.List<string> TokenizeCommandLine(string input)
    {
        var result = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }
        return result;
    }

    public void CaptureTargetWindowForPaste()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _capturedPasteTarget = GetForegroundWindow();
        }
    }

    public void ClearTargetWindowCapture()
    {
        _capturedPasteTarget = IntPtr.Zero;
    }

    /// <summary>
    /// Explicitly restores keyboard focus to the captured target window using
    /// AttachThreadInput so the call succeeds regardless of foreground lock state.
    /// </summary>
    public void RestoreCapturedForeground()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var target = System.Threading.Interlocked.Exchange(ref _capturedPasteTarget, IntPtr.Zero);
        if (target == IntPtr.Zero)
        {
            Trace.TraceWarning("[Paste] RestoreCapturedForeground: no captured target (HWND is zero).");
            return;
        }

        try
        {
            RestoreForegroundCore(target);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"RestoreCapturedForeground failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreForegroundCore(nint target)
    {
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(target, out var targetProcessId);

        // Attach input queues so SetForegroundWindow works even without foreground rights.
        var attached = targetThreadId != 0 && targetThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            var sfwResult = SetForegroundWindow(target);
            BringWindowToTop(target);
            Trace.TraceInformation(
                $"[Paste] RestoreForeground: target=0x{target:X} pid={targetProcessId} " +
                $"tid={targetThreadId} attached={attached} SetForegroundWindow={sfwResult}");
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    public void SimulatePasteKeystroke()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            SendPasteInputs();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Simulate paste failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SendPasteInputs()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_SHIFT = 0x10;
        const ushort VK_LWIN = 0x5B;
        const ushort VK_RWIN = 0x5C;
        const ushort VK_MENU = 0x12;
        const ushort VK_V = 0x56;
        const uint KEYEVENTF_KEYUP = 0x0002;

        // Release any currently-held modifiers so our synthetic Ctrl+V isn't interpreted
        // as Ctrl+Shift+V / Ctrl+Alt+V / etc. by the receiving application.
        var toRelease = new List<ushort>();
        void ReleaseIfDown(ushort vk)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                toRelease.Add(vk);
            }
        }

        ReleaseIfDown(VK_SHIFT);
        ReleaseIfDown(VK_MENU);
        ReleaseIfDown(VK_LWIN);
        ReleaseIfDown(VK_RWIN);
        // Note: VK_CONTROL is intentionally not released here — we will press V while Ctrl is down.
        var ctrlIsDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        var inputs = new List<INPUT>();
        foreach (var vk in toRelease)
        {
            inputs.Add(MakeKey(vk, KEYEVENTF_KEYUP));
        }

        if (!ctrlIsDown)
        {
            inputs.Add(MakeKey(VK_CONTROL, 0));
        }

        inputs.Add(MakeKey(VK_V, 0));
        inputs.Add(MakeKey(VK_V, KEYEVENTF_KEYUP));

        if (!ctrlIsDown)
        {
            inputs.Add(MakeKey(VK_CONTROL, KEYEVENTF_KEYUP));
        }

        var arr = inputs.ToArray();
        var cbSize = Marshal.SizeOf<INPUT>();
        System.Diagnostics.Debug.Assert(cbSize == 40, $"INPUT struct size mismatch: got {cbSize}, expected 40");
        var sent = SendInput((uint)arr.Length, arr, cbSize);
        if (sent != arr.Length)
        {
            Trace.TraceWarning($"[Paste] SendInput: requested {arr.Length} events, injected {sent} (error {Marshal.GetLastWin32Error()})");
        }

        static INPUT MakeKey(ushort vk, uint flags) => new()
        {
            type = 1, // INPUT_KEYBOARD
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // InputUnion must include MOUSEINPUT so the union is 32 bytes on x64,
    // matching the real Win32 sizeof(INPUT) = 40. Without it the union is only
    // 24 bytes and Marshal.SizeOf<INPUT>() returns 32, causing SendInput to
    // silently reject every call because cbSize does not match.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void ShowNotification(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        _notificationHost ??= WindowsBalloonNotificationHost.TryCreate();
        if (_notificationHost is null)
        {
            Trace.TraceWarning("System notification failed: Unable to initialize the notification host.");
            return;
        }

        if (!_notificationHost.TryShow(notification, out var error))
        {
            Trace.TraceWarning($"System notification failed: {error}");
        }
    }

    public void Dispose()
    {
        _globalHotKeyRegistration?.Dispose();
        _globalHotKeyRegistration = null;
        _notificationHost?.Dispose();
        _notificationHost = null;
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

        _globalHotKeyRegistration = WindowsGlobalHotKeyRegistration.TryCreate(window, windowHandle, modifiers, virtualKey, callback);
        return _globalHotKeyRegistration is not null;
    }

    public bool TryRegisterGlobalHotKey(Window window, string name, HotkeyGesture hotkey, Action callback)
    {
        UnregisterGlobalHotKey(name);

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

        var registration = WindowsGlobalHotKeyRegistration.TryCreate(window, windowHandle, modifiers, virtualKey, callback);
        if (registration is null)
        {
            return false;
        }

        _namedHotKeys[name] = registration;
        return true;
    }

    public void UnregisterGlobalHotKey()
    {
        _globalHotKeyRegistration?.Dispose();
        _globalHotKeyRegistration = null;
    }

    public void UnregisterGlobalHotKey(string name)
    {
        if (_namedHotKeys.Remove(name, out var registration))
        {
            registration.Dispose();
        }
    }

    public void UnregisterAllGlobalHotKeys()
    {
        UnregisterGlobalHotKey();
        foreach (var registration in _namedHotKeys.Values)
        {
            registration.Dispose();
        }

        _namedHotKeys.Clear();
    }

    public PixelPoint? GetCaretScreenPosition()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            return GetCaretScreenPositionCore();
        }
        catch (Exception ex)
        {
            Trace.TraceInformation($"Caret position detection failed: {ex.Message}");
            return null;
        }
    }

    public bool IsTargetWindowElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            return IsTargetWindowElevatedCore();
        }
        catch (Exception ex)
        {
            Trace.TraceInformation($"Elevation check failed: {ex.Message}");
            return false;
        }
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
                    var normalizedRtf = RichClipboardFormatting.NormalizeRtfForClipboard(richContent);
                    SetClipboardDataOrThrow(rtfFormat, CreateGlobalTextHandle(normalizedRtf, Encoding.ASCII), static handle => _ = GlobalFree(handle));
                    return;
                }

                if (contentFormat == ClipContentFormat.Html)
                {
                    var htmlFormat = RegisterClipboardFormat("HTML Format");
                    var cfHtml = RichClipboardFormatting.LooksLikeCfHtml(richContent) ? richContent : RichClipboardFormatting.BuildCfHtml(richContent);
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
            bitmap.Save(pngStream, PngBitmapEncoderOptions.Default);
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
            // Not OutOfMemoryException: that type is reserved for the runtime, and
            // a caller that catches it to shed memory would be reacting to a failed
            // GlobalAlloc of a few KB rather than to actual memory pressure.
            throw new InvalidOperationException("Failed to allocate clipboard memory.");
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
            throw new InvalidOperationException("Failed to allocate clipboard memory.");
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

    private sealed class WindowsBalloonNotificationHost : IDisposable
    {
        private const int GwlWndProc = -4;
        private const uint NimAdd = 0x00000000;
        private const uint NimModify = 0x00000001;
        private const uint NimDelete = 0x00000002;
        private const uint NimSetVersion = 0x00000004;
        private const uint NotifyIconVersion4 = 4;
        private const uint WmApp = 0x8000;
        private const uint WmTrayIcon = WmApp + 1;
        private const uint WmLButtonUp = 0x0202;
        private const uint NinBalloonUserClick = 0x0405;
        private const uint NifIcon = 0x00000002;
        private const uint NifState = 0x00000008;
        private const uint NifTip = 0x00000004;
        private const uint NifMessage = 0x00000001;
        private const uint NifInfo = 0x00000010;
        private const uint NisHidden = 0x00000001;
        private const uint NiifInfo = 0x00000001;
        private const uint NiifWarning = 0x00000002;
        private const uint NiifError = 0x00000003;
        private const int IdiInformation = 32516;
        private static readonly nint HwndMessage = new(-3);

        private readonly nint _windowHandle;
        private readonly nint _iconHandle;
        private readonly WndProc _wndProcDelegate;
        private readonly nint _previousWndProc;
        private Action? _activationCallback;
        private bool _isDisposed;

        private WindowsBalloonNotificationHost(nint windowHandle, nint iconHandle)
        {
            _windowHandle = windowHandle;
            _iconHandle = iconHandle;
            _wndProcDelegate = WindowProc;
            _previousWndProc = SetWindowLongPtr(_windowHandle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }

        public static WindowsBalloonNotificationHost? TryCreate()
        {
            var windowHandle = CreateWindowEx(0, "STATIC", "Clipthrough.NotificationHost", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (windowHandle == IntPtr.Zero)
            {
                Trace.TraceWarning($"System notification host initialization failed: {GetLastErrorMessage("Unable to create the notification host window.")}");
                return null;
            }

            var iconHandle = LoadIcon(IntPtr.Zero, new nint(IdiInformation));
            if (iconHandle == IntPtr.Zero)
            {
                Trace.TraceWarning($"System notification host initialization failed: {GetLastErrorMessage("Unable to load the notification icon.")}");
                _ = DestroyWindow(windowHandle);
                return null;
            }

            var host = new WindowsBalloonNotificationHost(windowHandle, iconHandle);
            if (!host.TryRegisterIcon(out var error))
            {
                Trace.TraceWarning($"System notification host initialization failed: {error}");
                host.Dispose();
                return null;
            }

            return host;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            var data = CreateNotificationData();
            _ = Shell_NotifyIcon(NimDelete, ref data);
            _ = SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWndProc);
            _ = DestroyWindow(_windowHandle);
        }

        public bool TryShow(AppNotification notification, out string? error)
        {
            if (_isDisposed)
            {
                error = "The notification host has been disposed.";
                return false;
            }

            _activationCallback = notification.Activated;
            var data = CreateNotificationData();
            data.uFlags = NifInfo;
            data.szInfoTitle = notification.Title;
            data.szInfo = notification.Message;
            data.dwInfoFlags = GetInfoFlags(notification.Level);
            data.uTimeoutOrVersion = 10000;

            if (!Shell_NotifyIcon(NimModify, ref data))
            {
                error = GetLastErrorMessage("Unable to display the system notification.");
                return false;
            }

            error = null;
            return true;
        }

        private bool TryRegisterIcon(out string? error)
        {
            var data = CreateNotificationData();
            data.uFlags = NifIcon | NifTip | NifState | NifMessage;
            data.hIcon = _iconHandle;
            data.szTip = "Clipthrough";
            data.dwState = NisHidden;
            data.dwStateMask = NisHidden;
            data.uCallbackMessage = WmTrayIcon;

            if (!Shell_NotifyIcon(NimAdd, ref data))
            {
                error = GetLastErrorMessage("Unable to register the system notification icon.");
                return false;
            }

            data.uVersion = NotifyIconVersion4;
            _ = Shell_NotifyIcon(NimSetVersion, ref data);
            error = null;
            return true;
        }

        private NotifyIconData CreateNotificationData() => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _windowHandle,
            uID = 1,
        };

        private static uint GetInfoFlags(AppNotificationLevel level)
            => level switch
            {
                AppNotificationLevel.Error => NiifError,
                AppNotificationLevel.Warning => NiifWarning,
                _ => NiifInfo,
            };

        private static string GetLastErrorMessage(string fallback)
        {
            var errorCode = Marshal.GetLastWin32Error();
            return errorCode == 0
                ? fallback
                : new Win32Exception(errorCode).Message;
        }

        private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WmTrayIcon)
            {
                var notificationMessage = unchecked((uint)lParam.ToInt64());
                if (notificationMessage == NinBalloonUserClick || notificationMessage == WmLButtonUp)
                {
                    var callback = _activationCallback;
                    if (callback is not null)
                    {
                        Dispatcher.UIThread.Post(callback);
                        return 0;
                    }
                }
            }

            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
            public uint uVersion
            {
                set => uTimeoutOrVersion = value;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(
            int exStyle,
            string className,
            string? windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            nint parentHandle,
            nint menuHandle,
            nint instanceHandle,
            nint param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint LoadIcon(nint instanceHandle, nint iconName);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandle(string? moduleName);

        private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc)
            => IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, newProc)
                : SetWindowLong32(hWnd, nIndex, newProc.ToInt32());

        private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);
    }

    [SupportedOSPlatform("windows")]
    private static PixelPoint? GetCaretScreenPositionCore()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (threadId == 0)
        {
            return null;
        }

        var guiInfo = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref guiInfo))
        {
            return null;
        }

        if (guiInfo.hwndCaret == IntPtr.Zero)
        {
            // No caret — fall back to foreground window position
            if (GetWindowRect(foregroundWindow, out var windowRect))
            {
                return new PixelPoint(windowRect.Left + 50, windowRect.Top + 50);
            }

            return null;
        }

        var point = new POINT { X = guiInfo.rcCaret.Left, Y = guiInfo.rcCaret.Bottom };
        ClientToScreen(guiInfo.hwndCaret, ref point);

        return new PixelPoint(point.X, point.Y);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTargetWindowElevatedCore()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            using var processHandle = process.SafeHandle;
            if (!OpenProcessToken(processHandle.DangerousGetHandle(), TOKEN_QUERY, out var tokenHandle))
            {
                return false;
            }

            try
            {
                var elevationResult = TOKEN_ELEVATION_TYPE.TokenElevationTypeDefault;
                var elevationResultSize = Marshal.SizeOf(typeof(int));
                var elevationTypePtr = Marshal.AllocHGlobal(elevationResultSize);
                try
                {
                    if (GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevationType, elevationTypePtr, elevationResultSize, out _))
                    {
                        elevationResult = (TOKEN_ELEVATION_TYPE)Marshal.ReadInt32(elevationTypePtr);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(elevationTypePtr);
                }

                return elevationResult == TOKEN_ELEVATION_TYPE.TokenElevationTypeFull;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Access denied likely means it is elevated
            return true;
        }
    }

    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevationType = 18,
    }

    private enum TOKEN_ELEVATION_TYPE
    {
        TokenElevationTypeDefault = 1,
        TokenElevationTypeFull = 2,
        TokenElevationTypeLimited = 3,
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class WindowsGlobalHotKeyRegistration : IDisposable
    {
        private const int WmHotKey = 0x0312;

        private readonly Window _window;
        private readonly nint _windowHandle;
        private readonly int _hotKeyId;
        private readonly Action _callback;
        private readonly Win32Properties.CustomWndProcHookCallback _hookCallback;
        private bool _isDisposed;

        private WindowsGlobalHotKeyRegistration(Window window, nint windowHandle, int hotKeyId, Action callback)
        {
            _window = window;
            _windowHandle = windowHandle;
            _hotKeyId = hotKeyId;
            _callback = callback;
            _hookCallback = HookCallback;
            Win32Properties.AddWndProcHookCallback(_window, _hookCallback);
        }

        public static WindowsGlobalHotKeyRegistration? TryCreate(Window window, nint windowHandle, uint modifiers, uint virtualKey, Action callback)
        {
            var hotKeyId = unchecked(RuntimeHelpers.GetHashCode(callback) & 0x7FFFFFFF);
            if (!RegisterHotKey(windowHandle, hotKeyId, modifiers, virtualKey))
            {
                return null;
            }

            return new WindowsGlobalHotKeyRegistration(window, windowHandle, hotKeyId, callback);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Win32Properties.RemoveWndProcHookCallback(_window, _hookCallback);
            UnregisterHotKey(_windowHandle, _hotKeyId);
        }

        private nint HookCallback(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
        {
            if (!_isDisposed && msg == WmHotKey && wParam.ToInt32() == _hotKeyId)
            {
                Dispatcher.UIThread.Post(_callback);
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint hWnd, int id);
    }
}
