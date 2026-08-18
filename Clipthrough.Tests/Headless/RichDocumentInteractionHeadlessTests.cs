using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.Controls;
using Clipthrough.Localization;
using Clipthrough.Models;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The rendered preview has to be readable, not merely visible.
///
/// Replacing the WebView with a native renderer swapped one whole interaction
/// model for another, and the tests carried over only the half that was easy to
/// assert: that the markup turned into a document. Every test passed while the
/// pane quietly lost selection, the caret, the clipboard and the context menu -
/// because <c>RichTextViewer</c> exposes a document and nothing else, and
/// "the document is right" cannot tell you the user can do anything with it.
///
/// These assert the affordances instead of the content.
/// </summary>
public sealed class RichDocumentInteractionHeadlessTests
{
    /// <summary>
    /// Renders <paramref name="markup"/> and waits for the document path.
    /// </summary>
    /// <remarks>
    /// Import degrades to plain text after a three-second timeout. That is right
    /// for the product and awkward for a test: on a loaded host - a mutation
    /// sweep on the other cores, say - a tiny fragment can miss the deadline and
    /// take the fallback, and then every assertion about selection is answering
    /// the wrong question. Retrying is honest here because the contract under
    /// test is what the document path does; a run that never reached it has not
    /// been asked. Two consecutive misses is a real failure and says so.
    /// </remarks>
    private static async Task<(RichDocumentView View, Window Window)> RenderAsync(string markup)
    {
        for (var attempt = 1; ; attempt++)
        {
            var view = new RichDocumentView
            {
                ContentFormat = ClipContentFormat.Html,
                Markup = markup,
            };
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await view.PendingRender;

            if (view.Viewer.IsVisible || attempt == 3)
            {
                Assert.True(
                    view.Viewer.IsVisible,
                    $"the document path was not taken after {attempt} attempts, so nothing here says anything about selection");
                return (view, window);
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TheRenderedPreviewCanSelectItsText()
    {
        var (view, window) = await RenderAsync("<p>Hello <strong>selectable</strong> world</p>");
        try
        {

            var editorView = view.Viewer.View;
            Assert.NotNull(editorView);

            editorView.SelectAll();
            Dispatcher.UIThread.RunJobs();

            var selected = editorView.SelectedText();

            Assert.Contains("Hello", selected, StringComparison.Ordinal);
            Assert.Contains("selectable", selected, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Read-only is the intended state, and it has to be the kind of read-only
    /// that still lets a user take a copy. Asserting both together is the point:
    /// the pane that shipped was read-only in the sense of being inert.
    /// </summary>
    [AvaloniaFact]
    public async Task TheRenderedPreviewIsReadOnlyButNotInert()
    {
        var (view, window) = await RenderAsync("<p>Hello world</p>");
        try
        {
            Assert.True(view.Viewer.IsReadOnly);

            var editorView = view.Viewer.View;
            Assert.NotNull(editorView);

            editorView.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrEmpty(editorView.SelectedText()));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Selecting text must not offer to format it. The pane is read-only, so
    /// every button on the selection toolbar - bold, lists, colour - is an
    /// action it will refuse, and popping it up on selection promises an edit
    /// that cannot happen.
    /// </summary>
    /// <remarks>
    /// VellumText enables the toolbar by default and does not gate it on
    /// IsReadOnly, so this has to be switched off here rather than inherited.
    /// Asserting it keeps the intent attached to the reason: a later upgrade
    /// that changed the default would otherwise silently reintroduce it.
    /// </remarks>
    [AvaloniaFact]
    public async Task SelectingTextDoesNotOfferAFormattingToolbar()
    {
        var (view, window) = await RenderAsync("<p>Hello selectable world</p>");
        try
        {
            Assert.True(view.Viewer.IsReadOnly);
            Assert.False(view.Viewer.IsSelectionToolbarEnabled);

            var editorView = view.Viewer.View;
            Assert.NotNull(editorView);
            editorView.SelectAll();
            Dispatcher.UIThread.RunJobs();

            // Selecting is still expected to work; it is only the formatting
            // offer that is unwanted.
            Assert.False(string.IsNullOrEmpty(editorView.SelectedText()));
            Assert.False(view.Viewer.IsSelectionToolbarEnabled);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
    /// <summary>
    /// The context menu offers only what a read-only pane can actually do.
    /// </summary>
    /// <remarks>
    /// VellumText's built-in menu includes Cut. The operation is safely gated -
    /// CutAsync leaves the document untouched while IsReadOnly is set, verified
    /// separately - so this is about what is advertised rather than about data
    /// loss. A menu item that does nothing when clicked reads as a broken
    /// application. The final assertion is the load-bearing one: it fails if a
    /// VellumText upgrade reinstates a menu with mutating entries.
    /// </remarks>
    [AvaloniaFact]
    public async Task TheContextMenuOffersNothingTheReadOnlyPaneWouldRefuse()
    {
        var (view, window) = await RenderAsync("<p>Hello selectable world</p>");
        try
        {
            var menu = view.Viewer.ContextMenu;
            Assert.NotNull(menu);

            var headers = new List<string>();
            foreach (var item in menu!.Items)
            {
                if (item is MenuItem menuItem)
                {
                    headers.Add(menuItem.Header?.ToString() ?? string.Empty);
                }
            }

            Assert.NotEmpty(headers);
            Assert.Contains(AppText.PreviewCopySelection, headers);
            Assert.Contains(AppText.PreviewSelectAll, headers);

            Assert.DoesNotContain(headers, h => h.Contains("Cut", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(headers, h => h.Contains("Paste", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(headers, h => h.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
    /// <summary>
    /// The degraded path too. An oversized or unbreakable clip falls back to
    /// plain text, and those are exactly the clips a user wants a piece of
    /// rather than the whole of, so the fallback cannot be an inert TextBlock.
    /// </summary>
    /// <remarks>
    /// No Window here, matching <c>RichDocumentView_WithAnOversizedPayload_FallsBackToBoundedText</c>
    /// and for the same reason: showing one drags in text layout, and laying out
    /// a long unbreakable run is the cost the fallback exists to bound, not a
    /// thing to measure inside an assertion about selectability. Writing this
    /// test with a Window first is how I rediscovered that - it hung for minutes
    /// on content the control had already handled correctly in under a
    /// millisecond.
    /// </remarks>
    [AvaloniaFact]
    public async Task TheFallbackForAnUnbreakableClipIsStillSelectable()
    {
        var view = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>" + new string('A', 40_000) + "</p>",
        };

        await view.PendingRender;

        Assert.True(view.FallbackText.IsVisible, "expected the unbreakable clip to take the fallback path");
        Assert.IsAssignableFrom<SelectableTextBlock>(view.FallbackText);
        Assert.False(string.IsNullOrEmpty(view.FallbackText.Text));
    }
}
