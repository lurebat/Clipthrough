using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using System.Reactive.Threading.Tasks;
using Avalonia.LogicalTree;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// "Copy as plain text" has to produce plain text. It read
/// <c>ClipItemViewModel.FullContent</c>, which is the raw *display* string, and
/// <c>GetRawContentDisplay</c> returns decoded HTML or RTF markup ahead of the
/// text field - so on any rich clip the one command whose entire purpose is to
/// strip formatting put markup on the clipboard.
/// (round 2, features-opus P4 / features-sol P3)
/// </summary>
/// <remarks>
/// The fixture makes the markup and the text disagree in every direction that
/// matters: different characters, different length, and the markup carries the
/// text inside it. A fixture where they agree - a clip whose HTML is just its
/// words - passes whichever field the code reads, which is the trap this
/// finding came with a warning about.
/// </remarks>
public sealed class CopyAsPlainTextHeadlessTests
{
    private const string Markup =
        "<html><body><!--StartFragment--><p style=\"color:red\"><b>Quarterly</b> revenue rose 12%.</p><!--EndFragment--></body></html>";

    private const string PlainText = "Quarterly revenue rose 12%.";

    private static void SelectRichClip(MainWindowTestHarness harness, string content)
    {
        var entry = new ClipEntry
        {
            Id = 1,
            Content = content,
            ContentBytes = Encoding.UTF8.GetBytes(Markup),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.Html,
            SourceApp = "Tests",
            Hash = "hash-rich-1",
        };

        var item = new ClipItemViewModel(entry);
        harness.ViewModel.Clips.Clear();
        harness.ViewModel.Clips.Add(item);
        harness.ViewModel.SelectedClip = item;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task CopyAsPlainText_OnARichClip_CopiesTheTextAndNotTheMarkup()
    {
        using var harness = MainWindowTestHarness.Create();
        SelectRichClip(harness, PlainText);

        // The premise: this clip really does render as markup, so the assertion
        // below is about which field was chosen and not about the fixture.
        Assert.Contains("<p style", harness.ViewModel.SelectedClip!.FullContent, System.StringComparison.Ordinal);

        await harness.ViewModel.CopySelectedAsPlainTextAsync();

        Assert.Equal(PlainText, harness.SystemInteraction.LastCopiedText);
        Assert.DoesNotContain("<", harness.SystemInteraction.LastCopiedText!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A rich clip with no text field still has to yield words rather than
    /// markup, so the fallback renders rather than copying FullContent through.
    /// </summary>
    [AvaloniaFact]
    public async Task CopyAsPlainText_OnARichClipWithNoTextField_RendersTheMarkup()
    {
        using var harness = MainWindowTestHarness.Create();
        SelectRichClip(harness, string.Empty);

        await harness.ViewModel.CopySelectedAsPlainTextAsync();

        var copied = harness.SystemInteraction.LastCopiedText;
        Assert.NotNull(copied);
        Assert.DoesNotContain("<", copied, System.StringComparison.Ordinal);
        Assert.Contains("Quarterly", copied, System.StringComparison.Ordinal);
        Assert.Contains("12%", copied, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The control. A plain clip must be unaffected, or "always render" would
    /// satisfy the tests above while mangling the ordinary case.
    /// </summary>
    [AvaloniaFact]
    public async Task CopyAsPlainText_OnAPlainClip_CopiesItUnchanged()
    {
        using var harness = MainWindowTestHarness.Create();
        var entry = new ClipEntry
        {
            Id = 2,
            Content = "a < b && c > d",
            ContentBytes = Encoding.UTF8.GetBytes("a < b && c > d"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
            Hash = "hash-plain-2",
        };

        var item = new ClipItemViewModel(entry);
        harness.ViewModel.Clips.Clear();
        harness.ViewModel.Clips.Add(item);
        harness.ViewModel.SelectedClip = item;
        Dispatcher.UIThread.RunJobs();

        await harness.ViewModel.CopySelectedAsPlainTextAsync();

        // Angle brackets that are the user's own text, not markup: a renderer
        // applied here would strip "< b && c >" as if it were a tag.
        Assert.Equal("a < b && c > d", harness.SystemInteraction.LastCopiedText);
    }

    /// <summary>
    /// The complaint was not only that the command was wrong but that it was
    /// unreachable: no menu item, no toolbar button, no context-menu entry. The
    /// only route to discovering it was a settings validation collision message.
    /// A correct command nobody can find is still not a feature.
    /// </summary>
    [AvaloniaFact]
    public void CopyAsPlainText_IsReachableFromTheEditMenuAndTheContextMenu()
    {
        using var harness = MainWindowTestHarness.Create();
        SelectRichClip(harness, PlainText);

        var expected = harness.ViewModel.CopySelectedAsPlainTextCommand;

        var inEditMenu = harness.Window.GetLogicalDescendants()
            .OfType<Avalonia.Controls.MenuItem>()
            .Any(item => ReferenceEquals(item.Command, expected));
        Assert.True(inEditMenu, "no Edit-menu item is bound to CopySelectedAsPlainTextCommand");

        var contextMenu = harness.ClipList.ContextMenu;
        Assert.NotNull(contextMenu);
        contextMenu!.Open(harness.ClipList);
        Dispatcher.UIThread.RunJobs();

        var inContextMenu = contextMenu.GetLogicalDescendants()
            .OfType<Avalonia.Controls.MenuItem>()
            .Any(item => ReferenceEquals(item.Command, expected));

        contextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(inContextMenu, "no context-menu item is bound to CopySelectedAsPlainTextCommand");
    }

    /// <summary>
    /// Exercises the command rather than the method, so the binding surface the
    /// menus use is what is under test.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCommand_StripsFormattingJustAsTheMethodDoes()
    {
        using var harness = MainWindowTestHarness.Create();
        SelectRichClip(harness, PlainText);

        await harness.ViewModel.CopySelectedAsPlainTextCommand.Execute().ToTask();

        Assert.Equal(PlainText, harness.SystemInteraction.LastCopiedText);
    }
}
