using System;
using System.IO;
using System.Linq;
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
}