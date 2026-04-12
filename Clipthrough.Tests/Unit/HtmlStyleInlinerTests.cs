using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class HtmlStyleInlinerTests
{
    [Fact]
    public void Inline_WithCssClassStyles_InlinesOntoSpans()
    {
        var html = """
            <style>.red{color:#FF0000;font-weight:bold}</style>
            <p><span class="red">Hello</span></p>
            """;

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("color:#FF0000", result);
        Assert.Contains("font-weight:bold", result);
        Assert.DoesNotContain("<style", result);
        Assert.DoesNotContain("class=\"red\"", result);
    }

    [Fact]
    public void Inline_ClassWithExistingInlineStyle_MergesStyles()
    {
        var html = """
            <style>.blue{color:#0000FF;font-size:10pt}</style>
            <span class="blue" style="font-style:italic">text</span>
            """;

        var result = HtmlStyleInliner.Inline(html);

        // Both class-derived and inline styles should be present
        Assert.Contains("color:#0000FF", result);
        Assert.Contains("font-style:italic", result);
        // Should NOT have duplicate style attributes
        Assert.DoesNotContain("class=\"blue\"", result);
        var styleCount = System.Text.RegularExpressions.Regex.Matches(result, "style=\"").Count;
        Assert.Equal(1, styleCount);
    }

    [Fact]
    public void Inline_WithDivTag_StripsWrapper()
    {
        var html = "<html><body><div style=\"color:#aaa\"><span>text</span></div></body></html>";

        var result = HtmlStyleInliner.Inline(html);

        Assert.DoesNotContain("<div", result);
        Assert.DoesNotContain("</div>", result);
        Assert.Contains("<span", result);
    }

    [Fact]
    public void Inline_WithPreTag_ConvertsToParagraph()
    {
        var html = "<pre style=\"font-family:monospace\">code here</pre>";

        var result = HtmlStyleInliner.Inline(html);

        Assert.DoesNotContain("<pre", result);
        Assert.DoesNotContain("</pre>", result);
        Assert.Contains("<p", result);
    }

    [Fact]
    public void Inline_PropagatesContainerColor_ToUnstyledSpans()
    {
        var html = """<div style="color:#a9b7c6"><span>text</span></div>""";

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("color:#a9b7c6", result);
        Assert.Matches(@"<span\s+style=""[^""]*color:#a9b7c6[^""]*""", result);
    }

    [Fact]
    public void Inline_DoesNotOverrideExistingSpanColor()
    {
        var html = """<div style="color:#a9b7c6"><span style="color:#808080">grey</span></div>""";

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("color:#808080", result);
    }

    [Fact]
    public void Inline_SiblingContainers_ScopesStylesCorrectly()
    {
        var html = """
            <div style="color:red"><span>A</span></div>
            <div style="color:blue"><span>B</span></div>
            """;

        var result = HtmlStyleInliner.Inline(html);

        // Span A should get red, span B should get blue
        var spanAIndex = result.IndexOf(">A<");
        var spanBIndex = result.IndexOf(">B<");
        Assert.True(spanAIndex > 0 && spanBIndex > 0);

        var beforeA = result[..spanAIndex];
        var betweenAB = result[spanAIndex..spanBIndex];
        Assert.Contains("color:red", beforeA);
        Assert.Contains("color:blue", betweenAB);
        // A should NOT have blue
        Assert.DoesNotContain("color:blue", beforeA);
    }

    [Fact]
    public void Inline_NestedContainers_InnerOverridesOuter()
    {
        var html = """<div style="color:red;font-size:12pt"><pre style="color:green"><span>text</span></pre></div>""";

        var result = HtmlStyleInliner.Inline(html);

        // Find the innermost span (the one wrapping "text")
        var textSpanIndex = result.IndexOf(">text<");
        Assert.True(textSpanIndex > 0);
        // Extract the span tag preceding ">text<"
        var preceding = result[..textSpanIndex];
        var lastSpanOpen = preceding.LastIndexOf("<span", System.StringComparison.OrdinalIgnoreCase);
        var spanTag = result[lastSpanOpen..(textSpanIndex + 1)];

        // Inner container (pre) color should override outer (div) color
        // But font-size from outer should still propagate
        Assert.Contains("color:green", spanTag);
        Assert.Contains("font-size:12pt", spanTag);
        Assert.DoesNotContain("color:red", spanTag);
    }

    [Fact]
    public void Inline_JetBrainsStyle_PreservesInlineColors()
    {
        var html = """
            <html><head></head><body>
            <div style="background-color:#2b2b2b;color:#a9b7c6">
            <pre style="font-family:monospace;font-size:12pt;">
            <span style="color:#808080;">// comment</span>
            <span style="color:#cc7832;">var </span>x = 1;
            </pre></div></body></html>
            """;

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("color:#808080", result);
        Assert.Contains("color:#cc7832", result);
        Assert.DoesNotContain("<div", result);
        Assert.DoesNotContain("<pre", result);
    }

    [Fact]
    public void Inline_KustoStyleWithCssClasses_InlinesColors()
    {
        var html = """
            <style type="text/css">
            .csBlue{color:#0000FF;font-family:Consolas;font-size:10pt}
            .csRed{color:#DA3900;font-family:Consolas;font-size:10pt}
            </style>
            <p><span class="csBlue">DimClustersMv</span></p>
            <p><span class="csRed">where</span></p>
            """;

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("color:#0000FF", result);
        Assert.Contains("color:#DA3900", result);
        Assert.DoesNotContain("<style", result);
    }

    [Fact]
    public void Inline_PreWithNewlines_ConvertsToBreaks()
    {
        var html = "<pre>line1\nline2\n  indented</pre>";

        var result = HtmlStyleInliner.Inline(html);

        Assert.Contains("line1<br/>line2", result);
        Assert.Contains("&nbsp;", result);
    }

    [Fact]
    public void Inline_NullOrEmpty_ReturnsAsIs()
    {
        Assert.Null(HtmlStyleInliner.Inline(null!));
        Assert.Equal(string.Empty, HtmlStyleInliner.Inline(string.Empty));
        Assert.Equal("  ", HtmlStyleInliner.Inline("  "));
    }

    [Fact]
    public void Inline_PlainHtmlWithoutStylesOrUnsupportedTags_PassesThrough()
    {
        var html = "<p><span style=\"color:red\">hello</span></p>";

        var result = HtmlStyleInliner.Inline(html);

        Assert.Equal(html, result);
    }

    [Fact]
    public void Inline_ExtractsBackgroundColor()
    {
        var html = """<div style="background-color:#2b2b2b;color:#a9b7c6"><pre><span>text</span></pre></div>""";

        _ = HtmlStyleInliner.Inline(html, out var bgColor);

        Assert.Equal("#2b2b2b", bgColor);
    }

    [Fact]
    public void Inline_NoBackgroundColor_ReturnsNull()
    {
        var html = "<p><span style=\"color:red\">hello</span></p>";

        _ = HtmlStyleInliner.Inline(html, out var bgColor);

        Assert.Null(bgColor);
    }

    [Fact]
    public void Inline_NestedDivPre_ProducesNonNestedParagraphs()
    {
        var html = """<div style="background-color:#2b2b2b;color:#a9b7c6"><pre style="font-family:mono"><span style="color:#808080">text</span></pre></div>""";

        var result = HtmlStyleInliner.Inline(html);

        Assert.DoesNotContain("<div", result);
        Assert.DoesNotContain("<pre", result);
        Assert.Contains("<p", result);
        // Should not have nested <p> tags
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "<p[ >]"));
    }

    [Fact]
    public void ExtractBackgroundColor_RgbFormat_CapturesFullValue()
    {
        var html = """<span style="background-color: rgb(255, 255, 255); color: rgb(43, 43, 43);">text</span>""";

        var bgColor = HtmlStyleInliner.ExtractBackgroundColor(html);

        Assert.NotNull(bgColor);
        Assert.StartsWith("rgb", bgColor);
        Assert.Contains("255", bgColor);
    }

    [Fact]
    public void InferBackgroundFromTextColors_LightHexColors_ReturnsDarkBackground()
    {
        var html = """<p><span style="color:#F8FAFC">light text</span></p>""";

        var bg = HtmlStyleInliner.InferBackgroundFromTextColors(html);

        Assert.NotNull(bg);
        Assert.Equal("#1E1E1E", bg);
    }

    [Fact]
    public void InferBackgroundFromTextColors_DarkHexColors_ReturnsNull()
    {
        var html = """<p><span style="color:#333333">dark text</span></p>""";

        var bg = HtmlStyleInliner.InferBackgroundFromTextColors(html);

        Assert.Null(bg);
    }

    [Fact]
    public void InferBackgroundFromTextColors_LightRgbColors_ReturnsDarkBackground()
    {
        var html = """<span style="color: rgb(200, 210, 220);">light</span>""";

        var bg = HtmlStyleInliner.InferBackgroundFromTextColors(html);

        Assert.NotNull(bg);
        Assert.Equal("#1E1E1E", bg);
    }

    [Fact]
    public void InferBackgroundFromTextColors_NoColors_ReturnsNull()
    {
        var html = """<p>plain text without colors</p>""";

        var bg = HtmlStyleInliner.InferBackgroundFromTextColors(html);

        Assert.Null(bg);
    }

    [Fact]
    public void InferBackgroundFromTextColors_MixedColors_UsesAverage()
    {
        // One very dark (luminance ~0.0) and one very light (luminance ~1.0) — average ~0.5
        var html = """<span style="color:#000000">dark</span><span style="color:#FFFFFF">light</span>""";

        var bg = HtmlStyleInliner.InferBackgroundFromTextColors(html);

        // Average luminance ~0.5 which is < 0.65, so no dark bg needed
        Assert.Null(bg);
    }
}
