using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class WindowsClipboardCaptureReader
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfHdrop = 15;

    public ClipCaptureRequest? TryRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ReadCore();
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"Clipboard capture skipped: {ex.Message}");
            return null;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning($"Clipboard capture failed: {ex.Message}");
            return null;
        }
    }

    private static ClipCaptureRequest? ReadCore()
    {
        var source = ResolveSourceApplication();

        return OpenClipboardScope(() =>
            TryReadFileDropList(source)
            ?? TryReadRichText(source)
            ?? TryReadHtml(source)
            ?? TryReadPng(source)
            ?? TryReadDib(source)
            ?? TryReadText(source));
    }

    private static ClipCaptureRequest? TryReadFileDropList(ClipboardSourceApplication source)
    {
        if (!IsClipboardFormatAvailable(CfHdrop))
        {
            return null;
        }

        var handle = GetClipboardData(CfHdrop);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var fileCount = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        if (fileCount == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var index = 0u; index < fileCount; index++)
        {
            var length = DragQueryFile(handle, index, null, 0);
            if (length == 0)
            {
                continue;
            }

            var pathBuilder = new StringBuilder((int)length + 1);
            _ = DragQueryFile(handle, index, pathBuilder, pathBuilder.Capacity);
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(pathBuilder);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var content = builder.ToString();
        return new ClipCaptureRequest
        {
            ContentType = ContentType.Files,
            ContentText = content,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
        };
    }

    private static ClipCaptureRequest? TryReadRichText(ClipboardSourceApplication source)
    {
        var format = RegisterClipboardFormat("Rich Text Format");
        if (format == 0 || !IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var bytes = TryReadClipboardBytes(format);
        if (bytes is null)
        {
            return null;
        }

        var content = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentText = content,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
        };
    }

    private static ClipCaptureRequest? TryReadHtml(ClipboardSourceApplication source)
    {
        var format = RegisterClipboardFormat("HTML Format");
        if (format == 0 || !IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var bytes = TryReadClipboardBytes(format);
        if (bytes is null)
        {
            return null;
        }

        var content = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentText = content,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
        };
    }

    private static ClipCaptureRequest? TryReadPng(ClipboardSourceApplication source)
    {
        var format = RegisterClipboardFormat("PNG");
        if (format == 0 || !IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var bytes = TryReadClipboardBytes(format);
        if (bytes is null || !TryGetImageSize(bytes, out var width, out var height))
        {
            return null;
        }

        return new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentBytes = bytes,
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
            ImageWidth = width,
            ImageHeight = height,
        };
    }

    private static ClipCaptureRequest? TryReadDib(ClipboardSourceApplication source)
    {
        if (!IsClipboardFormatAvailable(CfDib))
        {
            return null;
        }

        var dibBytes = TryReadClipboardBytes(CfDib);
        if (dibBytes is null || !TryConvertDibToBmp(dibBytes, out var bmpBytes) || !TryGetImageSize(bmpBytes, out var width, out var height))
        {
            return null;
        }

        return new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentBytes = bmpBytes,
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
            ImageWidth = width,
            ImageHeight = height,
        };
    }

    private static ClipCaptureRequest? TryReadText(ClipboardSourceApplication source)
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        var bytes = TryReadClipboardBytes(CfUnicodeText);
        if (bytes is null)
        {
            return null;
        }

        var content = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentText = content,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            SourceApp = source.Name,
            SourceAppPath = source.Path,
            SourceAppIconBytes = source.IconBytes,
        };
    }

    private static ClipboardSourceApplication ResolveSourceApplication()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
        {
            return ClipboardSourceApplication.Empty;
        }

        _ = GetWindowThreadProcessId(owner, out var processId);
        if (processId == 0)
        {
            return ClipboardSourceApplication.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processPath = TryGetProcessPath(process);
            var processName = TryGetProcessName(process, processPath);
            var iconBytes = TryGetProcessIcon(processPath);

            return new ClipboardSourceApplication(processName, processPath, iconBytes);
        }
        catch (ArgumentException)
        {
            return ClipboardSourceApplication.Empty;
        }
        catch (InvalidOperationException)
        {
            return ClipboardSourceApplication.Empty;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryGetProcessName(Process process, string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                var fileDescription = FileVersionInfo.GetVersionInfo(processPath).FileDescription;
                if (!string.IsNullOrWhiteSpace(fileDescription))
                {
                    return fileDescription;
                }
            }
            catch (FileNotFoundException)
            {
            }
        }

        return !string.IsNullOrWhiteSpace(process.ProcessName) ? process.ProcessName : null;
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
        catch (ArgumentException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static bool TryGetImageSize(byte[] imageBytes, out int width, out int height)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            width = image.Width;
            height = image.Height;
            return width > 0 && height > 0;
        }
        catch (ArgumentException)
        {
        }
        catch (ExternalException)
        {
        }

        width = 0;
        height = 0;
        return false;
    }

    private static bool TryConvertDibToBmp(byte[] dibBytes, out byte[] bmpBytes)
    {
        const int fileHeaderSize = 14;
        bmpBytes = [];

        if (dibBytes.Length < 40)
        {
            return false;
        }

        bmpBytes = new byte[fileHeaderSize + dibBytes.Length];
        bmpBytes[0] = (byte)'B';
        bmpBytes[1] = (byte)'M';
        BitConverter.GetBytes(bmpBytes.Length).CopyTo(bmpBytes, 2);
        BitConverter.GetBytes(fileHeaderSize + 40).CopyTo(bmpBytes, 10);
        Buffer.BlockCopy(dibBytes, 0, bmpBytes, fileHeaderSize, dibBytes.Length);
        return true;
    }

    private static byte[]? TryReadClipboardBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = GlobalSize(handle);
        if (size == UIntPtr.Zero)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static T OpenClipboardScope<T>(Func<T> action)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return action();
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException("Unable to access the clipboard.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    private sealed record ClipboardSourceApplication(string? Name, string? Path, byte[]? IconBytes)
    {
        public static ClipboardSourceApplication Empty { get; } = new(null, null, null);
    }
}
