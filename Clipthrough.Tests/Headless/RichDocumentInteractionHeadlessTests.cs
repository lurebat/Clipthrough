using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;

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
        var view = new RichDocumentView
        {
            // A minute instead of three seconds. Not because import is slow -
            // these fragments parse in under a millisecond - but because the
            // production timeout is wall-clock, and a machine busy with a
            // mutation sweep made a dozen words miss it. The pane then fell back
            // to plain text and every assertion below failed for a reason that
            // had nothing to do with what was being tested. This makes the test
            // independent of how loaded the machine is rather than lucky.
            ImportTimeout = TimeSpan.FromMinutes(1),
            ContentFormat = ClipContentFormat.Html,
            Markup = markup,
        };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await view.PendingRender;

        Assert.True(
            view.Viewer.IsVisible,
            "the document path was not taken, so nothing here says anything about selection");

        // Lay out again now that the editor is visible.
        //
        // It is created with IsVisible = false and only revealed once the async
        // render finishes, and an invisible control is never measured - so its
        // template has not been applied and RichTextEditor.View is still null.
        // Whether anything else happened to trigger a layout pass in between
        // varied with what ran before, which is why this passed alone and failed
        // in a full run rather than failing honestly every time.
        view.Viewer.Measure(new Size(800, 600));
        view.Viewer.Arrange(new Rect(0, 0, 800, 600));
        Dispatcher.UIThread.RunJobs();

        return (view, window);
    }

    private static async Task<string> ReadClipboardTextAsync(IClipboard clipboard)
    {
        using var data = await clipboard.TryGetDataAsync();
        return data is null ? "<no data>" : await data.TryGetValueAsync(DataFormat.Text) ?? "<no text>";
    }

    private static async Task WriteClipboardTextAsync(IClipboard clipboard, string text)
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    /// <summary>
    /// Ctrl+X must not reach the editor, because on a read-only control Cut is
    /// not a no-op: VellumText copies before it gates the delete, so it silently
    /// behaves as Copy.
    /// </summary>
    /// <remarks>
    /// Measured before the fix: the clipboard went from the sentinel to the
    /// preview's own text while the document stayed put. Dropping Cut from the
    /// context menu had removed the entry but left the shortcut, and in a
    /// clipboard manager the shortcut is the half that costs something - it
    /// discards whatever the user was holding and the monitor captures the
    /// replacement as a clip they never copied.
    ///
    /// Asserting the clipboard rather than the document is the point. The
    /// document is unchanged either way, so any assertion about it passes
    /// against the bug.
    /// </remarks>
    [AvaloniaFact]
    public async Task CtrlXOnTheRenderedPreview_LeavesTheClipboardAlone()
    {
        var (view, window) = await RenderAsync("<p>PREVIEW CONTENTS</p>");
        try
        {
            var editorView = view.Viewer.View;
            Assert.NotNull(editorView);
            editorView.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrEmpty(editorView.SelectedText()), "nothing selected, so Cut had nothing to take");

            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await WriteClipboardTextAsync(clipboard, "USER HELD THIS");
            Assert.Equal("USER HELD THIS", await ReadClipboardTextAsync(clipboard));

            editorView.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editorView.IsKeyboardFocusWithin, "the editor never took focus, so the key never reached it");

            window.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("USER HELD THIS", await ReadClipboardTextAsync(clipboard));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
    /// <summary>
    /// Of every clipboard-adjacent shortcut, only Ctrl+C may write the clipboard
    /// from the preview. Asserts the ones that must NOT, which is the half a
    /// test of Ctrl+C alone cannot cover.
    /// </summary>
    /// <remarks>
    /// Written after gating Ctrl+X, to check that gate was not itself the
    /// partial fix it was warning about: Shift+Delete and Ctrl+Insert are the
    /// classic synonyms for Cut and Copy, and had VellumText bound either, a fix
    /// aimed at one key would have left the bug reachable by another. Measured:
    /// it binds neither, so the single gate is complete today.
    ///
    /// The value of this test is the day that changes. It is a tripwire for
    /// moving off 0.4.1 - a release that binds a Cut synonym, or routes Delete
    /// through a copy, fails here rather than silently restoring a clipboard
    /// write we already paid to find once.
    /// </remarks>
    [AvaloniaFact]
    public async Task OnlyCopyMayWriteTheClipboardFromTheRenderedPreview()
    {
        var mustNotWrite = new (string Name, PhysicalKey Key, RawInputModifiers Modifiers)[]
        {
            ("Ctrl+X", PhysicalKey.X, RawInputModifiers.Control),
            ("Shift+Delete", PhysicalKey.Delete, RawInputModifiers.Shift),
            ("Ctrl+Insert", PhysicalKey.Insert, RawInputModifiers.Control),
            ("Shift+Insert", PhysicalKey.Insert, RawInputModifiers.Shift),
            ("Ctrl+V", PhysicalKey.V, RawInputModifiers.Control),
            ("Delete", PhysicalKey.Delete, RawInputModifiers.None),
            ("Backspace", PhysicalKey.Backspace, RawInputModifiers.None),
        };

        foreach (var (name, key, modifiers) in mustNotWrite)
        {
            var (view, window) = await RenderAsync("<p>PREVIEW CONTENTS</p>");
            try
            {
                var editorView = view.Viewer.View;
                Assert.NotNull(editorView);
                editorView.SelectAll();
                editorView.Focus();
                Dispatcher.UIThread.RunJobs();

                // Without a selection Cut has nothing to take and every case
                // below would pass for the wrong reason.
                Assert.False(string.IsNullOrEmpty(editorView.SelectedText()), $"{name}: nothing was selected");
                Assert.True(editorView.IsKeyboardFocusWithin, $"{name}: the editor never took focus");

                var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
                await WriteClipboardTextAsync(clipboard, "USER HELD THIS");

                window.KeyPressQwerty(key, modifiers);
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(120);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("USER HELD THIS", await ReadClipboardTextAsync(clipboard));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        // The control: Ctrl+C must still work, or the loop above would pass just
        // as well against a pane with no clipboard access at all.
        var (copyView, copyWindow) = await RenderAsync("<p>PREVIEW CONTENTS</p>");
        try
        {
            var editorView = copyView.Viewer.View!;
            editorView.SelectAll();
            editorView.Focus();
            Dispatcher.UIThread.RunJobs();

            var clipboard = TopLevel.GetTopLevel(copyWindow)!.Clipboard!;
            await WriteClipboardTextAsync(clipboard, "USER HELD THIS");

            copyWindow.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("PREVIEW CONTENTS", await ReadClipboardTextAsync(clipboard), StringComparison.Ordinal);
        }
        finally
        {
            copyWindow.Close();
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
