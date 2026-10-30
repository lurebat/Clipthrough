using System;
using System.Runtime.InteropServices;

namespace Clipthrough.Diagnostics;

/// <summary>
/// Tells the user why Clipthrough did not start, before Avalonia exists. A
/// Windows build has no console, so the message box is the only thing they
/// would see; stderr carries the same text for scripts and other platforms.
/// </summary>
internal static class StartupErrorDialog
{
    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;

    public static void Show(string title, string message)
    {
        Console.Error.WriteLine($"{title}: {message}");
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, message, title, MbOk | MbIconError);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}