using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));
    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty = AvaloniaProperty.Register<RichContentView, ClipContentFormat>(nameof(ContentFormat));
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<RichContentView, bool>(nameof(IsReadOnly), true);
    private readonly SafeRichTextBox _htmlView = new()
    {
        IsReadOnly = true,
    };
    private readonly TextBox _textBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = new SolidColorBrush(Color.Parse("#0F172A")),
        Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
    };
    private bool _isSyncingEditor;
    private bool _isSyncingMarkup;
    private string? _pendingRichContent;
    private ClipContentFormat _pendingFormat;

    public RichContentView()
    {
        _htmlView.KeyUp += (_, _) => SyncMarkupFromEditor();
        _htmlView.TextInput += (_, _) => SyncMarkupFromEditor();
        _htmlView.LostFocus += (_, _) => SyncMarkupFromEditor();
        _htmlView.PointerReleased += (_, _) => SyncMarkupFromEditor();
        _htmlView.Loaded += OnHtmlViewLoaded;
        Content = _textBox;
        this.GetObservable(MarkupProperty).Subscribe(RenderContent);
        this.GetObservable(ContentFormatProperty).Subscribe(_ => RenderContent(Markup));
        this.GetObservable(IsReadOnlyProperty).Subscribe(ApplyReadOnlyState);
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

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    private void RenderContent(string? content)
    {
        if (_isSyncingMarkup)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            _pendingRichContent = null;
            _textBox.Text = AppText.PreviewEmptyRichTextData;
            Content = _textBox;
            return;
        }

        if (ContentFormat == ClipContentFormat.Html)
        {
            var html = ClipboardMarkupDecoder.BuildHtmlRenderDocument(content);
            html = HtmlStyleInliner.Inline(html, out var bgColor);
            bgColor ??= HtmlStyleInliner.InferBackgroundFromTextColors(html);
            ApplyBackground(bgColor);
            ScheduleRichLoad(html, ClipContentFormat.Html);
            return;
        }

        if (ContentFormat == ClipContentFormat.Rtf)
        {
            ScheduleRichLoad(content, ClipContentFormat.Rtf);
            return;
        }

        _pendingRichContent = null;
        _textBox.Text = ClipDisplayFormatter.NormalizePreviewText(content);
        Content = _textBox;
    }

    /// <summary>
    /// AvRichTextBox creates its FlowDocument in its Loaded handler.
    /// We must add it to the visual tree first and wait for Loaded before
    /// calling LoadHtml/LoadRtf, otherwise FlowDoc is null → NRE.
    /// </summary>
    private void ScheduleRichLoad(string content, ClipContentFormat format)
    {
        _pendingRichContent = content;
        _pendingFormat = format;

        if (_htmlView.IsLoaded)
        {
            // Already in visual tree and loaded — load immediately
            Dispatcher.UIThread.Post(LoadPendingContent, DispatcherPriority.Loaded);
        }
        else
        {
            // Put it in the tree so Loaded fires; content loads in OnHtmlViewLoaded
            Content = _htmlView;
        }
    }

    private async void OnHtmlViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (_pendingRichContent is not null)
        {
            // AvRichTextBox.FlowDocument.InitializeDocument() is async void and
            // calls await Task.Delay(70) before accessing AllParagraphs[0].
            // We must wait longer than that to avoid a race that clears AllParagraphs
            // while InitializeDocument is still running.
            await Task.Delay(100);
            LoadPendingContent();
        }
    }

    private void LoadPendingContent()
    {
        var content = _pendingRichContent;
        var format = _pendingFormat;
        _pendingRichContent = null;

        if (content is null)
        {
            return;
        }

        if (format == ClipContentFormat.Html)
        {
            RenderHtml(content);
        }
        else if (format == ClipContentFormat.Rtf)
        {
            RenderRtf(content);
        }
    }

    private void RenderHtml(string html)
    {
        try
        {
            _isSyncingEditor = true;
            var flatHtml = HtmlFlattener.Flatten(html);
            flatHtml = HtmlStyleInliner.NormalizeRgbColors(flatHtml);
            _htmlView.LoadHtml(flatHtml);
            EnsureFlowDocHasParagraph();

            // If no explicit background was set by the caller, infer from loaded runs
            if (_htmlView.Background is SolidColorBrush { Color.R: 255, Color.G: 255, Color.B: 255 })
            {
                var inferred = InferBackgroundFromFlowDoc(_htmlView.FlowDocument);
                if (inferred is not null)
                {
                    ApplyBackground(inferred);
                }
            }

            ApplyReadOnlyState(IsReadOnly);
            Content = _htmlView;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HTML rich preview fallback: {ex.Message}");
            _textBox.Text = ClipDisplayFormatter.RenderRichContent(html);
            Content = _textBox;
        }
        finally
        {
            _isSyncingEditor = false;
        }
    }

    private void RenderRtf(string rtf)
    {
        try
        {
            _isSyncingEditor = true;

            // Use native RTF loading — AvRichTextBox's RtfConversions sets
            // Foreground/Background on each EditableRun from the RTF color table.
            _htmlView.LoadRtf(rtf);

            // LoadRtf silently swallows all exceptions. If it produced 0 blocks,
            // fall back to our RTF→HTML converter.
            if (_htmlView.FlowDocument?.Blocks.Count == 0)
            {
                Trace.TraceWarning("LoadRtf produced 0 blocks, trying HTML fallback");
                var html = RtfToHtmlConverter.Convert(rtf);
                html = HtmlStyleInliner.NormalizeRgbColors(html);
                var flatHtml = HtmlFlattener.Flatten(html);
                _htmlView.LoadHtml(flatHtml);
            }

            EnsureFlowDocHasParagraph();

            // Infer background from the parsed FlowDocument's actual text colors
            var bgColor = InferBackgroundFromFlowDoc(_htmlView.FlowDocument);
            ApplyBackground(bgColor);

            ApplyReadOnlyState(IsReadOnly);
            Content = _htmlView;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"RTF rich preview fallback: {ex.Message}");
            _textBox.Text = ClipDisplayFormatter.RenderRichContent(rtf);
            Content = _textBox;
        }
        finally
        {
            _isSyncingEditor = false;
        }
    }

    private void ApplyReadOnlyState(bool isReadOnly)
    {
        _htmlView.IsReadOnly = isReadOnly;
        _textBox.IsReadOnly = isReadOnly;
    }

    private void ApplyBackground(string? colorValue)
    {
        if (!string.IsNullOrEmpty(colorValue))
        {
            try
            {
                _htmlView.Background = new SolidColorBrush(ParseCssColor(colorValue));
                return;
            }
            catch
            {
                // Fall through to default
            }
        }

        _htmlView.Background = Brushes.White;
    }

    private static Color ParseCssColor(string cssColor)
    {
        cssColor = cssColor.Trim();
        if (cssColor.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(cssColor, @"rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
            if (match.Success)
            {
                return Color.FromRgb(
                    byte.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    byte.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    byte.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
            }
        }

        return Color.Parse(cssColor);
    }

    /// <summary>
    /// Infers an appropriate background color by analyzing text foreground colors
    /// in the parsed FlowDocument. If text colors are predominantly light (designed
    /// for dark backgrounds), returns a dark background color string.
    /// </summary>
    private static string? InferBackgroundFromFlowDoc(AvRichTextBox.FlowDocument? flowDoc)
    {
        if (flowDoc is null)
        {
            return null;
        }

        var totalLuminance = 0.0;
        var count = 0;

        foreach (var block in flowDoc.Blocks)
        {
            if (block is not AvRichTextBox.Paragraph para)
            {
                continue;
            }

            foreach (var inline in para.Inlines)
            {
                if (inline is not AvRichTextBox.EditableRun run)
                {
                    continue;
                }

                if (run.Foreground is not SolidColorBrush brush)
                {
                    continue;
                }

                var c = brush.Color;
                totalLuminance += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        return totalLuminance / count > 0.65 ? "#1E1E1E" : null;
    }

    private void SyncMarkupFromEditor()
    {
        if (_isSyncingEditor || IsReadOnly || Content != _htmlView)
        {
            return;
        }

        var markup = ContentFormat switch
        {
            ClipContentFormat.Html => _htmlView.SaveHtml(),
            ClipContentFormat.Rtf => _htmlView.SaveRtf(),
            _ => null
        };

        if (markup is null || string.Equals(markup, Markup, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _isSyncingMarkup = true;
            SetCurrentValue(MarkupProperty, markup);
        }
        finally
        {
            _isSyncingMarkup = false;
        }
    }

    /// <summary>
    /// AvRichTextBox.FlowDocument.InitializeDocument() is async void and accesses
    /// AllParagraphs[0] after a 70ms delay. If LoadHtml produces 0 paragraphs
    /// (e.g. the HTML parser doesn't find &lt;p&gt; as direct children of &lt;body&gt;),
    /// the app crashes with ArgumentOutOfRangeException. This adds a safety paragraph
    /// so AllParagraphs is never empty when the delayed access occurs.
    /// </summary>
    private void EnsureFlowDocHasParagraph()
    {
        var flowDoc = _htmlView.FlowDocument;
        if (flowDoc?.Blocks.Count == 0)
        {
            Trace.TraceWarning("LoadHtml produced 0 paragraphs — adding safety paragraph");
            var p = new AvRichTextBox.Paragraph(flowDoc);
            p.Inlines.Add(new AvRichTextBox.EditableRun(" "));
            flowDoc.Blocks.Add(p);
        }
    }
}
