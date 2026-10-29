using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// ReactiveUI's <c>RaiseAndSetIfChanged</c> returns the <em>new value</em>, not
/// whether it changed. On a <c>bool</c> field, <c>if (!this.RaiseAndSetIfChanged(ref _flag, value)) return;</c>
/// therefore compiles, reads naturally, and returns early on every
/// <c>false</c> - which is how closing the welcome screen and the password
/// prompt left the main window blank (audit F-46). For any other field type the
/// misuse would not compile, so bool setters are the only place it can hide.
/// </summary>
/// <remarks>
/// The result is never needed: the field already holds the value. So the rule
/// is simply that the call is a statement or the body of a setter. When a setter
/// needs to know whether anything changed, compare against the field first.
/// </remarks>
public sealed class RaiseAndSetIfChangedUsageTests
{
    private const string Call = "RaiseAndSetIfChanged(";

    [Fact]
    public void NoProductionCodeConsumesTheReturnValue()
    {
        var root = Path.Combine(RepositoryRoot(), "Clipthrough");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .ToList();

        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(Call, StringComparison.Ordinal))
                {
                    continue;
                }

                calls++;
                if (ConsumesReturnValue(lines, i))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        // Guards against a wrong root or an over-eager exclusion turning this
        // into a scan of nothing.
        Assert.True(calls > 50, $"only {calls} calls found under {root}; the scan is not looking at the view models");
        Assert.True(
            offenders.Count == 0,
            "RaiseAndSetIfChanged returns the new value, not a changed flag. Compare against the field before setting instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("        set => this.RaiseAndSetIfChanged(ref _flag, value);")]
    [InlineData("        private set => this.RaiseAndSetIfChanged(ref _flag, value);")]
    [InlineData("            this.RaiseAndSetIfChanged(ref _flag, value);")]
    public void TheDetectorAcceptsStatementsAndSetterBodies(string line)
        => Assert.False(ConsumesReturnValue(["    {", line], 1));

    [Theory]
    [InlineData("            if (!this.RaiseAndSetIfChanged(ref _flag, value))")]
    [InlineData("            var changed = this.RaiseAndSetIfChanged(ref _flag, value);")]
    [InlineData("            return this.RaiseAndSetIfChanged(ref _flag, value);")]
    [InlineData("            if (this.RaiseAndSetIfChanged(ref _flag, value) && other)")]
    [InlineData("    public bool Set(bool value) => this.RaiseAndSetIfChanged(ref _flag, value);")]
    public void TheDetectorFlagsAConsumedResult(string line)
        => Assert.True(ConsumesReturnValue(["    {", line], 1));

    [Fact]
    public void TheDetectorFlagsAResultConsumedAcrossALineBreak()
        => Assert.True(ConsumesReturnValue(
            ["            var changed =", "                this.RaiseAndSetIfChanged(ref _flag, value);"],
            1));

    private static readonly Regex SetterBody = new(@"^((private|protected|internal)\s+)*(set|init)\s*=>$", RegexOptions.CultureInvariant);

    private static readonly char[] ContinuationEndings = ['=', '(', ',', '&', '|', '?', ':', '!'];

    private static bool ConsumesReturnValue(IReadOnlyList<string> lines, int index)
    {
        var line = lines[index];
        var callAt = line.IndexOf(Call, StringComparison.Ordinal);
        var prefix = line[..callAt].Trim();
        if (prefix.EndsWith("this.", StringComparison.Ordinal))
        {
            prefix = prefix[..^"this.".Length].TrimEnd();
        }

        if (SetterBody.IsMatch(prefix))
        {
            return false;
        }

        if (prefix.Length > 0)
        {
            return true;
        }

        // The call opens its line; it is a statement unless the previous line
        // left an expression open.
        for (var i = index - 1; i >= 0; i--)
        {
            var previous = lines[i].Trim();
            if (previous.Length == 0 || previous.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            return previous.IndexOfAny(ContinuationEndings, previous.Length - 1) >= 0
                || previous.EndsWith("return", StringComparison.Ordinal)
                || previous.EndsWith("=>", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "obj" or "bin";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Clipthrough", "Clipthrough.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The repository root was not found above " + AppContext.BaseDirectory +
            "; this test cannot silently pass without it.");
    }
}
