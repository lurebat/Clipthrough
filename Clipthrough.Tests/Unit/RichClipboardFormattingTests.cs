using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class RichClipboardFormattingTests
{
    /// <summary>
    /// An opening tag alone is not evidence of markup. Code and prose produce
    /// tag-shaped text all the time, and classifying it as HTML injects it raw
    /// into the CF_HTML document - Word and Outlook then drop the "tag" and the
    /// text it swallowed, silently losing part of what the user copied.
    /// </summary>
    [Theory]
    [InlineData("Template<int> t; if (a<b) return;")]
    [InlineData("List<string> names = new();")]
    [InlineData("assert x < y && y > z;")]
    public void LooksLikeHtml_IsFalseForCodeThatMerelyContainsAngleBrackets(string content)
        => Assert.False(RichClipboardFormatting.LooksLikeHtml(content));

    /// <summary>
    /// The counterweight: tightening the rule must not start escaping genuine
    /// markup. Custom and unknown element names still close, so they are still
    /// recognised - this is what stops the rule becoming a tag-name whitelist.
    /// </summary>
    [Theory]
    [InlineData("<table><tr><td>1</td></tr></table>")]
    [InlineData("<mj-column>custom element</mj-column>")]
    [InlineData("<p>hello</p>")]
    [InlineData("line<br>break")]
    [InlineData("<img src=\"x.png\">")]
    [InlineData("<div class=\"a\" />")]
    [InlineData("<!DOCTYPE html><html><body>x</body></html>")]
    public void LooksLikeHtml_IsTrueForRealMarkup(string content)
        => Assert.True(RichClipboardFormatting.LooksLikeHtml(content));

    /// <summary>
    /// Code misdetected as HTML was passed through unescaped. It must now be
    /// escaped so the angle brackets survive the paste.
    /// </summary>
    [Fact]
    public void BuildCfHtml_EscapesCodeThatLooksLikeATag()
    {
        var result = RichClipboardFormatting.BuildCfHtml("Template<int> t;");

        Assert.Contains("Template&lt;int&gt; t;", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<int>", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Only Environment.NewLine was converted to a break. The transform service
    /// normalises to "\n", so on Windows a plain-text fragment kept no breaks at
    /// all and pasted into Word as a single run-on line.
    /// </summary>
    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("first\rsecond")]
    public void BuildCfHtml_ConvertsEveryLineEndingFormToABreak(string content)
    {
        var result = RichClipboardFormatting.BuildCfHtml(content);

        Assert.Contains("first<br>second", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The CF_HTML header offsets are byte counts into the document that
    /// follows. If they do not line up, Word pastes nothing or pastes garbage,
    /// and the escaping changes above move every one of them.
    ///
    /// The second case is the one that matters: an ASCII fragment has the same
    /// character count as byte count, so it cannot tell a byte offset from a
    /// character offset. Real markup keeps its non-ASCII bytes (only the escape
    /// branch folds them into numeric entities), so it does.
    /// </summary>
    [Theory]
    [InlineData("a & b\nc", "a &amp; b<br>c")]
    [InlineData("<p>caf\u00e9 \u2014 na\u00efve</p>", "<p>caf\u00e9 \u2014 na\u00efve</p>")]
    public void BuildCfHtml_HeaderOffsetsAddressTheRealFragmentBytes(string content, string expectedFragment)
    {
        var result = RichClipboardFormatting.BuildCfHtml(content);
        var bytes = System.Text.Encoding.UTF8.GetBytes(result);

        var startFragment = ReadOffset(result, "StartFragment:");
        var endFragment = ReadOffset(result, "EndFragment:");
        var startHtml = ReadOffset(result, "StartHTML:");
        var endHtml = ReadOffset(result, "EndHTML:");

        Assert.True(startHtml < startFragment, "StartHTML must precede StartFragment.");
        Assert.True(startFragment < endFragment, "StartFragment must precede EndFragment.");
        Assert.True(endFragment <= endHtml, "EndFragment must not run past EndHTML.");
        Assert.True(endHtml <= bytes.Length, $"EndHTML ({endHtml}) runs past the payload ({bytes.Length}).");

        var fragment = System.Text.Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment);
        Assert.Equal(expectedFragment, fragment);

        static int ReadOffset(string text, string key)
        {
            var start = text.IndexOf(key, System.StringComparison.Ordinal) + key.Length;
            return int.Parse(text.Substring(start, 10), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
