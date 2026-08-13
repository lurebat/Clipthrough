using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;

namespace Clipthrough.Presentation;

public static class ClipBitmapFactory
{
    public static Bitmap? TryLoad(byte[]? bytes) => TryLoad(bytes, decodeWidth: null);

    /// <summary>
    /// Decodes an image, optionally at a reduced width.
    /// </summary>
    /// <param name="decodeWidth">
    /// When set, the image is decoded scaled to this width instead of at full resolution.
    /// A decoded bitmap costs width x height x 4 bytes regardless of how small it is drawn,
    /// so a 4000x3000 screenshot rendered into an 84x48 row thumbnail otherwise holds ~48 MB
    /// resident per row. Callers must only pass this when the source is known to be wider -
    /// the decoder scales to exactly this width, so a smaller source would be upscaled,
    /// costing more memory than a plain decode and blurring the result.
    /// </param>
    public static Bitmap? TryLoad(byte[]? bytes, int? decodeWidth)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return decodeWidth is > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth.Value, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
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
