using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// The global paste hotkeys must put the clip on the clipboard in the format it
/// was captured in, and must say so honestly when they could not.
/// </summary>
/// <remarks>
/// Both halves are load-bearing and both were broken.
///
/// The format half: the hotkeys copied <c>clip.Content</c> as plain text
/// whatever the clip was. An image clip's Content is the short text label stored
/// beside the bytes - in the reporting user's library those run 17 to 121
/// characters - so Ctrl+V pasted a caption instead of the picture, and rich text
/// arrived stripped of its formatting.
///
/// The honesty half is worse: <c>PasteAndDelete</c> deleted the clip whether or
/// not the copy happened. An image whose bytes could not be read pasted whatever
/// the user already had on the clipboard and then destroyed the only copy of the
/// image.
///
/// AvaloniaFact rather than Fact: decoding a bitmap needs the platform up.
/// </remarks>
public sealed class ClipClipboardWriterTests
{
    private const int TinyPngLength = 70;

    /// <summary>A real 1x1 PNG - the headless platform does not truly decode, so
    /// garbage bytes would "succeed" and prove nothing.</summary>
    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

    private static ClipEntry Clip(ContentType type, string content, byte[]? bytes = null, ClipContentFormat format = ClipContentFormat.PlainText) => new()
    {
        Id = 1,
        Content = content,
        ContentBytes = bytes ?? Encoding.UTF8.GetBytes(content),
        ContentType = type,
        ContentFormat = format,
        SourceApp = "Tests",
        Hash = "h1",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    };

    private static (TestSystemInteractionService Interaction, TestClipboardMonitorService Monitor) Fresh()
        => (new TestSystemInteractionService(), new TestClipboardMonitorService());

    /// <summary>
    /// The reported defect: an image must reach the clipboard as a picture, not
    /// as the text label stored beside it.
    /// </summary>
    [AvaloniaFact]
    public async Task AnImageClipIsCopiedAsAnImage_NotAsItsTextLabel()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Image, "Screenshot 1920x1080", PngBytes(), ClipContentFormat.Bitmap);

        Assert.True(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Equal(1, interaction.BitmapCopyCount);
        Assert.Null(interaction.LastCopiedText);
        Assert.Equal(1, monitor.PendingSuppressions);
    }

    /// <summary>
    /// The data-loss half. An image the writer cannot read must leave the
    /// clipboard alone and say so, because the caller deletes on a true.
    /// </summary>
    /// <remarks>
    /// Driven by the size ceiling rather than by corrupt bytes: the headless
    /// platform does not really decode, so garbage bytes copy "successfully" and
    /// a test built on them asserts nothing. The ceiling is checked before the
    /// decode, so it is deterministic everywhere.
    /// </remarks>
    [AvaloniaFact]
    public async Task AnImageTooLargeToRead_WritesNothingAndReportsFailure()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Image, "Screenshot 1920x1080", PngBytes(), ClipContentFormat.Bitmap);

        Assert.False(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: TinyPngLength - 1));

        Assert.Equal(0, interaction.BitmapCopyCount);
        Assert.Null(interaction.LastCopiedText);

        // The gate is one-shot: an arm left pending here would be spent on
        // whatever the user copies next, which would then be missing from
        // their history.
        Assert.Equal(0, monitor.PendingSuppressions);
    }

    /// <summary>An image with no bytes at all is the same contract.</summary>
    [AvaloniaFact]
    public async Task AnImageWithNoBytes_WritesNothingAndReportsFailure()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Image, "Screenshot 1920x1080", Array.Empty<byte>(), ClipContentFormat.Bitmap);

        Assert.False(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Equal(0, interaction.BitmapCopyCount);
        Assert.Null(interaction.LastCopiedText);
        Assert.Equal(0, monitor.PendingSuppressions);
    }

    /// <summary>Rich text keeps its markup rather than being flattened.</summary>
    [AvaloniaFact]
    public async Task ARichTextClipKeepsItsFormatting()
    {
        var (interaction, monitor) = Fresh();
        const string html = "<p>hello <b>world</b></p>";
        var clip = Clip(ContentType.RichText, html, format: ClipContentFormat.Html);

        Assert.True(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Equal(html, interaction.LastCopiedRichContent);
        Assert.Equal(ClipContentFormat.Html, interaction.LastCopiedRichContentFormat);

        // The plain-text alternative has to be the rendered text, not the markup,
        // or a plain-text-only target pastes angle brackets.
        Assert.DoesNotContain("<b>", interaction.LastCopiedRichPlainText, StringComparison.Ordinal);
        Assert.Contains("world", interaction.LastCopiedRichPlainText!, StringComparison.Ordinal);
        Assert.Null(interaction.LastCopiedText);
        Assert.Equal(1, monitor.PendingSuppressions);
    }

    /// <summary>
    /// The control. Plain text must still go as plain text, or "never write
    /// anything" would satisfy the failure tests above.
    /// </summary>
    [AvaloniaFact]
    public async Task APlainTextClipIsCopiedAsText()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Text, "just some text");

        Assert.True(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Equal("just some text", interaction.LastCopiedText);
        Assert.Equal(0, interaction.BitmapCopyCount);
        Assert.Equal(1, monitor.PendingSuppressions);
    }

    /// <summary>A file clip's content is already the paths, so it goes as text.</summary>
    [AvaloniaFact]
    public async Task AFileClipIsCopiedAsItsPaths()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Files, @"C:\a\one.txt" + "\n" + @"C:\a\two.txt");

        Assert.True(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Contains("one.txt", interaction.LastCopiedText!, StringComparison.Ordinal);
        Assert.Contains("two.txt", interaction.LastCopiedText!, StringComparison.Ordinal);
        Assert.Equal(1, monitor.PendingSuppressions);
    }

    /// <summary>
    /// A write that throws must not leave a suppression armed.
    /// </summary>
    /// <remarks>
    /// The clipboard can be locked by another application, and Avalonia surfaces
    /// that as an exception. Arming has to happen before the write to win the
    /// race against the change notification, so a throw leaves the arm pending -
    /// and an arm that is never consumed is spent on whatever the user copies
    /// next, which then vanishes from their history with nothing to explain it.
    ///
    /// The gate's expiry window bounds this to about two seconds by itself. This
    /// closes it outright for the one case the writer can be certain about.
    /// </remarks>
    [AvaloniaFact]
    public async Task AWriteThatThrows_LeavesNoSuppressionArmed()
    {
        var (interaction, monitor) = Fresh();
        interaction.ThrowOnCopy = new InvalidOperationException("clipboard locked");
        var clip = Clip(ContentType.Text, "some text");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Equal(0, monitor.PendingSuppressions);
    }

    /// <summary>Empty content is nothing to write, and must not read as success.</summary>
    [AvaloniaFact]
    public async Task AnEmptyTextClip_WritesNothingAndReportsFailure()
    {
        var (interaction, monitor) = Fresh();
        var clip = Clip(ContentType.Text, string.Empty);

        Assert.False(await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes: null));

        Assert.Null(interaction.LastCopiedText);
        Assert.Equal(0, monitor.PendingSuppressions);
    }
}
