using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Clipthrough.Models;
using Clipthrough.Services;
using SkiaSharp;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-43. Every image path stores PNG and everything downstream assumes it
/// (export as .png, AI requests as image/png, drag-out as clip-N.png), but a
/// JPEG/GIF/WebP/BMP file dropped from disk was stored verbatim.
/// </summary>
public sealed class DragDropImageEncodingTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly string _dir = Directory.CreateTempSubdirectory("ct-drop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    private string Write(string name, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(new SKColor(200, 30, 60));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Theory]
    [InlineData("photo.jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData("anim.webp", SKEncodedImageFormat.Webp)]
    public void ADroppedNonPngImage_IsStoredAsPng(string name, SKEncodedImageFormat format)
    {
        var path = Write(name, format);
        Assert.False(File.ReadAllBytes(path).Take(8).SequenceEqual(Png), "fixture is already PNG");

        var request = DragDropService.TryBuildImageRequestFromFile(path, sourceInfo: null);

        Assert.NotNull(request);
        Assert.True(request!.ContentBytes!.Take(8).SequenceEqual(Png), "the stored bytes are not PNG");
        using var reread = SKBitmap.Decode(request.ContentBytes);
        Assert.Equal((4, 3), (reread.Width, reread.Height));
        Assert.Equal((4, 3), (request.ImageWidth, request.ImageHeight));
        Assert.Equal("photo.jpg" == name ? "photo" : "anim", request.ContentText);
    }

    [Fact]
    public void ADroppedPng_IsKeptByteForByte()
    {
        var path = Write("shot.png", SKEncodedImageFormat.Png);

        var request = DragDropService.TryBuildImageRequestFromFile(path, sourceInfo: null);

        Assert.Equal(File.ReadAllBytes(path), request!.ContentBytes);
    }

    [Fact]
    public void ADroppedFileThatIsNotAnImage_IsNotAnImageClip()
    {
        var path = Path.Combine(_dir, "fake.jpg");
        File.WriteAllText(path, "not an image");

        Assert.Null(DragDropService.TryBuildImageRequestFromFile(path, sourceInfo: null));
    }

    /// <summary>
    /// Rubber-duck F-09-2. The drop handler awaits this on the UI thread, and
    /// it used to read, decode and PNG-encode the dropped file before handing
    /// the task back, so a large photo froze the window for the whole encode.
    /// The returned task must still be running when the call returns; the
    /// image is big enough that the work cannot finish in that instant.
    /// </summary>
    [Fact]
    public async Task ADroppedNonPngImageFile_IsDecodedAndReEncodedOffTheCallingThread()
    {
        var path = WriteNoise("large.jpg", 1600, 1200);
        var drop = new DataTransfer();
        drop.Add(DataTransferItem.CreateFile(LocalFile(path)));

        var pending = new DragDropService().TryBuildCaptureRequestsAsync(drop, sourceInfo: null);

        Assert.False(
            pending.IsCompleted,
            "TryBuildCaptureRequestsAsync read, decoded and re-encoded the dropped image before returning, i.e. on the caller's (UI) thread");
        var request = Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(ContentType.Image, request.ContentType);
        Assert.True(request.ContentBytes!.Take(8).SequenceEqual(Png), "the stored bytes are not PNG");
        Assert.Equal((1600, 1200), (request.ImageWidth, request.ImageHeight));
        Assert.Equal("large", request.ContentText);
    }

    private string WriteNoise(string name, int width, int height)
    {
        var random = new Random(9);
        var pixels = new SKColor[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new SKColor((uint)random.Next() | 0xFF000000);
        }

        using var bitmap = new SKBitmap(width, height);
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    // IStorageFile cannot be implemented outside Avalonia, so borrow the
    // implementation Avalonia's own storage providers hand out for local files.
    private static IStorageFile LocalFile(string path)
    {
        var type = typeof(IStorageFile).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!;
        return (IStorageFile)Activator.CreateInstance(
            type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [new FileInfo(path)],
            culture: null)!;
    }
}
