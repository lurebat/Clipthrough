using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Controls;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Hostile clipboard markup fed through the two places Clipthrough hands it to
/// VellumText: the rich preview and the HTML/Markdown converters. Each case
/// runs in a child process - this test executable re-entered through one of
/// the <c>Child*</c> entries - and is judged by the child's exit code, wall
/// time and peak working set. A stack overflow cannot be caught and a test
/// host that dies of one can still print "Passed!", so nothing here trusts an
/// in-process result.
/// </summary>
/// <remarks>
/// <para>
/// The payloads come from the 2026-10-25 audit's live and harness runs. Four
/// of them reach VellumText with no Clipthrough guard in the way, and are held
/// by VellumText 0.8 alone: CSS whose parentheses nest thousands deep (no
/// guard here counts a parenthesis, and with whitespace between them no
/// unbroken run is long); a 1.2 KB table whose cells span 1000x1000, through
/// both the preview and HTML-&gt;Markdown; and deeply nested markup through
/// HTML-&gt;Markdown. On VellumText 0.7.0 the CSS killed the process with a
/// stack overflow on both paths and the span bomb cost seconds and gigabytes.
/// The preview's table-cell pre-screen and HTML-&gt;Markdown's nesting refusal
/// once stood in front of the last two; 0.8 made them redundant and they were
/// removed (owner decision on #12), so these tests are now what pins the
/// library to that job.
/// </para>
/// <para>
/// The rest are still refused or bounded by guards of ours that were kept on
/// purpose - Markdown-&gt;HTML's nesting refusal for its user-facing message,
/// and the preview's unbroken-run guard because it bounds Avalonia's line
/// breaking, which the importer does not - and are here so that removing one
/// of those later is judged against the library rather than assumed safe.
/// </para>
/// </remarks>
public sealed class HostileRichPayloadTests
{
    private const string ModeVariable = "CLIPTHROUGH_HOSTILE_CHILD_MODE";
    private const string PayloadVariable = "CLIPTHROUGH_HOSTILE_CHILD_PAYLOAD";
    private const string OutputVariable = "CLIPTHROUGH_HOSTILE_CHILD_OUTPUT";
    private const int StackOverflowExitCode = unchecked((int)0xC00000FD);

    // Generous enough to never trip on a loaded machine; what it catches is a
    // hang, not a slow run. The discriminating assertions are exit code and
    // memory.
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(120);

    // The child's whole working set, test host included. VellumText 0.7.0
    // peaked at 1.5 GB on the span bomb; an ordinary run is a fraction of this.
    private const long MaxPeakWorkingSetBytes = 768L * 1024 * 1024;

    // The UI thread's stack. The app's transforms start there; using the
    // smallest stack the app runs importers on keeps the test conservative.
    private const int UiThreadStackBytes = 1024 * 1024;

    internal static readonly string CssParenNesting =
        "<p style=\"font-size:calc(" + string.Join(' ', Enumerable.Repeat('(', 20_000)) + " 1px "
        + string.Join(' ', Enumerable.Repeat(')', 20_000)) + ")\">x</p>";

    internal static readonly string SpanBomb =
        "<table><tr>" + string.Concat(Enumerable.Repeat("<td colspan=1000 rowspan=1000>x</td>", 32)) + "</tr></table>";

    internal static readonly string Brackets = new('[', 8192);

    // Closed once at the end: LooksLikeHtml wants a closing tag, and without
    // one HTML->Markdown leaves the text alone before any guard is consulted,
    // which would make this payload prove nothing about that path.
    internal static readonly string NestedTemplates = string.Concat(Enumerable.Repeat("<template>", 5000)) + "x</template>";

    internal static readonly string NestedDivs =
        string.Concat(Enumerable.Repeat("<div>", 20_000)) + "deep text" + string.Concat(Enumerable.Repeat("</div>", 20_000));

    internal static readonly string ListStartNearIntMax =
        "<ol start=2147483646><li>a</li><li>b</li><li>c</li><li>d</li></ol>";

    private sealed record ChildReport(long PeakWorkingSet, double OperationMs, string Summary);

    private sealed record ChildResult(int ExitCode, TimeSpan Wall, ChildReport? Report);

