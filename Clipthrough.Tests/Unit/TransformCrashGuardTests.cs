using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Markdown→HTML and HTML→Markdown on input that would overflow the
/// importer's stack (F-48). A stack overflow cannot be caught: 8 KB of '['
/// killed the whole process with 0xC00000FD. The dangerous call therefore runs
/// in a child process - this test executable re-entered through
/// <see cref="ChildEntry"/> - so an unguarded build fails the test by exit
/// code instead of taking the test run down with it.
///
/// HTML->Markdown has the same guard but no crash test: on VellumText 0.7.0
/// its importer already survives the nesting (20,000 levels, exit 0), so a
/// child-process test of it could not fail. Only its refusal is asserted.
/// </summary>
public sealed class TransformCrashGuardTests
{
    private const string PayloadVariable = "CLIPTHROUGH_TRANSFORM_CHILD_PAYLOAD";
    private const string TransformVariable = "CLIPTHROUGH_TRANSFORM_CHILD_TRANSFORMATION";
    private const string OutputVariable = "CLIPTHROUGH_TRANSFORM_CHILD_OUTPUT";
    private const int StackOverflowExitCode = unchecked((int)0xC00000FD);

    private static readonly string Brackets = new('[', 8192);
    private static readonly string NestedTemplates = string.Concat(Enumerable.Repeat("<template>", 5000)) + "x";

    /// <summary>
    /// Not a test in its own right: the body the child process runs. Skipped in
    /// every ordinary run.
    /// </summary>
    [Fact]
    public void ChildEntry()
    {
        var payload = Environment.GetEnvironmentVariable(PayloadVariable);
        Assert.SkipUnless(payload is not null, "Runs only as the child process of TransformCrashGuardTests.");

        var transformation = Enum.Parse<TextTransformation>(Environment.GetEnvironmentVariable(TransformVariable)!);
        var result = TextTransformationService.Apply(transformation, File.ReadAllText(payload!));
        File.WriteAllText(Environment.GetEnvironmentVariable(OutputVariable)!, result);
    }

    private static (int ExitCode, string? Output) RunInChildProcess(TextTransformation transformation, string input)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-transform-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var payloadPath = Path.Combine(dir, "payload.txt");
            var outputPath = Path.Combine(dir, "output.txt");
            File.WriteAllText(payloadPath, input);

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
            start.ArgumentList.Add($"{typeof(TransformCrashGuardTests).FullName}.{nameof(ChildEntry)}");
            start.ArgumentList.Add("-noLogo");
            start.Environment[PayloadVariable] = payloadPath;
            start.Environment[TransformVariable] = transformation.ToString();
            start.Environment[OutputVariable] = outputPath;

            using var process = Process.Start(start)!;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(90)))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The child process did not finish.");
            }

            return (process.ExitCode, File.Exists(outputPath) ? File.ReadAllText(outputPath) : null);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }

    [Fact]
    public void MarkdownToHtml_OnDeeplyNestedBrackets_LeavesTheTextUnchangedInsteadOfCrashing()
    {
        var (exitCode, output) = RunInChildProcess(TextTransformation.MarkdownToHtml, Brackets);

        Assert.NotEqual(StackOverflowExitCode, exitCode);
        Assert.Equal(0, exitCode);
        Assert.Equal(Brackets, output);
    }


    [Fact]
    public void RefusalReason_NamesTheRefusalOnlyForTheConvertersAndOnlyWhenTheyRefuse()
    {
        Assert.Equal(Localization.AppText.TransformInputTooDeeplyNested, TextTransformationService.RefusalReason(TextTransformation.MarkdownToHtml, Brackets));
        Assert.Equal(Localization.AppText.TransformInputTooDeeplyNested, TextTransformationService.RefusalReason(TextTransformation.HtmlToMarkdown, NestedTemplates));
        Assert.Null(TextTransformationService.RefusalReason(TextTransformation.UpperCase, Brackets));
        Assert.Null(TextTransformationService.RefusalReason(TextTransformation.MarkdownToHtml, "# Title\n\nSome **bold** text."));
    }

    /// <summary>
    /// Anti-vacuity: nesting well inside the limit still converts, so the guard
    /// is a bound, not a refusal of anything bracketed.
    /// </summary>
    [Fact]
    public void ModerateNesting_StillConverts()
    {
        var input = new string('>', 50) + " deeply quoted text";

        var output = TextTransformationService.Apply(TextTransformation.MarkdownToHtml, input);

        Assert.True(ImportGuards.NestingDepth(input, ImportGuards.MaxNestingDepth) >= 50, "Setup: the fixture should be deeply nested.");
        Assert.NotEqual(input, output);
        Assert.Contains("deeply quoted text", output, StringComparison.Ordinal);
        Assert.Contains("<blockquote", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8192, true)]
    [InlineData(500, false)]
    public void NestingDepth_CountsOpenBrackets(int count, bool overLimit)
        => Assert.Equal(overLimit, ImportGuards.NestingDepth(new string('[', count), ImportGuards.MaxNestingDepth) > ImportGuards.MaxNestingDepth);

    [Fact]
    public void NestingDepth_DoesNotMistakeALongDocumentForADeepOne()
    {
        var manyBreaks = string.Concat(Enumerable.Repeat("line<br>", 5000));
        var unclosedItems = "<ul>" + string.Concat(Enumerable.Repeat("<li>item", 5000)) + "</ul>";
        var balanced = string.Concat(Enumerable.Repeat("<div><span>x</span></div>", 5000));
        var selfClosing = string.Concat(Enumerable.Repeat("<x/>", 5000));

        Assert.True(ImportGuards.NestingDepth(manyBreaks, ImportGuards.MaxNestingDepth) <= 1);
        Assert.True(ImportGuards.NestingDepth(unclosedItems, ImportGuards.MaxNestingDepth) <= 1);
        Assert.True(ImportGuards.NestingDepth(balanced, ImportGuards.MaxNestingDepth) <= 2);
        Assert.True(ImportGuards.NestingDepth(selfClosing, ImportGuards.MaxNestingDepth) <= 1);
    }

    [Fact]
    public void NestingDepth_IsNotFooledByStrayEndTags()
    {
        var disguised = string.Concat(Enumerable.Repeat("<template></p>", 5000));

        Assert.True(ImportGuards.NestingDepth(disguised, ImportGuards.MaxNestingDepth) > ImportGuards.MaxNestingDepth);
    }

    [Fact]
    public void NestingDepth_CountsBlockquoteMarkers()
    {
        var quoted = new string('>', 2000) + " deep";

        Assert.True(ImportGuards.NestingDepth(quoted, ImportGuards.MaxNestingDepth) > ImportGuards.MaxNestingDepth);
    }
}
