using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.Controls;
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
    private static async Task<(RichDocumentView View, Window Window)> RenderAsync(string markup)
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
        return (view, window);
    }

    [AvaloniaFact]
    public async Task TheRenderedPreviewCanSelectItsText()
    {
        var (view, window) = await RenderAsync("<p>Hello <strong>selectable</strong> world</p>");
        try
        {
            view.Viewer.View.SelectAll();
            Dispatcher.UIThread.RunJobs();

            var selected = view.Viewer.View.SelectedText();

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

            view.Viewer.View.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrEmpty(view.Viewer.View.SelectedText()));
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
