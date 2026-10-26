using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Guard R7 (F-28). Connection construction and busy_timeout were re-implemented
/// at seven sites, and the copies drifted: f450d9a's integrity check left the
/// pragma out and failed with "database is locked". Every connection is now
/// built by SqliteConnectionFactory, and this scan keeps it that way.
/// </summary>
public sealed class ConnectionConstructionGuardTests
{
    private const string Owner = "SqliteConnectionFactory.cs";

    private static string ProductionSourceRoot()
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

        throw new InvalidOperationException("Clipthrough/Clipthrough.csproj was not found above " + AppContext.BaseDirectory + "; this guard cannot silently pass without it.");
    }

    private static string[] FilesContaining(string needle)
    {
        var root = ProductionSourceRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(files.Length > 50, $"Only {files.Length} source files under {root}; the scan is looking in the wrong place.");
        return [.. files.Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal)).Select(Path.GetFileName).Order()!];
    }

    [Fact]
    public void OnlyTheFactory_SetsBusyTimeout()
        => Assert.Equal([Owner], FilesContaining("busy_timeout ="));

    [Fact]
    public void OnlyTheFactory_ConstructsAConnection()
        => Assert.Equal([Owner], FilesContaining("new SqliteConnection("));
}