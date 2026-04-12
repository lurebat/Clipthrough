using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class RtfToHtmlConverterTests
{
    [Fact]
    public void Convert_SimpleColoredText_ProducesHtmlWithColorStyle()
    {
        var rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 hello}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("<html>", html);
        Assert.Contains("hello", html);
        Assert.Contains("color:#FF0000", html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_BoldItalicText_ProducesCorrectStyles()
    {
        var rtf = @"{\rtf1\ansi\b\i bold italic text}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("font-weight:bold", html);
        Assert.Contains("font-style:italic", html);
        Assert.Contains("bold italic text", html);
    }

    [Fact]
    public void Convert_MultipleColors_PreservesEachColor()
    {
        var rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;\red0\green0\blue255;}\cf1 red\cf2  blue}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("color:#FF0000", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color:#0000FF", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("red", html);
        Assert.Contains("blue", html);
    }

    [Fact]
    public void Convert_PlainTextRtf_ProducesValidHtml()
    {
        var rtf = @"{\rtf1\ansi\pard hello world\par}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("<html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("hello world", html);
    }

    [Fact]
    public void Convert_HtmlSpecialChars_AreEscaped()
    {
        var rtf = @"{\rtf1\ansi\pard a < b & c > d\par}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("&lt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&gt;", html);
    }

    [Fact]
    public void Convert_FontName_IncludedInStyle()
    {
        var rtf = @"{\rtf1\ansi{\fonttbl{\f0 Consolas;}}{\colortbl ;\red0\green128\blue0;}\f0\cf1 code}";
        var html = RtfToHtmlConverter.Convert(rtf);

        Assert.Contains("Consolas", html);
        Assert.Contains("code", html);
    }
}
