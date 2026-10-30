using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Clipthrough keeps its files in one folder, chosen once at startup by
/// AppDataRootResolver from --data-dir, CLIPTHROUGH_DATA_DIR, a portable
/// marker or the default, and injected as AppDataRoot. A service that asks
/// Windows for local application data itself silently ignores that choice:
/// with --data-dir set, its file still lands in %LOCALAPPDATA% - the live
/// checks that rely on the option for isolation would then share state with
/// the user's real install. Seven sites did exactly that before the root
/// existed. This scan keeps the resolver the only one.
///
/// Tests are scanned too, because a test that asks for the real folder reads
/// or writes the user's real settings.
/// </summary>
public sealed class AppDataRootGuardTests
{
    private const string Owner = "AppDataRootResolver.cs";

    // Each rule breaks its own literal so this file does not match itself.
    private static readonly Regex LocalAppDataFolder = new(
        @"\bSpecialFolder\s*\.\s*Local" + "ApplicationData\\b",
        RegexOptions.CultureInvariant);

    // The environment-variable routes to the same folder. ClipAngel's importer
    // expands %LOCALAPPDATA%\ClipAngel - another application's data, which is
    // where it is whatever folder Clipthrough uses - so only a Clipthrough
    // path, or a bare read of the variable, is a bypass.
    private static readonly Regex LocalAppDataVariable = new(
        @"\bGetEnvironmentVariable\s*\(\s*""LOCAL" + @"APPDATA""\s*\)"
        + @"|\bExpandEnvironmentVariables\s*\(\s*[@$]*""[^""\n]*%LOCAL" + @"APPDATA%[\\/]*Clipthrough",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

        throw new InvalidOperationException("Clipthrough/Clipthrough.csproj was not found above " + AppContext.BaseDirectory + "; this guard cannot silently pass without it.");
    }

    private static string[] FilesMatching(Regex rule, string project)
    {
        var root = Path.Combine(RepositoryRoot(), project);
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                     && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                     && !f.Contains($"{separator}artifacts{separator}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(files.Length > 40, $"Only {files.Length} source files under {root}; the scan is looking in the wrong place.");
        return [.. files.Where(f => rule.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).Order()!];
    }

    [Fact]
    public void OnlyTheResolver_AsksForLocalApplicationData()
        => Assert.Equal([Owner], FilesMatching(LocalAppDataFolder, "Clipthrough"));

    [Fact]
    public void NothingBuildsAClipthroughPathFromTheLocalAppDataVariable()
        => Assert.Empty(FilesMatching(LocalAppDataVariable, "Clipthrough"));

    [Fact]
    public void TestsNeverAskForTheRealFolder()
    {
        Assert.Empty(FilesMatching(LocalAppDataFolder, "Clipthrough.Tests"));
        Assert.Empty(FilesMatching(LocalAppDataVariable, "Clipthrough.Tests"));
    }

    private const string Folder = "Local" + "ApplicationData";
    private const string Variable = "LOCAL" + "APPDATA";

    [Theory]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder." + Folder + ")")]
    [InlineData("GetFolderPath(SpecialFolder." + Folder + ")")]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder\n    ." + Folder + ")")]
    [InlineData("GetFolderPath(System.Environment.SpecialFolder . " + Folder + ", Environment.SpecialFolderOption.Create)")]
    public void FolderRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(LocalAppDataFolder, snippet);

    [Theory]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)")]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)")]
    public void FolderRule_LeavesOtherFoldersAlone(string snippet)
        => Assert.DoesNotMatch(LocalAppDataFolder, snippet);

    [Theory]
    [InlineData("Environment.GetEnvironmentVariable(\"" + Variable + "\")")]
    [InlineData("Environment.ExpandEnvironmentVariables(\"%" + Variable + "%\\\\Clipthrough\\\\settings.json\")")]
    [InlineData("Environment.ExpandEnvironmentVariables(@\"%" + Variable + "%\\Clipthrough\")")]
    [InlineData("Environment.ExpandEnvironmentVariables($\"%" + Variable + "%/Clipthrough/{name}\")")]
    public void VariableRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(LocalAppDataVariable, snippet);

    [Theory]
    [InlineData("Environment.ExpandEnvironmentVariables(\"%" + Variable + "%\\\\ClipAngel\")")]
    [InlineData("\"  --data-dir <path>   instead of %" + Variable + "%\\\\Clipthrough.\"")]
    public void VariableRule_LeavesClipAngelAndProseAlone(string snippet)
        => Assert.DoesNotMatch(LocalAppDataVariable, snippet);
}
