using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using VellumText;
using VellumText.Avalonia;
using VellumText.Interop.Html;
using VellumText.Interop.Rtf;

namespace Clipthrough.Controls;

/// <summary>
/// Renders a rich text clip natively, replacing the WebView this used to run in.
/// </summary>
/// <remarks>
/// The WebView rendered clip markup in a real browser with a policy that permitted
/// <c>img-src</c>, <c>style-src</c>, <c>font-src</c> and <c>connect-src</c> over http(s).
/// Previewing a clip therefore fetched whatever the document named: copy a marketing
/// email, click it in the history, and its tracking pixels fire - the sender learns when
/// you looked at your own clipboard, from the one tool whose whole job is holding what
/// you copied. <c>file:</c> was permitted too, so a crafted clip could make the preview
/// read a local path.
///
/// VellumText resolves only pixels carried inside the document (<c>data:</c> URLs) and has no
/// setting that turns network fetching on at all, so the leak is closed by construction
/// rather than by policy.
/// </remarks>
public sealed class RichDocumentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty =
        AvaloniaProperty.Register<RichDocumentView, string?>(nameof(Markup));

    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty =
        AvaloniaProperty.Register<RichDocumentView, ClipContentFormat>(nameof(ContentFormat));

    // Importing is linear in the payload and runs off the UI thread, but a pathological
    // clip should not spend seconds there before we give up on it. Past this size the
    // preview degrades to plain text, which is what the WebView did too.
    private const int MaxImportSizeChars = 512 * 1024;

    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(3);

    // Avalonia's line breaking is quadratic in the length of a run that offers no break
    // opportunity, because the cost is characters x lines and an unbreakable run wraps
    // once per line-width. Measured upstream at width 400: 20,000 chars 217 ms, 40,000
    // chars 801 ms, 80,000 chars 3,047 ms - a 3.86x rise per doubling. The same 80,000
    // characters as ordinary words cost 97 ms.
    //
    // This is not hypothetical for a clipboard manager: a copied base64 blob, a minified
    // script or a long URL is exactly one enormous unbreakable run, and at a few hundred
    // thousand characters that curve reaches minutes of frozen UI. The size cap above
    // does not catch it, because such a clip is comfortably under it.
    //
    // So a document is only built when the content actually has break opportunities;
    // otherwise it takes the same bounded plain-text path as an oversized clip. 10,000
    // chars sits an order of magnitude below where the curve turns painful.
    private const int MaxUnbrokenRunChars = 10_000;

    private static int LongestUnbrokenRun(string content)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c))
            {
                current = 0;
                continue;
            }

            current++;
            if (current > longest)
            {
                longest = current;
            }
        }

        return longest;
    }

    private readonly RichTextEditor _viewer;
    private readonly TextBlock _emptyState;
    private readonly SelectableTextBlock _fallbackContent;

    // Rendering is asynchronous, so a test that only drained the dispatcher would assert
    // against whatever happened to be there. Exposing the in-flight render lets a test
    // await the actual completion instead of sleeping and hoping.
    private Task _pendingRender = Task.CompletedTask;

    internal Task PendingRender => _pendingRender;

    internal RichTextEditor Viewer => _viewer;

    internal SelectableTextBlock FallbackText => _fallbackContent;

    internal TextBlock EmptyState => _emptyState;

    // Incremented per render so a slow import cannot overwrite the document belonging to
    // a clip the user has since moved off. Arrowing through the list starts a render per
    // keypress, so out-of-order completion is the normal case, not the rare one.
    private int _renderGeneration;

    public RichDocumentView()
    {
        // An editor rather than a viewer, and read-only rather than inert. The
        // viewer exposes a Document and nothing else - no selection, no caret,
        // no clipboard, no context menu - so choosing it did not merely defer
        // editing, it removed the ability to select a sentence and copy it,
        // which the WebView it replaced had always allowed.
        //
        // The selection toolbar is switched off explicitly. It is a formatting
        // toolbar - bold, lists, colour - and every button on it is inapplicable
        // while IsReadOnly is set, so offering it on selection promises an edit
        // the pane will not accept. VellumText enables it by default and does
        // not gate it on IsReadOnly; reported upstream, and this line stays
        // either way because the pane would not want it even when editing lands.
        _viewer = new RichTextEditor
        {
            IsReadOnly = true,
            IsSelectionToolbarEnabled = false,
            IsVisible = false,
            Margin = new Thickness(12),
        };

        _viewer.ContextMenu = BuildReadOnlyContextMenu(_viewer);

        _emptyState = new TextBlock
        {
            Text = AppText.PreviewEmptyRichTextData,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(20),
        };

        // Selectable too: the fallback is what an oversized or unbreakable clip
        // degrades to, and those are exactly the clips worth copying a piece of
        // rather than the whole thing.
        _fallbackContent = new SelectableTextBlock
        {
            IsVisible = false,
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new Grid();
        root.Children.Add(_viewer);
        root.Children.Add(_fallbackContent);
        root.Children.Add(_emptyState);
        Content = new ScrollViewer { Content = root };

        this.GetObservable(MarkupProperty).Subscribe(_ => StartRender());
        this.GetObservable(ContentFormatProperty).Subscribe(_ => StartRender());
    }

    /// <summary>
    /// A context menu that offers only what a read-only pane can do.
    /// </summary>
    /// <remarks>
    /// VellumText's built-in menu includes Cut, which a read-only editor cannot
    /// perform. The operations themselves are safely gated - CutAsync,
    /// DeleteSelection and InsertText all leave the document untouched while
    /// IsReadOnly is set, so nothing here could corrupt a clip - but offering
    /// them is still wrong: a menu item that does nothing when clicked reads as
    /// a broken application rather than as a disabled feature. Reported upstream
    /// as a default that should follow IsReadOnly.
    /// </remarks>
    private static ContextMenu BuildReadOnlyContextMenu(RichTextEditor editor)
    {
        var copy = new MenuItem { Header = AppText.PreviewCopySelection };
        copy.Click += async (_, _) =>
        {
            if (editor.View is { } view)
            {
                await view.CopyAsync();
            }
        };

        var selectAll = new MenuItem { Header = AppText.PreviewSelectAll };
        selectAll.Click += (_, _) => editor.View?.SelectAll();

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        return menu;
    }
    public string? Markup
    {
        get => GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    public ClipContentFormat ContentFormat
    {
        get => GetValue(ContentFormatProperty);
        set => SetValue(ContentFormatProperty, value);
    }

    private void StartRender() => _pendingRender = RenderContentAsync();

    private async Task RenderContentAsync()
    {
        var generation = Interlocked.Increment(ref _renderGeneration);
        var content = Markup;
        var format = ContentFormat;

        if (string.IsNullOrWhiteSpace(content))
        {
            ShowEmpty();
            return;
        }

        if (content.Length > MaxImportSizeChars)
        {
            Trace.TraceWarning(
                $"Rich content too large to render as a document ({content.Length:N0} chars); falling back to plain text.");
            ShowFallback(content);
            return;
        }

        if (LongestUnbrokenRun(content) > MaxUnbrokenRunChars)
        {
            Trace.TraceWarning(
                "Rich content contains a very long run with no break opportunity; falling back to plain text.");
            ShowFallback(content);
            return;
        }

        DocumentNode document;
        try
        {
            // Importers are pure and take no Avalonia dependency, so the parse is safe off
            // the UI thread, and DocumentNode is immutable once built.
            document = await Task.Run(() => Import(content, format)).WaitAsync(ImportTimeout)
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            Trace.TraceWarning("Rich content import timed out; falling back to plain text.");
            ShowFallback(content);
            return;
        }
        catch (Exception ex)
        {
            // Importers are documented never to throw, so reaching here means a defect
            // rather than bad input. Degrade instead of blanking the preview.
            Trace.TraceWarning($"Rich content import failed; falling back to plain text: {ex}");
            ShowFallback(content);
            return;
        }

        if (Volatile.Read(ref _renderGeneration) != generation)
        {
            return;
        }

        _viewer.State = EditorState.Create(document);
        _viewer.IsVisible = true;
        _fallbackContent.IsVisible = false;
        _emptyState.IsVisible = false;
    }

    private static DocumentNode Import(string content, ClipContentFormat format) => format switch
    {
        ClipContentFormat.Html => HtmlFormat.Instance.Import(content).Doc,
        ClipContentFormat.Rtf => RtfFormat.Instance.Import(content).Doc,
        // Anything else reaching a rich renderer is text that was mislabelled somewhere
        // upstream; showing it as paragraphs beats showing nothing.
        _ => DocumentNode.FromParagraphs(content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')),
    };

    private void ShowEmpty()
    {
        _viewer.State = EditorState.Create(DocumentNode.Empty);
        _viewer.IsVisible = false;
        _fallbackContent.IsVisible = false;
        _emptyState.IsVisible = true;
    }

    private void ShowFallback(string content)
    {
        _viewer.State = EditorState.Create(DocumentNode.Empty);
        _viewer.IsVisible = false;
        _fallbackContent.Text = BuildFallbackText(content);
        _fallbackContent.IsVisible = true;
        _emptyState.IsVisible = false;
    }

    // Flattening the markup is cheap - sub-millisecond even at a few hundred KB - but
    // handing the whole result to a wrapping TextBlock is not: shaping and wrapping an
    // unbounded string costs minutes, on the UI thread, for a preview nobody can read
    // past the first screen of. Measured at over four minutes for a 600 KB clip. The
    // control this replaced had the same shape, but only on platforms with no WebView,
    // so it never showed up on Windows.
    //
    // The bound is 8 KB rather than something roomier because this is also where an
    // unbreakable run lands (see MaxUnbrokenRunChars), and that is the quadratic case:
    // 8,000 characters of base64 costs tens of milliseconds, where 16,000 would already
    // be over a tenth of a second on every arrow-key move onto such a clip.
    private const int MaxFallbackChars = 8 * 1024;


    private static string BuildFallbackText(string content)
    {
        var flattened = ClipDisplayFormatter.NormalizePreviewText(
            ClipDisplayFormatter.RenderRichContent(content));

        return flattened.Length <= MaxFallbackChars
            ? flattened
            : flattened[..MaxFallbackChars] + AppText.PreviewTruncatedSuffix;
    }
}
