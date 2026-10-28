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
/// The payloads come from the 2026-10-25 audit's live and harness runs. Two of
/// them got past every Clipthrough-side guard and are held only by VellumText
/// 0.8: CSS whose parentheses nest thousands deep (no guard here counts a
/// parenthesis, and with whitespace between them no unbroken run is long), and
/// a 1.2 KB table whose cells span 1000x1000 (the preview counts table cells
/// before importing; the HTML-&gt;Markdown transform never did). On VellumText
/// 0.7.0 the first killed the process with a stack overflow on both paths and
/// the second cost seconds and gigabytes in the transform.
/// </para>
/// <para>
/// The others are already refused or bounded by guards of ours, and are here
/// so that removing one of those guards later is judged against the library
/// rather than assumed safe.
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

    // ---- already guarded on our side; pinned against the library ---------

    [Fact]
    public void NestedTemplates_ReachesTheConverterOnlyPastOurNestingGuard()
    {
        Assert.True(Services.Platform.RichClipboardFormatting.LooksLikeHtml(NestedTemplates),
            "Setup: HTML->Markdown must treat the payload as markup, or its guard is never consulted.");
        Assert.False(Presentation.ImportGuards.IsSafeToImport(NestedTemplates));
    }

    [Fact]
    public void SpanBomb_Preview_Survives()
        => AssertSurvived($"preview:{ClipContentFormat.Html}", SpanBomb);

    [Fact]
    public void Brackets_MarkdownToHtml_Survives()
        => AssertSurvived($"transform:{TextTransformation.MarkdownToHtml}", Brackets);

    [Fact]
    public void NestedTemplates_HtmlToMarkdown_Survives()
        => AssertSurvived($"transform:{TextTransformation.HtmlToMarkdown}", NestedTemplates);

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
