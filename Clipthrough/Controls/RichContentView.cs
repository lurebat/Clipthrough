using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvRichTextBoxControl = AvRichTextBox.RichTextBox;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));
    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty = AvaloniaProperty.Register<RichContentView, ClipContentFormat>(nameof(ContentFormat));
    private readonly AvRichTextBoxControl _htmlView = new()
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

    public RichContentView()
    {
        Content = _textBox;
        this.GetObservable(MarkupProperty).Subscribe(RenderContent);
        this.GetObservable(ContentFormatProperty).Subscribe(_ => RenderContent(Markup));
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
            var html = ClipboardMarkupDecoder.ExtractHtmlFragment(content);
            Dispatcher.UIThread.Post(() => RenderHtml(html), DispatcherPriority.Loaded);
            return;
        }

        _textBox.Text = ContentFormat switch
        {
            ClipContentFormat.Rtf => ClipDisplayFormatter.RenderRichContent(content),
            _ => ClipDisplayFormatter.NormalizePreviewText(content),
        };
        Content = _textBox;
    }

    private void RenderHtml(string html)
    {
        try
        {
            _htmlView.LoadHtml(html);
            Content = _htmlView;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HTML rich preview fallback activated: {ex.Message}");
            _textBox.Text = ClipDisplayFormatter.RenderRichContent(html);
            Content = _textBox;
        }
    }
}

