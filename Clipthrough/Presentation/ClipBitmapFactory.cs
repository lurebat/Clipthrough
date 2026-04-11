using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;

namespace Clipthrough.Presentation;

public static class ClipBitmapFactory
{
    public static Bitmap? TryLoad(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"Bitmap decode failed: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"Bitmap decode failed: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Trace.TraceWarning($"Bitmap decode failed: {ex.Message}");
            return null;
        }
    }
}
