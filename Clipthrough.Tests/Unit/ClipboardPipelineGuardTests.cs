using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Guard R1 (audit 2026-10-25): clip -> transform -> clipboard was implemented
/// three times, and two copies drifted. F-04 was one of them deciding "is this
/// HTML?" by comparing against <c>TextTransformation.BoxTableToHtml</c> after
/// a second HTML-producing transform existed. The answer has exactly one
/// owner, <c>TextTransformationService.ProducesHtml</c>; this fails the build
/// the moment a caller starts answering it for itself again.
/// </summary>
public sealed class ClipboardPipelineGuardTests
{
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Clipthrough", "Clipthrough.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Clipthrough/Clipthrough.csproj was not found above " + AppContext.BaseDirectory +
            "; this guard cannot silently pass without the source.");
    }

    private static readonly Regex HtmlByComparison = new(
        @"[=!]=\s*(Models\.)?TextTransformation\.BoxTableToHtml\b|\bTextTransformation\.BoxTableToHtml\s*[=!]=|\bis\s+(not\s+)?(Models\.)?TextTransformation\.BoxTableToHtml\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void NoCallerDecidesHtmlOutputByComparingAgainstOneTransform()
    {
        var root = SourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(path), "TextTransformationService.cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, index))
                .Where(entry => HtmlByComparison.IsMatch(entry.line)))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Use TextTransformationService.ProducesHtml instead of comparing against one transform:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheGuardRecognisesTheComparisonItForbids()
    {
        // Keeps the regex honest: a pattern that matches nothing would pass forever.
        Assert.Matches(HtmlByComparison, "var isHtml = transformation == TextTransformation.BoxTableToHtml;");
        Assert.Matches(HtmlByComparison, "if (TextTransformation.BoxTableToHtml != transformation)");
        Assert.Matches(HtmlByComparison, "var isHtml = transformation is Models.TextTransformation.BoxTableToHtml;");
        Assert.DoesNotMatch(HtmlByComparison, "TextTransformation.BoxTableToHtml => \"text table -> HTML\",");
    }
}
