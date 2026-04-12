using System;
using System.Text;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class ClipDisplayFormatterTests
{
    [Fact]
    public void RenderRichContent_StripsHtmlAndDecodesEntities()
    {
        var html = "<div>Hello <b>world</b> &amp; <i>friends</i></div><script>alert('x')</script>";

        var result = ClipDisplayFormatter.RenderRichContent(html);

        Assert.Equal("Hello world & friends", result);
    }

    [Fact]
    public void BuildFileItems_RemovesLabelsQuotesAndDuplicates()
    {
        const string content = "Files copied:\n\"C:\\Temp\\One.txt\"\nC:\\Temp\\One.txt\n• C:\\Temp\\Two.txt";

        var items = ClipDisplayFormatter.BuildFileItems(content);

        Assert.Equal(["C:\\Temp\\One.txt", "C:\\Temp\\Two.txt"], items);
    }

    [Fact]
    public void BuildTitle_UsesPlainTextRenderingForRichText()
    {
        var clip = new ClipEntry
        {
            Content = "<p>fallback</p>",
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
            ContentBytes = Encoding.UTF8.GetBytes("<p>Hello <strong>rich</strong> text</p>"),
        };

        var title = ClipDisplayFormatter.BuildTitle(clip);

        Assert.Equal("Hello rich text", title);
    }

    [Fact]
    public void RenderRichContent_StripsCfHtmlHeaderBeforeRendering()
    {
        const string document = "<html><body><!--StartFragment--><p>Hello <strong>world</strong></p><!--EndFragment--></body></html>";
        const string fragment = "<p>Hello <strong>world</strong></p>";
        var html = CreateClipboardHtml(document, fragment);

        var result = ClipDisplayFormatter.RenderRichContent(html);

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void BuildTitle_UsesPreferredImageLabelWhenAvailable()
    {
        var clip = new ClipEntry
        {
            Content = "IMG_2048",
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ImageWidth = 1920,
            ImageHeight = 1080,
        };

        var title = ClipDisplayFormatter.BuildTitle(clip);

        Assert.Equal("IMG_2048", title);
    }

    [Fact]
    public void GetRawMarkup_DecodesStoredRtfBytes()
    {
        const string rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 original}";
        var clip = new ClipEntry
        {
            Content = "original",
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Rtf,
            ContentBytes = Encoding.UTF8.GetBytes(rtf),
        };

        var markup = ClipDisplayFormatter.GetRawMarkup(clip);

        Assert.Equal(rtf, markup);
    }

    [Fact]
    public void ExtractHtmlDocument_PreservesStyleBlock()
    {
        const string document = "<!DOCTYPE html><html><head><style>.code{color:#ff0000;}</style></head><body><!--StartFragment--><span class=\"code\">hello</span><!--EndFragment--></body></html>";
        const string fragment = "<span class=\"code\">hello</span>";
        var html = CreateClipboardHtml(document, fragment);

        var extractedDocument = ClipboardMarkupDecoder.ExtractHtmlDocument(html);

        Assert.StartsWith("<!DOCTYPE html><html><head>", extractedDocument);
        Assert.Contains("<style>.code{color:#ff0000;}</style>", extractedDocument);
    }

    [Fact]
    public void BuildHtmlRenderDocument_RemovesClipboardHeaderButKeepsStyles()
    {
        const string document = "<!DOCTYPE html><html><head><style>.code{color:#ff0000;}</style></head><body><!--StartFragment--><span class=\"code\">hello</span><!--EndFragment--></body></html>";
        const string fragment = "<span class=\"code\">hello</span>";
        var html = CreateClipboardHtml(document, fragment);

        var rendered = ClipboardMarkupDecoder.BuildHtmlRenderDocument(html);

        Assert.DoesNotContain("Format:HTML Format", rendered);
        Assert.DoesNotContain("<!--StartFragment-->", rendered);
        Assert.Contains("<style>.code{color:#ff0000;}</style>", rendered);
        Assert.Contains(fragment, rendered);
    }

    private static string CreateClipboardHtml(string document, string fragment)
    {
        const string headerTemplate = "Format:HTML Format\r\nVersion:1.0\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        var startHtml = headerTemplate.Length;
        var fragmentOffset = document.IndexOf(fragment, StringComparison.Ordinal);
        var startFragment = startHtml + fragmentOffset;
        var endFragment = startFragment + fragment.Length;
        var endHtml = startHtml + document.Length;

        return $"Format:HTML Format\r\nVersion:1.0\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n{document}";
    }
}
