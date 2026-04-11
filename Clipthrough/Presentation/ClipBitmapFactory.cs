using System;
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
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
