using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class ClipboardMonitorService : IClipboardMonitorService, IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IClipStoreService _clipStoreService;
    private readonly ISettingsService _settingsService;
    private readonly Subject<ClipEntry> _capturedClips = new();

    private Window? _window;
    private bool _isStarted;
    private bool _isHookAttached;
    private bool _isDisposed;

    public ClipboardMonitorService(IClipStoreService clipStoreService, ISettingsService settingsService)
    {
        _clipStoreService = clipStoreService;
        _settingsService = settingsService;
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
        if (!_isStarted || _isDisposed || _window is null)
        {
            return;
        }

        try
        {
            var clipboard = _window.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            using var clipboardData = await clipboard.TryGetDataAsync();
            if (clipboardData is null)
            {
                return;
            }

            var text = await clipboardData.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var capturedClip = await _clipStoreService.CaptureAsync(text, ContentType.Text, null).ConfigureAwait(false);
            if (capturedClip is not null)
            {
                _capturedClips.OnNext(capturedClip);
            }
        }
        catch
        {
            // Silently handle errors
        }
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
            catch
            {
                // Silently handle removal failure
            }

            try
            {
                Win32Properties.RemoveWndProcHookCallback(_window, WndProcHook);
            }
            catch
            {
                // Silently handle removal failure
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
            // Always attach the WndProc hook to handle WM_CLIPBOARDUPDATE messages
            Win32Properties.AddWndProcHookCallback(_window, WndProcHook);
            
            // Also try to use the more efficient AddClipboardFormatListener
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                AddClipboardFormatListener(hwnd);
            }

            _isHookAttached = true;
        }
        catch
        {
            // Silently handle attachment failure
        }
    }

    private Window? ResolveMainWindow()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }
            ? mainWindow
            : null;


}

