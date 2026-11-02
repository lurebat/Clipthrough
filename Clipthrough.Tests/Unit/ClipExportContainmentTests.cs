using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-33. The export folder and file names come from the clip's own first line,
/// and only SanitizePathSegment stands between that text and the file system.
/// The real ClipExportService was never constructed by a test (headless tests
/// use a fake), so disabling the sanitiser survived every test.
/// </summary>
public sealed class ClipExportContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ct-export-root-" + Guid.NewGuid().ToString("N"), "Exports");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* test cleanup */ }
    }

    private static ClipEntry Text(string content) => new()
    {
        Id = 7,
        Content = content,
        ContentBytes = Encoding.UTF8.GetBytes(content),
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        Hash = "h",
        FirstCopiedAt = DateTimeOffset.UtcNow,
        LastCopiedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(@"..\..\escaped")]
    [InlineData(@"C:\x:y*?")]
    [InlineData("../../escaped")]
    public async Task AClipsFirstLine_CannotChooseWhereTheExportIsWritten(string firstLine)
    {
        var service = new ClipExportService(_root);
        var clip = Text(firstLine + "\nsecond line");

        var result = await service.ExportAsync(clip);

        var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        var written = Directory.GetFiles(Path.GetDirectoryName(_root)!, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(written);
        Assert.All(written, f => Assert.StartsWith(root, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(root, Path.GetFullPath(result.ExportDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(Path.GetDirectoryName(result.ExportDirectory)!));
        Assert.DoesNotContain(Path.GetFileName(result.ExportDirectory), c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public async Task ATextClip_WritesItsPayloadRenderingAndMetadata()
    {
        var service = new ClipExportService(_root);
        var clip = Text("hello export\nline two");

        var result = await service.ExportAsync(clip);

        var files = Directory.GetFiles(result.ExportDirectory).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["hello export-rendered.txt", "hello export.txt", "metadata.json"], files);
        Assert.Equal("hello export\nline two", File.ReadAllText(Path.Combine(result.ExportDirectory, "hello export.txt")));
        Assert.Equal(Path.Combine(result.ExportDirectory, "hello export-rendered.txt"), result.PrimaryPath);
    }

    [Fact]
    public async Task AnImageClip_WritesItsBytesVerbatim()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var service = new ClipExportService(_root);
        var clip = new ClipEntry
        {
            Id = 8,
            Content = "Image 2 x 2",
            ContentBytes = png,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            Hash = "h",
            FirstCopiedAt = DateTimeOffset.UtcNow,
            LastCopiedAt = DateTimeOffset.UtcNow,
        };

        var result = await service.ExportAsync(clip);

        var image = Assert.Single(Directory.GetFiles(result.ExportDirectory, "*.png"));
        Assert.Equal(png, File.ReadAllBytes(image));
    }
}