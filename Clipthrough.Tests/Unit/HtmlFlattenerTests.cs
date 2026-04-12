using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class HtmlFlattenerTests
{
    [Fact]
    public void Flatten_SimpleSpan_PreservesContent()
    {
        var html = "<body><p><span style=\"color:#FF0000\">Hello</span></p></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("<p>", result);
        Assert.Contains("Hello", result);
        Assert.Contains("color:#FF0000", result);
    }

    [Fact]
    public void Flatten_DivWithText_BecomesParaWithSpan()
    {
        var html = "<body><div style=\"color:red\">Hello world</div></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("<p>", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("<span", result);
    }

    [Fact]
    public void Flatten_ButtonTag_ContentPreserved()
    {
        var html = "<body><button style=\"font-size:13px\"><div style=\"color:rgb(85,85,85)\">ime</div></button></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("ime", result);
        Assert.Contains("<p>", result);
    }

    [Fact]
    public void Flatten_NestedDivs_FlattenedToMultipleParagraphs()
    {
        var html = "<body><div><div>First</div><div>Second</div></div></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("First", result);
        Assert.Contains("Second", result);
        // Each inner div should become its own <p>
        var pCount = System.Text.RegularExpressions.Regex.Matches(result, "<p>").Count;
        Assert.True(pCount >= 2, $"Expected >=2 <p> tags, got {pCount}");
    }

    [Fact]
    public void Flatten_SemanticTags_ConvertedToStyles()
    {
        var html = "<body><p><b>Bold</b> and <i>italic</i></p></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("Bold", result);
        Assert.Contains("italic", result);
        Assert.Contains("font-weight:bold", result);
        Assert.Contains("font-style:italic", result);
    }

    [Fact]
    public void Flatten_StyleInheritance_PropagatedToChildren()
    {
        var html = "<body><div style=\"color:#FF0000\"><span>Red text</span></div></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("Red text", result);
        Assert.Contains("color:#FF0000", result);
    }

    [Fact]
    public void Flatten_ScriptAndStyleTags_Removed()
    {
        var html = "<body><style>.cls{color:red}</style><p>Content</p><script>alert('x')</script></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("Content", result);
        Assert.DoesNotContain("<style", result);
        Assert.DoesNotContain("<script", result);
        Assert.DoesNotContain("alert", result);
    }

    [Fact]
    public void Flatten_BrTag_Preserved()
    {
        var html = "<body><p>Line1<br/>Line2</p></body>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("Line1", result);
        Assert.Contains("Line2", result);
        Assert.Contains("<br/>", result);
    }

    [Fact]
    public void Flatten_BrowserClipboardHtml_ExtractsText()
    {
        // Realistic clipboard HTML from a browser with button/div structure
        var html = @"<html><body>
            <button style=""font-size:13px"">
                <div style=""color: rgb(85, 85, 85);"">FilterName</div>
                <span>: </span>
                <div style=""font-weight: bold;"">FilterValue</div>
            </button>
        </body></html>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("FilterName", result);
        Assert.Contains("FilterValue", result);
        Assert.Contains("<p>", result);
    }

    [Fact]
    public void Flatten_NoBody_StillWorks()
    {
        var html = "<div style=\"color:blue\">No body tag</div>";
        var result = HtmlFlattener.Flatten(html);

        Assert.Contains("<body>", result);
        Assert.Contains("No body tag", result);
    }

    [Fact]
    public void Flatten_EmptyHtml_ProducesEmptyBody()
    {
        var result = HtmlFlattener.Flatten("<html><body></body></html>");
        Assert.Contains("<body>", result);
        Assert.Contains("</body>", result);
    }

    [Fact]
    public void Flatten_SpanExistingStyleNotOverridden()
    {
        var html = "<body><div style=\"color:red\"><span style=\"color:blue\">Text</span></div></body>";
        var result = HtmlFlattener.Flatten(html);

        // Span's own color:blue should take precedence over div's color:red
        Assert.Contains("color:blue", result);
    }
}
