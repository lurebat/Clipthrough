using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clipthrough.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsSourceApplicationResolver : ISourceApplicationResolver
{
    public ClipboardSourceApplicationInfo? TryResolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ResolveCore();
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning($"Source app lookup failed: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"Source app lookup failed: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"Source app lookup failed (process exited): {ex.Message}");
            return null;
        }
    }

    private static ClipboardSourceApplicationInfo? ResolveCore()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(owner, out var processId);
        if (processId == 0)
        {
            return null;
        }

        using var process = Process.GetProcessById((int)processId);
        var processPath = TryGetProcessPath(process);
        var name = TryGetProcessName(process, processPath);
        var iconBytes = TryGetProcessIcon(processPath);
        var windowTitle = TryGetWindowTitle(owner);

        // Fallbacks: the clipboard owner is often a hidden helper window with no title
        // (e.g., Snip & Sketch writing a bitmap). Fall back to the foreground window's
        // title, then to the main window of the same process.
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != owner)
            {
                windowTitle = TryGetWindowTitle(fg);
            }
        }
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            try
            {
                var mainHwnd = process.MainWindowHandle;
                if (mainHwnd != IntPtr.Zero && mainHwnd != owner)
                {
                    windowTitle = TryGetWindowTitle(mainHwnd);
                }
                if (string.IsNullOrWhiteSpace(windowTitle) && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    windowTitle = process.MainWindowTitle;
                }
            }
            catch
            {
                // best effort
            }
        }

        return string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(processPath) && iconBytes is null
            ? null
            : new ClipboardSourceApplicationInfo(name, processPath, iconBytes, windowTitle);
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceInformation($"Source app process path unavailable for pid {process.Id}: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceInformation($"Source app process path unavailable for pid {process.Id}: {ex.Message}");
            return null;
        }
    }

    private static string? TryGetProcessName(Process process, string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(processPath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }
            }
            catch (FileNotFoundException ex)
            {
                Trace.TraceInformation($"Source app description unavailable for '{processPath}': {ex.Message}");
            }
        }

        return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
    }

    private static byte[]? TryGetProcessIcon(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(processPath);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (ArgumentException ex)
        {
            Trace.TraceInformation($"Source app icon unavailable for '{processPath}': {ex.Message}");
            return null;
        }
        catch (ExternalException ex)
        {
            Trace.TraceInformation($"Source app icon unavailable for '{processPath}': {ex.Message}");
            return null;
        }
    }

    private static string? TryGetWindowTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return null;
            }

            var sb = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed record ClipboardSourceApplicationInfo(string? Name, string? Path, byte[]? IconBytes, string? WindowTitle = null);
