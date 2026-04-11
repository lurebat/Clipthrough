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
        const string html = "Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000133\r\nStartFragment:00000101\r\nEndFragment:00000115\r\n<html><body><!--StartFragment--><p>Hello</p><!--EndFragment--></body></html>";
        var gibberish = Encoding.Unicode.GetString(Encoding.UTF8.GetBytes(html));

        var normalized = ClipboardMarkupDecoder.NormalizePlatformMarkupString(gibberish, ClipContentFormat.Html);

        Assert.StartsWith("Version:0.9", normalized);
        Assert.Contains("<p>Hello</p>", normalized);
    }

    [Fact]
    public void ExtractHtmlFragment_RemovesCfHtmlHeader()
    {
        const string html = "Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000133\r\nStartFragment:00000101\r\nEndFragment:00000115\r\n<html><body><!--StartFragment--><p>Hello</p><!--EndFragment--></body></html>";

        var fragment = ClipboardMarkupDecoder.ExtractHtmlFragment(html);

        Assert.Equal("<p>Hello</p>", fragment);
    }

    [Fact]
    public void DecodeMarkupBytes_DecodesUtf16Markup()
    {
        var bytes = Encoding.Unicode.GetBytes(@"{\rtf1\ansi Hello}");

        var decoded = ClipboardMarkupDecoder.DecodeMarkupBytes(bytes);

        Assert.Equal(@"{\rtf1\ansi Hello}", decoded);
    }
}
