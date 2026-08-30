using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Markdown conversion in both directions. Markdown is what a great deal of
/// clipboard traffic already is - issue trackers, chat, READMEs - and what
/// people want web or Office content to become before pasting it there.
/// </summary>
/// <remarks>
/// The conversion itself belongs to VellumText; these tests pin the decisions
/// Clipthrough makes around it: when to convert at all, what happens to input
/// that cannot be converted, and that embedded raw HTML does not survive a
/// round trip to the clipboard as rich content.
/// </remarks>
public sealed class MarkdownTransformationTests
{
    private static string Apply(TextTransformation transformation, string input)
        => TextTransformationService.Apply(transformation, input);

    [Fact]
    public void HtmlToMarkdown_ConvertsEmphasisAndHeadings()
    {
        var markdown = Apply(
            TextTransformation.HtmlToMarkdown,
            "<html><body><h1>Title</h1><p>Some <b>bold</b> text.</p></body></html>");

        Assert.Contains("Title", markdown, System.StringComparison.Ordinal);
        Assert.Contains("**bold**", markdown, System.StringComparison.Ordinal);

        // The tags themselves are gone - this is the whole point of the transform.
        Assert.DoesNotContain("<b>", markdown, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", markdown, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Plain text has no markup to convert, and running it through an importer
    /// would escape characters the user typed deliberately. Returning it
    /// unchanged is the behaviour.
    /// </summary>
    /// <remarks>
    /// The generics cases are the ones that matter for a clipboard manager: a
    /// large share of what people copy is source code, and an opening angle
    /// bracket followed by a letter is not evidence of markup. Caught by a
    /// reviewer against a probe of my own that had exactly this bug - and the
    /// predicate now used carries a comment naming these same examples, because
    /// this application had already learned it once.
    /// </remarks>
    [Theory]
    [InlineData("just a sentence")]
    [InlineData("a < b && c > d")]
    [InlineData("cost: $5 * 3 = $15_total")]
    [InlineData("List<string> names = new();")]
    [InlineData("Template<int> t;")]
    [InlineData("if (a < b && b > c) { return; }")]
    [InlineData("Dictionary<string, List<int>> map;")]
    [InlineData("fn parse<T: FromStr>(s: &str) -> Result<T, T::Err>")]
    public void HtmlToMarkdown_LeavesTextThatIsNotHtmlAlone(string input)
    {
        Assert.Equal(input, Apply(TextTransformation.HtmlToMarkdown, input));
    }

    [Fact]
    public void MarkdownToHtml_ConvertsEmphasisAndHeadings()
    {
        var html = Apply(TextTransformation.MarkdownToHtml, "# Title\n\nSome **bold** text.");

        Assert.Contains("Title", html, System.StringComparison.Ordinal);
        Assert.Contains("<", html, System.StringComparison.Ordinal);
        Assert.DoesNotContain("**bold**", html, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The security posture, stated as a test rather than left to the package
    /// default. This output goes to the clipboard as rich content and is
    /// rendered by whatever application receives it, and the input is arbitrary
    /// text from an arbitrary source.
    /// </summary>
    /// <remarks>
    /// VellumText's MarkdownImportOptions.Default sets AllowRawHtml = false and
    /// this transform deliberately does not override it. If a future version
    /// flips that default, this fails - which is the point, because nothing else
    /// here would notice.
    /// </remarks>
    [Theory]
    [InlineData("before\n\n<script>stealTheClipboard()</script>\n\nafter", "stealTheClipboard")]
    [InlineData("before\n\n<style>.x{color:red}</style>\n\nafter", "color:red")]
    [InlineData("before <img src=x onerror=alert(1)> after", "onerror")]
    public void MarkdownToHtml_DoesNotPassEmbeddedRawHtmlThrough(string markdown, string forbidden)
    {
        var html = Apply(TextTransformation.MarkdownToHtml, markdown);

        Assert.DoesNotContain(forbidden, html, System.StringComparison.OrdinalIgnoreCase);

        // The premise: the surrounding document really did convert, so a
        // converter that returned nothing cannot be what satisfied the check.
        Assert.Contains("before", html, System.StringComparison.Ordinal);
        Assert.Contains("after", html, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Both transforms must survive input they cannot make sense of, because a
    /// clipboard holds whatever the last application put there. Returning the
    /// input beats throwing inside a menu command.
    /// </summary>
    [Theory]
    [InlineData(TextTransformation.HtmlToMarkdown, "<<<>>> not really markup <")]
    [InlineData(TextTransformation.MarkdownToHtml, "[unclosed](")]
    [InlineData(TextTransformation.HtmlToMarkdown, "")]
    [InlineData(TextTransformation.MarkdownToHtml, "")]
    public void EitherDirection_SurvivesInputItCannotConvert(TextTransformation transformation, string input)
    {
        var result = Apply(transformation, input);
        Assert.NotNull(result);
    }

    /// <summary>
    /// The HTML-producing set has to be one list. Two call sites decided this
    /// independently with a hand-written comparison against a single member, so
    /// adding an HTML-producing transform meant remembering both - and forgetting
    /// one would write markup to the clipboard as plain text, silently.
    /// </summary>
    [Fact]
    public void ProducesHtml_IsTrueForExactlyTheTransformsThatEmitMarkup()
    {
        Assert.True(TextTransformationService.ProducesHtml(TextTransformation.BoxTableToHtml));
        Assert.True(TextTransformationService.ProducesHtml(TextTransformation.MarkdownToHtml));

        // The direction that produces Markdown is plain text, not markup.
        Assert.False(TextTransformationService.ProducesHtml(TextTransformation.HtmlToMarkdown));
        Assert.False(TextTransformationService.ProducesHtml(TextTransformation.UpperCase));
        Assert.False(TextTransformationService.ProducesHtml(TextTransformation.JsonPretty));
    }
}
