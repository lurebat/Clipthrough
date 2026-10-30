using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// The global step-paste and custom-transform hotkeys on an image clip (F-12).
/// </summary>
/// <remarks>
/// An image clip's text content is not a caption the user wrote: it is the
/// title of the window that was in front when the image was copied. Live, the
/// custom hotkey pasted that title uppercased into the user's document, and
/// step-paste pasted it verbatim and marked the image pasted. A title like
/// the one below carries a subject line into whatever the user is typing, so
/// these tests assert it never reaches the clipboard.
///
/// AvaloniaFact: decoding the bitmap for the step-paste copy needs the platform.
/// </remarks>
public sealed class HotkeyClipActionsTests
{
    private const string WindowTitle = "Q3 layoffs - Outlook";

    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

    private static async Task<ClipEntry> CaptureImageAsync(TemporaryDatabaseScope scope)
        => (await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = PngBytes(),
            ContentText = WindowTitle,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ImageWidth = 1,
            ImageHeight = 1,
            SourceApp = "Tests",
        }))!;

    private static async Task<ClipEntry> CaptureTextAsync(TemporaryDatabaseScope scope, string text)
        => (await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(text),
            ContentText = text,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
        }))!;

    [AvaloniaFact]
    public async Task CustomTransformHotkey_OnAnImageClip_LeavesTheClipboardAloneAndPastesNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var image = await CaptureImageAsync(scope);
        var interaction = new TestSystemInteractionService();
        var monitor = new TestClipboardMonitorService();

        var copied = await HotkeyClipActions.TransformAndCopyAsync(
            image,
            CustomHotkeyTarget.Parse("builtin:UpperCase"),
            "Ctrl+Alt+U",
            scope.ClipStoreService,
            interaction,
            monitor,
            ai: null,
            presets: Array.Empty<AiPreset>());

        Assert.False(copied, "A paste would follow a transform of the image's window title.");
        Assert.Null(interaction.LastCopiedText);
        Assert.Null(interaction.LastCopiedRichContent);
        Assert.Equal(0, monitor.PendingSuppressions);

        var all = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Single(all.Items);
    }

    [AvaloniaFact]
    public async Task CustomTransformHotkey_OnATextClip_StillTransformsAndCopies()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var text = await CaptureTextAsync(scope, "make me loud");
        var interaction = new TestSystemInteractionService();
        var monitor = new TestClipboardMonitorService();

        var copied = await HotkeyClipActions.TransformAndCopyAsync(
            text,
            CustomHotkeyTarget.Parse("builtin:UpperCase"),
            "Ctrl+Alt+U",
            scope.ClipStoreService,
            interaction,
            monitor,
            ai: null,
            presets: Array.Empty<AiPreset>());

        Assert.True(copied);
        Assert.Equal("MAKE ME LOUD", interaction.LastCopiedText);
    }

    /// <summary>
    /// Live check C3: a builtin:HtmlToMarkdown hotkey on a rich clip copied from a
    /// browser transformed its plain text. The hotkey takes the selected list row
    /// when the window is open, and list rows carry no markup (it is read lazily),
    /// so the transform has to fetch it.
    /// </summary>
    [AvaloniaFact]
    public async Task HtmlToMarkdownHotkey_OnARichClipFromTheList_ConvertsItsHtml()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await RichBrowserClip.CaptureAsync(scope.ClipStoreService);
        var listRow = Assert.Single((await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items);
        Assert.Null(listRow.ContentBytes);
        var interaction = new TestSystemInteractionService();

        var copied = await HotkeyClipActions.TransformAndCopyAsync(
            listRow,
            CustomHotkeyTarget.Parse("builtin:HtmlToMarkdown"),
            "Ctrl+Alt+H",
            scope.ClipStoreService,
            interaction,
            new TestClipboardMonitorService(),
            ai: null,
            presets: Array.Empty<AiPreset>());

        Assert.True(copied);
        Assert.Contains("**bold**", interaction.LastCopiedText, StringComparison.Ordinal);
        Assert.DoesNotContain("<", interaction.LastCopiedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rubber-duck F-16-2. A Markdown -&gt; HTML result is stored as a bare HTML
    /// fragment. Converting it back must not read text that looks like a CF_HTML
    /// header field as offsets into it: that copied <c>&lt;p&gt;Before S</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task HtmlToMarkdownHotkey_OnAMarkdownToHtmlResult_KeepsTextThatLooksLikeAHeader()
    {
        const string text = "Before StartFragment:0000000000 EndFragment:0000000011 after";
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var source = await CaptureTextAsync(scope, text);
        var interaction = new TestSystemInteractionService();
        Assert.True(await HotkeyClipActions.TransformAndCopyAsync(
            source,
            CustomHotkeyTarget.Parse("builtin:MarkdownToHtml"),
            "Ctrl+Alt+M",
            scope.ClipStoreService,
            interaction,
            new TestClipboardMonitorService(),
            ai: null,
            presets: Array.Empty<AiPreset>()));
        var html = Assert.Single(
            (await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items,
            static clip => clip.ContentFormat == ClipContentFormat.Html);

        var copied = await HotkeyClipActions.TransformAndCopyAsync(
            html,
            CustomHotkeyTarget.Parse("builtin:HtmlToMarkdown"),
            "Ctrl+Alt+H",
            scope.ClipStoreService,
            interaction,
            new TestClipboardMonitorService(),
            ai: null,
            presets: Array.Empty<AiPreset>());

        Assert.True(copied);
        Assert.Equal(text, interaction.LastCopiedText?.Trim());
    }

    [AvaloniaFact]
    public async Task StepPaste_OnAnImageClip_CopiesThePictureNotTheWindowTitle()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureImageAsync(scope);
        var interaction = new TestSystemInteractionService();
        var monitor = new TestClipboardMonitorService();

        var outcome = await HotkeyClipActions.CopyClipAtOffsetAsync(
            0, scope.ClipStoreService, interaction, monitor, maxImageBytes: null);

        Assert.Equal(HotkeyClipActions.StepPasteOutcome.Copied, outcome);
        Assert.Equal(1, interaction.BitmapCopyCount);
        Assert.Null(interaction.LastCopiedText);
    }

    /// <summary>
    /// A clip that cannot reach the clipboard must not be marked pasted, and
    /// the caller must not send the keystroke: it would paste whatever the user
    /// had on the clipboard before.
    /// </summary>
    [AvaloniaFact]
    public async Task StepPaste_WhenTheImageCannotBeCopied_DoesNotMarkItPasted()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var image = await CaptureImageAsync(scope);
        var interaction = new TestSystemInteractionService();
        var monitor = new TestClipboardMonitorService();

        var outcome = await HotkeyClipActions.CopyClipAtOffsetAsync(
            0, scope.ClipStoreService, interaction, monitor, maxImageBytes: 1);

        Assert.Equal(HotkeyClipActions.StepPasteOutcome.NotCopied, outcome);
        Assert.Null(interaction.LastCopiedText);
        Assert.Equal(0, interaction.BitmapCopyCount);
        Assert.Equal(0, monitor.PendingSuppressions);
        var stored = await scope.ClipStoreService.GetByIdAsync(image.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.LastPastedAt);
    }

    [AvaloniaFact]
    public async Task StepPaste_PastTheEnd_ReportsNoClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureTextAsync(scope, "only one");

        var outcome = await HotkeyClipActions.CopyClipAtOffsetAsync(
            1, scope.ClipStoreService, new TestSystemInteractionService(), new TestClipboardMonitorService(), maxImageBytes: null);

        Assert.Equal(HotkeyClipActions.StepPasteOutcome.NoClip, outcome);
    }
}
