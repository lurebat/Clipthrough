using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using TextMateSharp.Grammars;
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

    private readonly HtmlPanel _htmlPanel = new()
    {
        IsSelectionEnabled = true,
    };

    private readonly ScrollViewer _htmlScroll;

    private readonly TextEditor _textEditor;
    private TextMate.Installation? _textMateInstall;
    private readonly RegistryOptions _darkRegistry = new(ThemeName.DarkPlus);
    private readonly RegistryOptions _lightRegistry = new(ThemeName.LightPlus);

    public RichContentView()
    {
        Focusable = true;

        _textEditor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
        };

        _htmlScroll = new ScrollViewer
        {
            Content = _htmlPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Content = _textEditor;

        // Use tunnel strategy so we intercept Ctrl+C before HtmlPanel can swallow it
        AddHandler(KeyDownEvent, OnCopyKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeColors();
    }

    private void OnCopyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C
            && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control
            && Content == _htmlScroll)
        {
            var selectedText = _htmlPanel.SelectedText;
            if (!string.IsNullOrEmpty(selectedText))
            {
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selectedText);
                e.Handled = true;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant")
        {
            ApplyThemeColors();
            RenderContent(Markup);
        }
    }

    private void ApplyThemeColors()
    {
        var isDark = ActualThemeVariant != ThemeVariant.Light;

        _htmlPanel.Background = isDark
            ? new SolidColorBrush(Color.Parse("#1E293B"))
            : Brushes.White;

        _textEditor.Background = isDark
            ? new SolidColorBrush(Color.Parse("#1E293B"))
            : new SolidColorBrush(Color.Parse("#F8FAFC"));
        _textEditor.Foreground = isDark
            ? new SolidColorBrush(Color.Parse("#E2E8F0"))
            : new SolidColorBrush(Color.Parse("#0F172A"));

        ApplyTextMateTheme(isDark);
    }

    private void ApplyTextMateTheme(bool isDark)
    {
        _textMateInstall?.Dispose();
        var registry = isDark ? _darkRegistry : _lightRegistry;
        _textMateInstall = _textEditor.InstallTextMate(registry);

        var htmlLang = registry.GetLanguageByExtension(".html");
        if (htmlLang is not null)
        {
            _textMateInstall.SetGrammar(registry.GetScopeByLanguageId(htmlLang.Id));
        }
    }

    private void RenderContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _textEditor.Text = AppText.PreviewEmptyRichTextData;
            Content = _textEditor;
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

        _textEditor.Text = ClipDisplayFormatter.NormalizePreviewText(content);
        Content = _textEditor;
    }

    private void RenderHtml(string html)
    {
        try
        {
            var document = ClipboardMarkupDecoder.BuildHtmlRenderDocument(html);
            if (string.IsNullOrWhiteSpace(document))
            {
                _textEditor.Text = AppText.PreviewEmptyRichTextData;
                Content = _textEditor;
                return;
            }

            var fullHtml = WrapInDocument(document);
            _htmlPanel.Text = fullHtml;
            Content = _htmlScroll;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HTML render fallback: {ex.Message}");
            _textEditor.Text = FormatHtml(html);
            Content = _textEditor;
        }
    }

    private void RenderRtf(string rtf)
    {
        try
        {
            var html = RtfToHtmlConverter.Convert(rtf);
            var fullHtml = WrapInDocument(html);
            _htmlPanel.Text = fullHtml;
            Content = _htmlScroll;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"RTF render fallback: {ex.Message}");
            _textEditor.Text = rtf;
            Content = _textEditor;
        }
    }

    private void ApplyReadOnlyState(bool isReadOnly)
    {
        _textEditor.IsReadOnly = isReadOnly;
    }

    /// <summary>
    /// Wraps HTML content in a full document with default styling based on the current theme.
    /// </summary>
    private string WrapInDocument(string htmlContent)
    {
        if (htmlContent.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return htmlContent;
        }

        var isDark = ActualThemeVariant != ThemeVariant.Light;
        var bg = isDark ? "#1E293B" : "#FFFFFF";
        var fg = isDark ? "#E2E8F0" : "#0F172A";

        return $"""
            <html>
            <body style="margin:8px; font-family:'Segoe UI',sans-serif; font-size:14px; word-wrap:break-word; background-color:{bg}; color:{fg};">
            {htmlContent}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Attempts to pretty-print HTML for display in the text editor.
    /// Falls back to the raw string if parsing fails.
    /// </summary>
    private static string FormatHtml(string html)
    {
        try
        {
            var doc = XDocument.Parse(html, LoadOptions.PreserveWhitespace);
            using var sw = new System.IO.StringWriter();
            using var xw = XmlWriter.Create(sw, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = true,
            });
            doc.WriteTo(xw);
            xw.Flush();
            return sw.ToString();
        }
        catch
        {
            return html;
        }
    }
}
