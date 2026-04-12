using System;
using System.Text;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class ClipboardMarkupDecoderTests
{
    [Fact]
    public void NormalizePlatformMarkupString_RecoversBytePairedCfHtml()
    {
        const string document = "<html><body><!--StartFragment--><p>Hello</p><!--EndFragment--></body></html>";
        const string fragment = "<p>Hello</p>";
        var html = CreateClipboardHtml(document, fragment);
        var gibberish = Encoding.Unicode.GetString(Encoding.UTF8.GetBytes(html));

        var normalized = ClipboardMarkupDecoder.NormalizePlatformMarkupString(gibberish, ClipContentFormat.Html);

        Assert.StartsWith("Format:HTML Format", normalized);
        Assert.Contains("<p>Hello</p>", normalized);
    }

    [Fact]
    public void ExtractHtmlFragment_RemovesCfHtmlHeader()
    {
        const string document = "<html><body><!--StartFragment--><p>Hello</p><!--EndFragment--></body></html>";
        const string fragmentMarkup = "<p>Hello</p>";
        var html = CreateClipboardHtml(document, fragmentMarkup);

        var fragment = ClipboardMarkupDecoder.ExtractHtmlFragment(html);

        Assert.Equal(fragmentMarkup, fragment);
    }

    [Fact]
    public void DecodeMarkupBytes_DecodesUtf16Markup()
    {
        var bytes = Encoding.Unicode.GetBytes(@"{\rtf1\ansi Hello}");

        var decoded = ClipboardMarkupDecoder.DecodeMarkupBytes(bytes);

        Assert.Equal(@"{\rtf1\ansi Hello}", decoded);
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
