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
using Vellum;
using Vellum.Avalonia;
using Vellum.Interop.Html;
using Vellum.Interop.Rtf;

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
/// Vellum resolves only pixels carried inside the document (<c>data:</c> URLs) and has no
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

    private readonly RichTextViewer _viewer;
    private readonly TextBlock _emptyState;
    private readonly TextBlock _fallbackContent;

    // Rendering is asynchronous, so a test that only drained the dispatcher would assert
    // against whatever happened to be there. Exposing the in-flight render lets a test
    // await the actual completion instead of sleeping and hoping.
    private Task _pendingRender = Task.CompletedTask;

    internal Task PendingRender => _pendingRender;

    internal RichTextViewer Viewer => _viewer;

    internal TextBlock FallbackText => _fallbackContent;

    internal TextBlock EmptyState => _emptyState;

    // Incremented per render so a slow import cannot overwrite the document belonging to
    // a clip the user has since moved off. Arrowing through the list starts a render per
    // keypress, so out-of-order completion is the normal case, not the rare one.
    private int _renderGeneration;

    public RichDocumentView()
    {
        _viewer = new RichTextViewer
        {
            IsVisible = false,
            Margin = new Thickness(12),
        };

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

        _fallbackContent = new TextBlock
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

        _viewer.Document = document;
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
        _viewer.Document = DocumentNode.Empty;
        _viewer.IsVisible = false;
        _fallbackContent.IsVisible = false;
        _emptyState.IsVisible = true;
    }

    private void ShowFallback(string content)
    {
        _viewer.Document = DocumentNode.Empty;
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
    private const int MaxFallbackChars = 16 * 1024;

    private static string BuildFallbackText(string content)
    {
        var flattened = ClipDisplayFormatter.NormalizePreviewText(
            ClipDisplayFormatter.RenderRichContent(content));

        return flattened.Length <= MaxFallbackChars
            ? flattened
            : flattened[..MaxFallbackChars] + AppText.PreviewTruncatedSuffix;
    }
}
