using System;
using System.Threading;
using System.Threading.Tasks;
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
    private static Task<string> ApplyAsync(TextTransformation transformation, string input)
        => TextTransformationService.ApplyAsync(transformation, input);

    [Fact]
    public async Task HtmlToMarkdown_ConvertsEmphasisAndHeadings()
    {
        var markdown = await ApplyAsync(
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
    public async Task HtmlToMarkdown_LeavesTextThatIsNotHtmlAlone(string input)
    {
        Assert.Equal(input, await ApplyAsync(TextTransformation.HtmlToMarkdown, input));
    }

    [Fact]
    public async Task MarkdownToHtml_ConvertsEmphasisAndHeadings()
    {
        var html = await ApplyAsync(TextTransformation.MarkdownToHtml, "# Title\n\nSome **bold** text.");

        Assert.Contains("Title", html, System.StringComparison.Ordinal);
        Assert.Contains("<", html, System.StringComparison.Ordinal);
        Assert.DoesNotContain("**bold**", html, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Live check C3 (2026-10-29): a Markdown -> HTML clip's text began
    /// "Version:0.9 StartHTML:...". VellumText's HtmlFormat.Export returns a
    /// CF_HTML clipboard document, header included, and the transform stored it
    /// as the clip. The result is the HTML fragment, as BoxTableToHtml's is;
    /// the clipboard copy adds its own header (RichClipboardFormatting.BuildCfHtml).
    /// </summary>
    [Fact]
    public async Task MarkdownToHtml_ReturnsTheFragment_NotAClipboardDocument()
    {
        var html = await ApplyAsync(TextTransformation.MarkdownToHtml, "# Title\n\nSome **bold** text.");

        Assert.False(Clipthrough.Services.Platform.RichClipboardFormatting.LooksLikeCfHtml(html), html);
        Assert.DoesNotContain("StartFragment", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<", html.TrimStart(), System.StringComparison.Ordinal);
        Assert.Contains("<h1", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bold", html, System.StringComparison.Ordinal);
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
    public async Task MarkdownToHtml_DoesNotPassEmbeddedRawHtmlThrough(string markdown, string forbidden)
    {
        var html = await ApplyAsync(TextTransformation.MarkdownToHtml, markdown);

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
    public async Task EitherDirection_SurvivesInputItCannotConvert(TextTransformation transformation, string input)
    {
        var result = await ApplyAsync(transformation, input);
        Assert.NotNull(result);
    }

    /// <summary>
    /// The conversion finishes without the caller's thread. VellumText's
    /// importers are async-only; the export that follows them is synchronous,
    /// and it must not be marshalled back to the thread that awaited - in the
    /// app, the UI thread - where it would cost the very freeze the async import
    /// exists to remove.
    /// </summary>
    /// <remarks>
    /// A synchronization context that never runs what is posted to it stands
    /// in for a UI thread that is busy. Any continuation that insists on coming
    /// back to it never runs, and the conversion never completes; one that
    /// resumes on the pool completes as normal. That makes "where does the work
    /// after the import run" observable without timing anything.
    /// </remarks>
    [Theory]
    [InlineData(TextTransformation.MarkdownToHtml, "# Title\n\nSome **bold** text.")]
    [InlineData(TextTransformation.HtmlToMarkdown, "<h1>Title</h1><p>Some <b>bold</b> text.</p>")]
    public async Task Conversion_CompletesWithoutTheCallersThread(TextTransformation transformation, string input)
    {
        var previous = SynchronizationContext.Current;
        var stuck = new NeverRunsContext();
        Task<string> conversion;
        SynchronizationContext.SetSynchronizationContext(stuck);
        try
        {
            conversion = TextTransformationService.ApplyAsync(transformation, input);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var finished = await Task.WhenAny(conversion, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(
            finished == conversion,
            $"The conversion waited for the caller's thread ({stuck.Posted} continuation(s) posted back to it).");
        Assert.NotEqual(input, await conversion);
    }

    private sealed class NeverRunsContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);
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
