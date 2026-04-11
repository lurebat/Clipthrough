using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using AvRichTextBox;
using Clipthrough.Localization;

namespace Clipthrough.Controls;

public sealed class RichContentView : UserControl
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<RichContentView, string?>(nameof(Markup));

    private static readonly Regex s_htmlRegex = new(@"<\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex s_cfHtmlHeaderRegex = new(@"(?<name>StartHTML|EndHTML|StartFragment|EndFragment):(?<value>\d{1,10})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RichTextBox _richTextBox = new()
    {
        IsReadOnly = true,
        ShowDebuggerPanelInDebugMode = false,
        FlowDocument = new FlowDocument(),
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
            if (LooksLikeHtml(content))
            {
                _richTextBox.LoadHtml(NormalizeHtmlForDisplay(content));
                return;
            }

            if (LooksLikeRtf(content))
            {
                _richTextBox.LoadRtf(content);
                return;
            }
        }
        catch
        {
            // Fall back to a simple HTML-encoded text rendering if the source markup is malformed.
        }

        LoadPlainText(content);
    }

    private void LoadPlainText(string text)
    {
        var encoded = WebUtility.HtmlEncode(text)
            .Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("\r", "<br/>", StringComparison.Ordinal);

        _richTextBox.LoadHtml($"<html><body><p>{encoded}</p></body></html>");
    }

    private static bool LooksLikeHtml(string content)
        => s_htmlRegex.IsMatch(content) || content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRtf(string content)
        => content.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);

    private void OnLoaded(object? sender, EventArgs e)
    {
        RenderContent(Markup);
    }

    private static string NormalizeHtmlForDisplay(string content)
    {
        if (!content.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var startFragmentMarker = "<!--StartFragment-->";
        var endFragmentMarker = "<!--EndFragment-->";
        var markerStart = content.IndexOf(startFragmentMarker, StringComparison.OrdinalIgnoreCase);
        var markerEnd = content.IndexOf(endFragmentMarker, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0 && markerEnd > markerStart)
        {
            var fragmentStart = markerStart + startFragmentMarker.Length;
            return content[fragmentStart..markerEnd].Trim();
        }

        var offsets = s_cfHtmlHeaderRegex.Matches(content)
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["value"].Value));

        if (offsets.TryGetValue("StartHTML", out var startHtml)
            && offsets.TryGetValue("EndHTML", out var endHtml)
            && startHtml >= 0
            && endHtml > startHtml
            && endHtml <= content.Length)
        {
            return content[startHtml..endHtml].Trim();
        }

        var htmlIndex = content.IndexOf('<');
        return htmlIndex >= 0 ? content[htmlIndex..].Trim() : content;
    }
}

