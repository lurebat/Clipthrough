using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Guard R7 (F-28). Connection construction and busy_timeout were re-implemented
/// at seven sites, and the copies drifted: f450d9a's integrity check left the
/// pragma out and failed with "database is locked". Every connection is now
/// built by SqliteConnectionFactory, and this scan keeps it that way.
///
/// Since the move to SQLite3 Multiple Ciphers (U-C9) the factory is also the
/// only place the cipher recipe is added, so a bypass no longer just misses a
/// pragma: it writes an encrypted library in a cipher no SQLCipher build can
/// read. The scan therefore covers the test tree as well - a test that built
/// its own keyed connection would read its own file back and pass - and the
/// other routes to a connection: the ADO.NET provider factory and assigning a
/// connection string after construction.
/// </summary>
public sealed class ConnectionConstructionGuardTests
{
    private const string Owner = "SqliteConnectionFactory.cs";

    // Patterns, not substrings: a substring catches only the one spelling it
    // names, and C# has several - a target-typed new(), a qualified type name,
    // an object initializer, no space before the parenthesis or the '='. Each
    // pattern breaks its own literal with \s or a group, so this file does not
    // match its own rules. The spellings each rule must catch are pinned by the
    // *_CatchesEverySpelling theories below.
    private static readonly Regex NewConnection = new(
        // Explicit type, optionally qualified, with an argument list or an initializer.
        @"\bnew\s+(?:global::)?(?:\w+\s*\.\s*)*SqliteConnection\s*[({]"
        // Target-typed new() into a local, a field, a using declaration or an expression-bodied member.
        + @"|\bSqliteConnection\s*\??\s+\w+\s*(?:\([^()]*\)\s*)?=>?\s*new\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex ProviderFactory = new(@"\bSqliteFactory\s*\.", RegexOptions.CultureInvariant);
    private static readonly Regex BusyTimeout = new(@"\bbusy_timeout\s*[=(]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ConnectionStringAssignment = new(@"\.\s*ConnectionString\s*=(?!=)", RegexOptions.CultureInvariant);

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

    private static string[] FilesMatching(Regex rule, string? root = null)
    {
        root ??= ProductionSourceRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(files.Length > 40, $"Only {files.Length} source files under {root}; the scan is looking in the wrong place.");
        return [.. files.Where(f => rule.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).Order()!];
    }

    [Fact]
    public void OnlyTheFactory_SetsBusyTimeout()
        => Assert.Equal([Owner], FilesMatching(BusyTimeout));

    [Fact]
    public void OnlyTheFactory_ConstructsAConnection()
        => Assert.Equal([Owner], FilesMatching(NewConnection));

    [Fact]
    public void NoOtherRouteToAConnection_IsUsed()
    {
        Assert.Empty(FilesMatching(ProviderFactory));
        Assert.Empty(FilesMatching(ConnectionStringAssignment));
    }

    [Fact]
    public void TestsGetTheirConnectionsFromTheFactory_Too()
    {
        var testRoot = Path.Combine(Path.GetDirectoryName(ProductionSourceRoot())!, "Clipthrough.Tests");
        Assert.Empty(FilesMatching(NewConnection, testRoot));
        Assert.Empty(FilesMatching(ProviderFactory, testRoot));
    }

    // The scans above are only as strong as these rules, so each spelling of a
    // bypass is pinned here. The snippets are concatenated for the same reason
    // as the rules: a literal would make this file a violation of its own scan.
    private const string Conn = "Sqlite" + "Connection";

    [Theory]
    [InlineData("var c = new " + Conn + "(cs);")]
    [InlineData("var c = new " + Conn + " (cs);")]
    [InlineData("var c = new\n    " + Conn + "(cs);")]
    [InlineData("var c = new Microsoft.Data.Sqlite." + Conn + "(cs);")]
    [InlineData("var c = new global::Microsoft.Data.Sqlite." + Conn + "(cs);")]
    [InlineData("var c = new " + Conn + " { DefaultTimeout = 5 };")]
    [InlineData(Conn + " c = new(cs);")]
    [InlineData(Conn + "? c = new(cs);")]
    [InlineData("using " + Conn + " c = new (cs);")]
    [InlineData("Microsoft.Data.Sqlite." + Conn + " c = new(cs);")]
    [InlineData("private readonly " + Conn + " _c = new();")]
    [InlineData("private static " + Conn + " Open(string cs) => new(cs);")]
    public void ConstructionRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(NewConnection, snippet);

    [Theory]
    [InlineData("using var c = " + Conn + "Factory.Create(path, password);")]
    [InlineData(Conn + " c = " + Conn + "Factory.Create(path, password);")]
    [InlineData("var b = new " + Conn + "StringBuilder(cs);")]
    [InlineData(Conn + "StringBuilder b = new(cs);")]
    [InlineData("private " + Conn + "? _connection;")]
    public void ConstructionRule_LeavesTheFactoryRouteAlone(string snippet)
        => Assert.DoesNotMatch(NewConnection, snippet);

    [Theory]
    [InlineData("c.Connection" + "String = cs;")]
    [InlineData("c.Connection" + "String=cs;")]
    [InlineData("c.Connection" + "String\n    = cs;")]
    [InlineData("c . Connection" + "String = cs;")]
    public void ConnectionStringRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(ConnectionStringAssignment, snippet);

    [Theory]
    [InlineData("var s = c.Connection" + "String;")]
    [InlineData("if (c.Connection" + "String == cs) { }")]
    public void ConnectionStringRule_IgnoresReads(string snippet)
        => Assert.DoesNotMatch(ConnectionStringAssignment, snippet);

    [Theory]
    [InlineData("var f = Sqlite" + "Factory.Instance;")]
    [InlineData("var f = Microsoft.Data.Sqlite.Sqlite" + "Factory.Instance;")]
    [InlineData("var f = Sqlite" + "Factory\n    .Instance;")]
    public void ProviderFactoryRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(ProviderFactory, snippet);

    [Theory]
    [InlineData("cmd.CommandText = \"PRAGMA busy_timeout" + " = 5000;\";")]
    [InlineData("cmd.CommandText = \"PRAGMA busy_timeout" + "=5000;\";")]
    [InlineData("cmd.CommandText = \"PRAGMA BUSY_TIMEOUT" + " = 5000;\";")]
    [InlineData("cmd.CommandText = \"PRAGMA busy_timeout" + "(5000);\";")]
    public void BusyTimeoutRule_CatchesEverySpelling(string snippet)
        => Assert.Matches(BusyTimeout, snippet);
}