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
    public void ExtractHtmlFragment_HonoursByteOffsetsWhenDocumentHasNonAsciiText()
    {
        // "shalom olam" in Hebrew: 9 chars, 17 UTF-8 bytes. Every offset after it is
        // shifted by 8, which is exactly what a char-indexed slice gets wrong.
        const string prefix = "<p>\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD</p>";
        const string fragment = "<p>Fragment</p>";
        const string document = "<html><body>" + prefix + "<!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>";
        var html = CreateClipboardHtml(document, fragment);

        var extracted = ClipboardMarkupDecoder.ExtractHtmlFragment(html);

        Assert.Equal(fragment, extracted);
    }

    [Fact]
    public void ExtractHtmlFragment_HonoursByteOffsetsAcrossSurrogatePairs()
    {
        // U+1F600 is a surrogate pair: 2 chars, 4 UTF-8 bytes.
        const string prefix = "<p>\U0001F600\U0001F600</p>";
        const string fragment = "<p>Fragment</p>";
        const string document = "<html><body>" + prefix + "<!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>";
        var html = CreateClipboardHtml(document, fragment);

        var extracted = ClipboardMarkupDecoder.ExtractHtmlFragment(html);

        Assert.Equal(fragment, extracted);
    }

    [Fact]
    public void ExtractHtmlDocument_HonoursByteOffsetsWhenDocumentHasNonAsciiText()
    {
        const string fragment = "<p>Fragment</p>";
        const string document = "<html><body><p>\u05E9\u05DC\u05D5\u05DD</p><!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>";
        var html = CreateClipboardHtml(document, fragment);

        var extracted = ClipboardMarkupDecoder.ExtractHtmlDocument(html);

        Assert.Equal(document, extracted);
    }

    [Fact]
    public void DecodeMarkupBytes_DecodesUtf16Markup()
    {
        var bytes = Encoding.Unicode.GetBytes(@"{\rtf1\ansi Hello}");

        var decoded = ClipboardMarkupDecoder.DecodeMarkupBytes(bytes);

        Assert.Equal(@"{\rtf1\ansi Hello}", decoded);
    }

    // U19: CF_HTML with offsets > Int32.MaxValue previously threw OverflowException in
    // int.Parse, which silently aborted the entire clipboard capture. After the fix,
    // long.TryParse returns null for out-of-range values and the extraction falls back
    // gracefully to marker-based or heuristic paths.

    [Fact]
    public void ExtractHtmlFragment_WithEndHtmlExceedingInt32Max_DoesNotThrow()
    {
        // 9999999999 > int.MaxValue (2147483647); old code threw OverflowException.
        const string html =
            "Version:0.9\r\nStartHTML:0000000097\r\nEndHTML:9999999999\r\n" +
            "StartFragment:0000000097\r\nEndFragment:9999999999\r\n" +
            "<html><body><!--StartFragment--><p>hello</p><!--EndFragment--></body></html>";

        // Must not throw; marker-based fallback extracts the fragment correctly.
        var result = ClipboardMarkupDecoder.ExtractHtmlFragment(html);

        Assert.Equal("<p>hello</p>", result);
    }

    [Theory]
    [InlineData("Version:0.9\r\nStartHTML:9999999999\r\nEndHTML:9999999998\r\n<html/>")]  // start > end
    [InlineData("Version:0.9\r\nStartHTML:2147483648\r\nEndHTML:2147483649\r\n<html/>")]  // = Int32.MaxValue + 1
    public void ExtractHtmlFragment_WithOverflowOffsets_DoesNotThrow(string html)
    {
        // Old code: OverflowException. New code: graceful null → heuristic fallback.
        var ex = Record.Exception(() => ClipboardMarkupDecoder.ExtractHtmlFragment(html));

        Assert.Null(ex);
    }

    [Fact]
    public void ExtractHtmlDocument_WithOversizedOffsets_DoesNotThrow()
    {
        const string html =
            "Version:0.9\r\nStartHTML:9999999999\r\nEndHTML:9999999999\r\n" +
            "<html><body>test</body></html>";

        var ex = Record.Exception(() => ClipboardMarkupDecoder.ExtractHtmlDocument(html));

        Assert.Null(ex);
    }

    private static string CreateClipboardHtml(string document, string fragment)
    {
        const string headerTemplate = "Format:HTML Format\r\nVersion:1.0\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";

        // CF_HTML offsets are byte positions into the UTF-8 payload. The header itself is
        // pure ASCII, so its byte length equals its char length, but the document is not.
        var startHtml = headerTemplate.Length;
        var fragmentCharOffset = document.IndexOf(fragment, StringComparison.Ordinal);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(document[..fragmentCharOffset]);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(document);

        return $"Format:HTML Format\r\nVersion:1.0\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n{document}";
    }
}
