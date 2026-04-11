using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvRichTextBox;
using Clipthrough.Localization;
using Clipthrough.Models;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));
    public static readonly StyledProperty<ClipContentFormat> ContentFormatProperty = AvaloniaProperty.Register<RichContentView, ClipContentFormat>(nameof(ContentFormat));
    private static readonly Regex s_cfHtmlHeaderRegex = new(@"(?<name>StartHTML|EndHTML|StartFragment|EndFragment):(?<value>\d{1,10})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RichTextBox _richTextBox = new()
    {
        IsReadOnly = true,
        ShowDebuggerPanelInDebugMode = false,
        FlowDocument = new FlowDocument(),
        Background = new SolidColorBrush(Color.Parse("#0F172A")),
        Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
    };

    public RichContentView()
    {
        Content = _richTextBox;
        this.GetObservable(MarkupProperty).Subscribe(RenderContent);
        Loaded += OnLoaded;
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
        _richTextBox.CreateNewDocument();

        if (string.IsNullOrWhiteSpace(content))
        {
            LoadPlainText(AppText.PreviewEmptyRichTextData);
            return;
        }

        try
        {
            if (ContentFormat == ClipContentFormat.Html)
            {
                _richTextBox.LoadHtml(NormalizeHtmlForDisplay(content));
                return;
            }

            if (ContentFormat == ClipContentFormat.Rtf)
            {
                _richTextBox.LoadRtf(content);
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"Rich content render failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"Rich content render failed: {ex.Message}");
        }

        LoadPlainText(content);
    }

    private void LoadPlainText(string text)
    {
        var encoded = WebUtility.HtmlEncode(text)
            .Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("\r", "<br/>", StringComparison.Ordinal);

        _richTextBox.LoadHtml($"<html><body style=\"background:#0F172A;color:#E2E8F0;font-family:Inter,Segoe UI,sans-serif;\"><p>{encoded}</p></body></html>");
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        RenderContent(Markup);
    }

    private static string NormalizeHtmlForDisplay(string content)
    {
        if (!content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            return WrapInTheme(content);
        }

        var startFragmentMarker = "<!--StartFragment-->";
        var endFragmentMarker = "<!--EndFragment-->";
        var markerStart = content.IndexOf(startFragmentMarker, StringComparison.OrdinalIgnoreCase);
        var markerEnd = content.IndexOf(endFragmentMarker, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0 && markerEnd > markerStart)
        {
            var fragmentStart = markerStart + startFragmentMarker.Length;
            return WrapInTheme(content[fragmentStart..markerEnd].Trim());
        }

        var offsets = s_cfHtmlHeaderRegex.Matches(content)
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["value"].Value));

        if (offsets.TryGetValue("StartHTML", out var startHtml)
            && offsets.TryGetValue("EndHTML", out var endHtml)
            && startHtml >= 0
            && endHtml > startHtml
            && endHtml <= content.Length)
        {
            return WrapInTheme(content[startHtml..endHtml].Trim());
        }

        var htmlIndex = content.IndexOf('<');
        return WrapInTheme(htmlIndex >= 0 ? content[htmlIndex..].Trim() : content);
    }

    private static string WrapInTheme(string html)
    {
        if (html.Contains("<body", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(
                html,
                "<body([^>]*)>",
                "<body$1 style=\"background:#0F172A;color:#E2E8F0;font-family:Inter,Segoe UI,sans-serif;\">",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        return $"<html><body style=\"background:#0F172A;color:#E2E8F0;font-family:Inter,Segoe UI,sans-serif;\">{html}</body></html>";
    }
}

