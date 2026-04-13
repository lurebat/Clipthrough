using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty =
        AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));

    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty =
        AvaloniaProperty.Register<RichContentView, ClipContentFormat>(nameof(ContentFormat));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<RichContentView, bool>(nameof(IsReadOnly), true);

    private readonly HtmlLabel _htmlLabel = new()
    {
        Background = Brushes.White,
    };

    private readonly ScrollViewer _htmlScroll;

    private readonly TextBox _textBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = new SolidColorBrush(Color.Parse("#0F172A")),
        Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
    };

    public RichContentView()
    {
        _htmlScroll = new ScrollViewer
        {
            Content = _htmlLabel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

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
        if (string.IsNullOrWhiteSpace(content))
        {
            _textBox.Text = AppText.PreviewEmptyRichTextData;
            Content = _textBox;
            return;
        }

        if (ContentFormat == ClipContentFormat.Html)
        {
            RenderHtml(content);
            return;
        }

        if (ContentFormat == ClipContentFormat.Rtf)
        {
            RenderRtf(content);
            return;
        }

        _textBox.Text = ClipDisplayFormatter.NormalizePreviewText(content);
        Content = _textBox;
    }

    private void RenderHtml(string html)
    {
        try
        {
            var document = ClipboardMarkupDecoder.BuildHtmlRenderDocument(html);
            if (string.IsNullOrWhiteSpace(document))
            {
                _textBox.Text = AppText.PreviewEmptyRichTextData;
                Content = _textBox;
                return;
            }

            var fullHtml = WrapInDocument(document);
            _htmlLabel.Text = fullHtml;
            Content = _htmlScroll;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HTML render fallback: {ex.Message}");
            _textBox.Text = ClipDisplayFormatter.RenderRichContent(html);
            Content = _textBox;
        }
    }

    private void RenderRtf(string rtf)
    {
        try
        {
            var html = RtfToHtmlConverter.Convert(rtf);
            var fullHtml = WrapInDocument(html);
            _htmlLabel.Text = fullHtml;
            Content = _htmlScroll;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"RTF render fallback: {ex.Message}");
            _textBox.Text = ClipDisplayFormatter.RenderRichContent(rtf);
            Content = _textBox;
        }
    }

    private void ApplyReadOnlyState(bool isReadOnly)
    {
        _textBox.IsReadOnly = isReadOnly;
    }

    /// <summary>
    /// Wraps HTML content in a full document with default styling.
    /// </summary>
    private static string WrapInDocument(string htmlContent)
    {
        if (htmlContent.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return htmlContent;
        }

        return $"""
            <html>
            <body style="margin:8px; font-family:'Segoe UI',sans-serif; font-size:14px; word-wrap:break-word;">
            {htmlContent}
            </body>
            </html>
            """;
    }
}
