using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Controls;

public sealed class RichWebContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty =
        AvaloniaProperty.Register<RichWebContentView, string?>(nameof(Markup));

    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty =
        AvaloniaProperty.Register<RichWebContentView, ClipContentFormat>(nameof(ContentFormat));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<RichWebContentView, bool>(nameof(IsReadOnly), true);

    private readonly Grid _root;
    private readonly NativeWebView? _webView;
    private readonly TextBlock _emptyState;
    private readonly TextBlock _fallbackContent;
    private string? _loadedDocument;
    private bool _suppressRenderFromWebMessage;

    public RichWebContentView()
    {
        if (SupportsNativeWebView())
        {
            _webView = new NativeWebView();
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.WebMessageReceived += OnWebMessageReceived;
        }

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

        _root = new Grid();
        if (_webView is not null)
        {
            _root.Children.Add(_webView);
        }
        _root.Children.Add(_fallbackContent);
        _root.Children.Add(_emptyState);
        Content = _root;

        this.GetObservable(MarkupProperty).Subscribe(RenderContent);
        this.GetObservable(ContentFormatProperty).Subscribe(_ => RenderContent(Markup));
        this.GetObservable(IsReadOnlyProperty).Subscribe(_ => RenderContent(Markup));
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant")
        {
            RenderContent(Markup);
        }
    }

    private bool CanEditHtml => !IsReadOnly && ContentFormat == ClipContentFormat.Html;

    private void RenderContent(string? content)
    {
        if (_suppressRenderFromWebMessage)
        {
            return;
        }

        var document = BuildDocument(content);
        if (string.IsNullOrWhiteSpace(document))
        {
            _loadedDocument = null;
            if (_webView is not null)
            {
                _webView.IsVisible = false;
            }
            _fallbackContent.IsVisible = false;
            _emptyState.IsVisible = true;
            return;
        }

        _emptyState.IsVisible = false;

        if (_webView is null)
        {
            var preview = content ?? string.Empty;
            _fallbackContent.Text = ClipDisplayFormatter.NormalizePreviewText(ClipDisplayFormatter.RenderRichContent(preview));
            _fallbackContent.IsVisible = true;
            return;
        }

        _fallbackContent.IsVisible = false;
        _webView.IsVisible = true;

        if (string.Equals(_loadedDocument, document, StringComparison.Ordinal))
        {
            return;
        }

        _loadedDocument = document;
        _webView.NavigateToString(document);
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !CanEditHtml)
        {
            return;
        }

        try
        {
            if (_webView is null)
            {
                return;
            }

            await _webView.InvokeScript(
                """
                (() => {
                    const root = document.getElementById('clipthrough-editor');
                    if (!root || root.dataset.clipthroughBound === '1') {
                        return;
                    }

                    root.dataset.clipthroughBound = '1';
                    root.contentEditable = 'true';
                    root.spellcheck = false;

                    let timer = 0;
                    const emit = () => invokeCSharpAction(JSON.stringify({ type: 'content', html: root.innerHTML }));
                    const scheduleEmit = () => {
                        clearTimeout(timer);
                        timer = window.setTimeout(emit, 120);
                    };

                    root.addEventListener('input', scheduleEmit);
                    root.addEventListener('blur', emit);
                })();
                """);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WebView editor initialization failed: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (!CanEditHtml)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(e.Body))
            {
                return;
            }

            using var json = JsonDocument.Parse(e.Body);
            if (!json.RootElement.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "content", StringComparison.Ordinal)
                || !json.RootElement.TryGetProperty("html", out var htmlElement))
            {
                return;
            }

            var html = htmlElement.GetString() ?? string.Empty;
            if (string.Equals(Markup, html, StringComparison.Ordinal))
            {
                return;
            }

            _suppressRenderFromWebMessage = true;
            SetCurrentValue(MarkupProperty, html);
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning($"WebView message parse failed: {ex.Message}");
        }
        finally
        {
            _suppressRenderFromWebMessage = false;
        }
    }

    private string BuildDocument(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return ContentFormat switch
        {
            ClipContentFormat.Html when CanEditHtml => WrapFragmentDocument(ClipboardMarkupDecoder.ExtractHtmlFragment(content), editable: true),
            ClipContentFormat.Html => WrapHtmlDocument(ClipboardMarkupDecoder.BuildHtmlRenderDocument(content)),
            ClipContentFormat.Rtf => WrapFragmentDocument(RtfToHtmlConverter.Convert(content), editable: false),
            _ => WrapFragmentDocument(System.Net.WebUtility.HtmlEncode(ClipDisplayFormatter.NormalizePreviewText(content)).Replace(Environment.NewLine, "<br>", StringComparison.Ordinal), editable: false),
        };
    }

    private string WrapHtmlDocument(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return WrapFragmentDocument(html, editable: false);
        }

        var styleBlock = BuildDocumentStyleBlock();
        if (Regex.IsMatch(html, "</head>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Regex.Replace(
                html,
                "</head>",
                $"{styleBlock}</head>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        if (Regex.IsMatch(html, "<html[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Regex.Replace(
                html,
                "<html[^>]*>",
                match => $"{match.Value}<head>{styleBlock}</head>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        return WrapFragmentDocument(html, editable: false);
    }

    private string WrapFragmentDocument(string htmlContent, bool editable)
    {
        var isDark = ActualThemeVariant != ThemeVariant.Light;
        var background = isDark ? "#1E293B" : "#FFFFFF";
        var foreground = isDark ? "#E2E8F0" : "#0F172A";
        var border = isDark ? "#334155" : "#CBD5E1";
        var hostStyles = editable
            ? $"min-height:100%; outline:none; border:1px solid {border}; border-radius:10px; padding:12px; background:rgba(15,23,42,0.02);"
            : "min-height:100%;";

        return $$"""
            <html>
            <head>
                {{BuildDocumentStyleBlock()}}
            </head>
            <body style="margin:0; padding:10px; background-color:{{background}}; color:{{foreground}};">
                <div id="clipthrough-editor" style="{{hostStyles}}">{{htmlContent}}</div>
            </body>
            </html>
            """;
    }

    private string BuildDocumentStyleBlock()
    {
        var isDark = ActualThemeVariant != ThemeVariant.Light;
        var background = isDark ? "#1E293B" : "#FFFFFF";
        var foreground = isDark ? "#E2E8F0" : "#0F172A";
        var link = isDark ? "#93C5FD" : "#2563EB";

        return $$"""
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'; img-src data: blob: file: http: https:; style-src 'unsafe-inline' http: https:; font-src data: http: https:; connect-src http: https:; media-src data: blob: file: http: https:; script-src 'none';">
            <style>
                html, body {
                    min-height: 100%;
                }

                body {
                    font-family: 'Segoe UI', sans-serif;
                    font-size: 14px;
                    line-height: 1.45;
                    word-break: break-word;
                    overflow-wrap: anywhere;
                    background: {{background}};
                    color: {{foreground}};
                }

                img, video, iframe, table {
                    max-width: 100%;
                }

                a {
                    color: {{link}};
                }
            </style>
            """;
    }

    private static bool SupportsNativeWebView()
        => !AppDomain.CurrentDomain
            .GetAssemblies()
            .Any(static assembly => string.Equals(assembly.GetName().Name, "Avalonia.Headless", StringComparison.Ordinal));
}
