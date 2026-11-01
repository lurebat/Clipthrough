using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static class Native
{
    internal static void Guard()
    {
        if (!OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("REFUSED: live qualification requires a GitHub-hosted Windows Actions runner. Never run this harness locally.");
    }

    internal static void Check(bool value, string operation)
    {
        if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal static void Chord(params ushort[] keys)
    {
        Guard();
        var inputs = keys.Select(k => Key(k, 0)).Concat(keys.Reverse().Select(k => Key(k, 2))).ToArray();
        Check(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length, "SendInput keyboard chord");
    }

    internal static void Type(string text)
    {
        Guard();
        var inputs = text.SelectMany(c => new[] { Key(c, 4, true), Key(c, 6, true) }).ToArray();
        Check(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length, "SendInput Unicode text");
    }

    internal static void Click(int x, int y)
    {
        Guard();
        Check(SetCursorPos(x, y), "SetCursorPos");
        var inputs = new[] { new INPUT { Type = 0, Data = new UNION { Mouse = new MOUSEINPUT { Flags = 2 } } },
                             new INPUT { Type = 0, Data = new UNION { Mouse = new MOUSEINPUT { Flags = 4 } } } };
        Check(SendInput(2, inputs, Marshal.SizeOf<INPUT>()) == 2, "SendInput mouse click");
    }

    private static INPUT Key(ushort key, uint flags, bool unicode = false)
        => new() { Type = 1, Data = new UNION { Keyboard = new KEYBDINPUT { Vk = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0, Flags = flags } } };

    internal static void Publish(nint owner, params (uint Format, byte[] Bytes)[] formats)
    {
        Guard();
        Check(OpenClipboard(owner), "OpenClipboard for synthetic publish");
        try
        {
            Check(EmptyClipboard(), "EmptyClipboard");
            foreach (var (format, bytes) in formats)
            {
                var handle = GlobalAlloc(0x42, (nuint)bytes.Length);
                Check(handle != 0, "GlobalAlloc");
                var transferred = false;
                try
                {
                    var pointer = GlobalLock(handle);
                    Check(pointer != 0, "GlobalLock");
                    try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                    finally { GlobalUnlock(handle); }
                    Check(SetClipboardData(format, handle) != 0, "SetClipboardData");
                    transferred = true;
                }
                finally { if (!transferred) GlobalFree(handle); }
            }
        }
        finally { Check(CloseClipboard(), "CloseClipboard"); }
    }

    internal static byte[] Read(uint format)
    {
        Guard();
        Check(OpenClipboard(0), "OpenClipboard for paste receiver");
        try
        {
            var handle = GetClipboardData(format);
            Check(handle != 0, $"GetClipboardData({format})");
            var size = GlobalSize(handle);
            if (size == 0 || size > 16 * 1024 * 1024) throw new InvalidOperationException($"Unexpected synthetic clipboard payload size: {size}");
            var pointer = GlobalLock(handle);
            Check(pointer != 0, "GlobalLock");
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { Check(CloseClipboard(), "CloseClipboard"); }
    }

    internal static uint Format(string name)
    {
        Guard();
        var format = RegisterClipboardFormat(name);
        Check(format != 0, "RegisterClipboardFormat");
        return format;
    }

    internal static bool Available(uint format)
    {
        Guard();
        return IsClipboardFormatAvailable(format);
    }

    internal static byte[] Text(string text) => Encoding.Unicode.GetBytes(text + '\0');

    internal static bool Presented(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window)) return false;
        var hr = DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int));
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return cloaked == 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public UNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}
