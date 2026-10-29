using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-42 / guard R4. The agent-facing docs named a schema file
/// (<c>Data/ClipSchema.cs</c>), a settings interface (<c>IAppSettingsService</c>)
/// and a menu table (<c>s_transformMenuEntries</c>) that had all stopped
/// existing, and an agent following them went looking for code that was not
/// there. This checks the three kinds of reference that rotted: file paths,
/// service interfaces and static fields, each written in backticks.
/// </summary>
public sealed class AgentDocReferenceTests
{
    private static readonly Regex Backticked = new("`([^`\\n]+)`", RegexOptions.Compiled);
    private static readonly Regex FilePath = new(@"^[\w./-]+\.(cs|axaml|md|ps1|json|yml|csproj)$", RegexOptions.Compiled);
    private static readonly Regex SourceFile = new(@"\.(cs|axaml|csproj|ps1)$", RegexOptions.Compiled);
    private static readonly Regex ServiceInterface = new(@"^I[A-Z]\w*Service$", RegexOptions.Compiled);
    private static readonly Regex StaticField = new(@"^s_[a-zA-Z]\w*$", RegexOptions.Compiled);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("AGENTS.md was not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Docs(string root)
    {
        yield return Path.Combine(root, "AGENTS.md");
        yield return Path.Combine(root, "CONCEPTS.md");
        yield return Path.Combine(root, ".github", "copilot-instructions.md");
        foreach (var skill in Directory.EnumerateFiles(Path.Combine(root, ".github", "copilot-cli-skills"), "*.md"))
        {
            yield return skill;
        }
    }

    private static string[] RepositoryFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith(".git/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/artifacts/", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void EveryReferenceInTheAgentDocs_NamesSomethingThatExists()
    {
        var root = RepositoryRoot();
        var files = RepositoryFiles(root);
        var source = string.Join('\n', files
            .Where(path => path.StartsWith("Clipthrough/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(path => File.ReadAllText(Path.Combine(root, path))));

        var missing = new List<string>();
        var checkedCount = 0;
        foreach (var doc in Docs(root))
        {
            foreach (Match match in Backticked.Matches(File.ReadAllText(doc)))
            {
                var token = match.Groups[1].Value.Trim();
                bool exists;
                // A bare settings.json or storage.json names a runtime file, not one in
                // the repository; only a source file or a path with a folder is checked.
                if (FilePath.IsMatch(token) && (token.Contains('/', StringComparison.Ordinal) || SourceFile.IsMatch(token)))
                {
                    exists = files.Any(path => path == token || path.EndsWith("/" + token, StringComparison.Ordinal));
                }
                else if (ServiceInterface.IsMatch(token))
                {
                    exists = Regex.IsMatch(source, @"\binterface\s+" + token + @"\b");
                }
                else if (StaticField.IsMatch(token))
                {
                    exists = Regex.IsMatch(source, @"\b" + token + @"\s*(=|;)");
                }
                else
                {
                    continue;
                }

                checkedCount++;
                if (!exists)
                {
                    missing.Add($"{Path.GetFileName(doc)}: `{token}`");
                }
            }
        }

        Assert.True(checkedCount >= 30, $"only {checkedCount} references were checked; the scan is broken");
        Assert.True(missing.Count == 0, "The agent docs name things that do not exist:\n" + string.Join('\n', missing.Distinct()));
    }
}
