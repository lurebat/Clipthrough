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
        const string html = """
            Version:0.9
            StartHTML:0000000097
            EndHTML:0000000175
            StartFragment:0000000133
            EndFragment:0000000138
            <html><body><!--StartFragment--><p>Hello <strong>world</strong></p><!--EndFragment--></body></html>
            """;

        var result = ClipDisplayFormatter.RenderRichContent(html);

        Assert.Equal("Hello world", result);
    }
}
