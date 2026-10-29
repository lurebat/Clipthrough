using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Regex search on both engines (F-11, OD-7, S38). Before the fix a pattern
/// with nested quantifiers over a long near-miss never finished: the audit's
/// store search over one 41-character clip was still running after 30 s.
/// </summary>
public sealed class RegexSearchEngineTests
{
    // 40 'a' and a character that makes "(a+)+$" fail only after trying every
    // way of splitting the run: about 2^40 steps for a backtracking engine.
    private static readonly string NearMiss = new string('a', 40) + "!";

    private static async Task<TemporaryDatabaseScope> LibraryAsync(params string[] texts)
    {
        var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        foreach (var text in texts)
        {
            // No source app or window title: those columns are searched too,
            // and "Tests" would satisfy (\w)\1 on its own.
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
            });
        }

        return scope;
    }

    private static ClipSearchFilters Regex(string pattern, RegexSearchEngine engine) => new()
    {
        SearchText = pattern,
        UseRegex = true,
        RegexEngine = engine,
    };

    [Fact]
    public void TheDefaultEngineIsNonBacktracking()
    {
        Assert.Equal(RegexSearchEngine.NonBacktracking, new ClipSearchFilters().RegexEngine);
        Assert.Equal(RegexSearchEngine.NonBacktracking, AppSettings.Default.RegexSearchEngine);
        Assert.Equal(RegexSearchEngine.NonBacktracking, new AppSettings().Normalize().RegexSearchEngine);
    }

    [Fact]
    public async Task NestedQuantifiers_OnTheDefaultEngine_FinishQuicklyWithNoMatch()
    {
        using var scope = await LibraryAsync(NearMiss);

        var result = await scope.ClipStoreService
            .SearchAsync(Regex("(a+)+$", RegexSearchEngine.NonBacktracking))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.TotalMatchingCount);
    }

    [Fact]
    public async Task NestedQuantifiers_OnTheBacktrackingEngine_StopAsTooSlow()
    {
        using var scope = await LibraryAsync(NearMiss);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SearchPatternException>(() => scope.ClipStoreService
            .SearchAsync(Regex("(a+)+$", RegexSearchEngine.Backtracking))
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(SearchPatternProblem.TooSlow, ex.Problem);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"Took {started.Elapsed} to give up.");
    }

    [Fact]
    public async Task ABackreference_OnTheDefaultEngine_IsReportedAsNeedingBacktracking()
    {
        using var scope = await LibraryAsync("aa", "ab");

        var ex = await Assert.ThrowsAsync<SearchPatternException>(() => scope.ClipStoreService
            .SearchAsync(Regex(@"(\w)\1", RegexSearchEngine.NonBacktracking)));

        Assert.Equal(SearchPatternProblem.NeedsBacktracking, ex.Problem);
    }

    [Fact]
    public async Task ABackreference_OnTheBacktrackingEngine_Matches()
    {
        using var scope = await LibraryAsync("aa", "ab");

        var result = await scope.ClipStoreService.SearchAsync(Regex(@"(\w)\1", RegexSearchEngine.Backtracking));

        Assert.Equal(["aa"], result.Items.Select(i => i.Content));
    }

    /// <summary>
    /// An ordinary pattern answers the same on both engines. The expected set
    /// is written out by hand rather than taken from either engine, so a
    /// regression in one cannot hide behind the other.
    /// </summary>
    [Theory]
    [InlineData(RegexSearchEngine.NonBacktracking)]
    [InlineData(RegexSearchEngine.Backtracking)]
    public async Task AnOrdinaryPattern_MatchesTheSameClipsOnEitherEngine(RegexSearchEngine engine)
    {
        using var scope = await LibraryAsync(
            "invoice 2024 paid",
            "Invoices 2019",
            "inventory 2024",
            "invoice 1999",
            "INVOICE 2031",
            "in 2024");

        var result = await scope.ClipStoreService.SearchAsync(Regex(@"inv\w+ 20\d\d", engine));

        Assert.Equal(
            new[] { "INVOICE 2031", "Invoices 2019", "inventory 2024", "invoice 2024 paid" },
            result.Items.Select(i => i.Content).OrderBy(s => s, StringComparer.Ordinal));
    }
}

public sealed class RegexSearchEngineSettingTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsPath;

    public RegexSearchEngineSettingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "clipthrough-regex-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _settingsPath = Path.Combine(_tempRoot, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    private SettingsService NewService()
        => new(new SqliteConnectionFactory(new TestStorageOptionsService(Path.Combine(_tempRoot, "test.db"))), new FakeDataProtectionService(), _settingsPath);

    [Fact]
    public async Task Backtracking_SurvivesASaveAndAReload()
    {
        var service = NewService();
        await service.InitializeAsync();
        await service.SaveAsync(service.Current with { RegexSearchEngine = RegexSearchEngine.Backtracking });

        var reloaded = NewService();
        await reloaded.InitializeAsync();

        Assert.Equal(RegexSearchEngine.Backtracking, reloaded.Current.RegexSearchEngine);
    }

    [Fact]
    public void Normalize_MapsAnUnknownEngineToTheDefault()
    {
        var settings = new AppSettings { RegexSearchEngine = (RegexSearchEngine)42 }.Normalize();

        Assert.Equal(RegexSearchEngine.NonBacktracking, settings.RegexSearchEngine);
    }
}