    // ---- child entries --------------------------------------------------

    /// <summary>
    /// Not a test in its own right: the transform body the child process
    /// runs. Skipped in every ordinary run.
    /// </summary>
    [Fact]
    public void ChildTransform()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        Assert.SkipUnless(mode is not null && mode.StartsWith("transform:", StringComparison.Ordinal),
            "Runs only as the child process of HostileRichPayloadTests.");

        var transformation = Enum.Parse<TextTransformation>(mode!["transform:".Length..]);
        var input = File.ReadAllText(Environment.GetEnvironmentVariable(PayloadVariable)!);

        string? output = null;
        var stopwatch = Stopwatch.StartNew();
        // A thread with the UI thread's stack, as the app's transforms start on
        // one. Blocking here is the test child's own thread, never a UI thread.
        var thread = new Thread(
            () => output = TextTransformationService.ApplyAsync(transformation, input).GetAwaiter().GetResult(),
            UiThreadStackBytes);
        thread.Start();
        thread.Join();
        stopwatch.Stop();

        var summary = output is null ? "null"
            : string.Equals(output, input, StringComparison.Ordinal) ? "unchanged"
            : $"converted ({output.Length} chars)";
        WriteReport(stopwatch.Elapsed, summary);
    }

    /// <summary>
    /// Not a test in its own right: the preview body the child process runs.
    /// The real control, its guards and its importer; only the timeout is
    /// raised, so the child waits for the import it would otherwise abandon
    /// and its cost is measured rather than hidden behind a fallback.
    /// </summary>
    [AvaloniaFact]
    public async Task ChildPreview()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        Assert.SkipUnless(mode is not null && mode.StartsWith("preview:", StringComparison.Ordinal),
            "Runs only as the child process of HostileRichPayloadTests.");

        var format = Enum.Parse<ClipContentFormat>(mode!["preview:".Length..]);
        var input = File.ReadAllText(Environment.GetEnvironmentVariable(PayloadVariable)!);

        var stopwatch = Stopwatch.StartNew();
        var view = new RichDocumentView { ImportTimeout = ChildTimeout, ContentFormat = format, Markup = input };
        await view.PendingRender;
        stopwatch.Stop();

        WriteReport(stopwatch.Elapsed, view.Viewer.IsVisible ? "rendered" : view.FallbackText.IsVisible ? "fallback" : "empty");
    }

    private static void WriteReport(TimeSpan elapsed, string summary)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        var report = new ChildReport(self.PeakWorkingSet64, elapsed.TotalMilliseconds, summary);
        File.WriteAllText(Environment.GetEnvironmentVariable(OutputVariable)!, JsonSerializer.Serialize(report));
    }

    // ---- parent side ----------------------------------------------------

    private static ChildResult RunInChildProcess(string mode, string payload)
    {
        var entry = mode.StartsWith("preview:", StringComparison.Ordinal) ? nameof(ChildPreview) : nameof(ChildTransform);
        var dir = Path.Combine(Path.GetTempPath(), "ct-hostile-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var payloadPath = Path.Combine(dir, "payload.txt");
            var outputPath = Path.Combine(dir, "report.json");
            File.WriteAllText(payloadPath, payload);

            var exe = Path.Combine(AppContext.BaseDirectory, "Clipthrough.Tests.exe");
            Assert.True(File.Exists(exe), $"The test executable was not found at {exe}.");
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add($"{typeof(HostileRichPayloadTests).FullName}.{entry}");
            start.ArgumentList.Add("-noLogo");
            start.Environment[ModeVariable] = mode;
            start.Environment[PayloadVariable] = payloadPath;
            start.Environment[OutputVariable] = outputPath;

            var wall = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ChildTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"{mode}: the child process did not finish within {ChildTimeout.TotalSeconds:N0} s.");
            }

            wall.Stop();
            var report = File.Exists(outputPath)
                ? JsonSerializer.Deserialize<ChildReport>(File.ReadAllText(outputPath))
                : null;
            return new ChildResult(process.ExitCode, wall.Elapsed, report);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }

    private static ChildReport AssertSurvived(string mode, string payload)
    {
        var result = RunInChildProcess(mode, payload);
        var detail = $"{mode}: exit 0x{result.ExitCode:X8}, wall {result.Wall.TotalSeconds:N1} s, "
            + (result.Report is { } r ? $"op {r.OperationMs:N0} ms, peak {r.PeakWorkingSet / (1024 * 1024)} MB, {r.Summary}" : "no report");

        Assert.False(result.ExitCode == StackOverflowExitCode, $"Stack overflow. {detail}");
        Assert.True(result.ExitCode == 0, detail);
        Assert.True(result.Report is not null, detail);
        Assert.True(result.Report!.PeakWorkingSet <= MaxPeakWorkingSetBytes, detail);
        TestContext.Current.TestOutputHelper?.WriteLine(detail);
        return result.Report;
    }

    // ---- held by VellumText 0.8 alone ------------------------------------

    [Fact]
    public void CssParenNesting_PassesEveryGuardOfOurs()
    {
        Assert.True(Presentation.ImportGuards.IsSafeToImport(CssParenNesting),
            "Setup: the payload must reach the importer, or the tests below prove nothing about it.");
        Assert.True(CssParenNesting.Length < 512 * 1024);
    }

    [Fact]
    public void CssParenNesting_HtmlToMarkdown_Survives()
    {
        var report = AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", CssParenNesting);

        Assert.StartsWith("converted", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CssParenNesting_Preview_Survives()
    {
        var report = AssertSurvived($"preview:{ClipContentFormat.Html}", CssParenNesting);

        Assert.Equal("rendered", report.Summary);
    }

    [Fact]
    public void SpanBomb_HtmlToMarkdown_StaysWithinMemory()
        => AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", SpanBomb);

    /// <summary>
    /// The preview used to count table cells and fall back before importing
    /// anything that expanded past 200,000 (F-47). VellumText 0.8 budgets table
    /// slots inside its importers, so that pre-screen was removed (owner
    /// decision on #12) and the span bomb now reaches the importer. Asserting
    /// "rendered" is what proves it does: the old pre-screen answered
    /// "fallback" without importing.
    /// </summary>
    [Fact]
    public void SpanBomb_Preview_ReachesTheImporterAndStaysWithinMemory()
    {
        var report = AssertSurvived($"preview:{ClipContentFormat.Html}", SpanBomb);

        Assert.Equal("rendered", report.Summary);
    }

    /// <summary>
    /// HTML-&gt;Markdown used to refuse markup nested past
    /// <see cref="Presentation.ImportGuards.MaxNestingDepth"/>. VellumText 0.8
    /// drops tags nested past its own depth limit, keeps their text, and parses
    /// on a 256 MiB stack, so that refusal was removed (owner decision on #12).
    /// 20,000 nested divs is the shape that cost 0.7.0's quadratic tree
    /// builder seconds; the text inside has to come out, which the old
    /// refusal - returning the input unchanged - never let it.
    /// </summary>
    [Fact]
    public void NestedDivs_HtmlToMarkdown_ConvertsWithinMemory()
    {
        Assert.False(Presentation.ImportGuards.IsSafeToImport(NestedDivs),
            "Setup: the payload must be one the removed guard would have refused.");

        var report = AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", NestedDivs);

        Assert.StartsWith("converted", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedTemplates_HtmlToMarkdown_Survives()
    {
        Assert.True(Services.Platform.RichClipboardFormatting.LooksLikeHtml(NestedTemplates),
            "Setup: HTML->Markdown must treat the payload as markup, or it never reaches the importer.");

        AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", NestedTemplates);
    }

    // ---- still guarded on our side; pinned against the library -----------

    [Fact]
    public void Brackets_MarkdownToHtml_Survives()
        => AssertSurvived($"transform:{TextTransformation.MarkdownToHtml}", Brackets);

    [Fact]
    public void NestedTemplates_Preview_Survives()
        => AssertSurvived($"preview:{ClipContentFormat.Html}", NestedTemplates);

    [Fact]
    public void ListStartNearIntMax_PreviewAndTransform_Survive()
    {
        AssertSurvived($"preview:{ClipContentFormat.Html}", ListStartNearIntMax);
        AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", ListStartNearIntMax);
    }
}
