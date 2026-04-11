using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));
    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty = AvaloniaProperty.Register<RichContentView, ClipContentFormat>(nameof(ContentFormat));
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
            return;
        }

        _textBox.Text = ContentFormat switch
        {
            ClipContentFormat.Html or ClipContentFormat.Rtf => ClipDisplayFormatter.RenderRichContent(content),
            _ => ClipDisplayFormatter.NormalizePreviewText(content),
        };
    }
}

