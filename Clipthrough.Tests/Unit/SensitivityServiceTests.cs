using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Scan decides whether a clip is treated as a secret, which governs whether it
/// is excluded from embeddings, hidden from search, and expired under the
/// shorter sensitive lifetime. It had no direct tests at all: a mutation check
/// that deleted the IsEnabled guard outright passed all 278 non-headless tests.
/// </summary>
public class SensitivityServiceTests
{
    private static SensitivityRule Rule(string name, string pattern, bool isEnabled = true, string severity = "warning")
        => new() { Name = name, Pattern = pattern, IsEnabled = isEnabled, Severity = severity, IsBuiltIn = false };

    // The parameterless ctor has no connection factory, so SaveRulesAsync
    // compiles the rules in place without touching a database.
    private static async Task<SensitivityService> WithRulesAsync(params SensitivityRule[] rules)
    {
        var service = new SensitivityService();
        await service.SaveRulesAsync(rules);
        return service;
    }

    [Fact]
    public async Task Scan_EnabledRule_ReportsNameAndSeverity()
    {
        var service = await WithRulesAsync(Rule("Tokens", "secret-[0-9]+", severity: "critical"));

        var match = Assert.Single(service.Scan("here is secret-4242 for you"));

        Assert.Equal("Tokens", match.RuleName);
        Assert.Equal("critical", match.Severity);
    }

    /// <summary>
    /// The match has to carry the rule's regex, not just its name. When a scan runs
    /// against rules that were never persisted - the fallback path taken when the
    /// rules table cannot be read - the store provisions the missing rule from the
    /// match alone, and the pattern is the only part it cannot reconstruct.
    /// </summary>
    [Fact]
    public async Task Scan_CarriesThePatternSoAMissingRuleCanBeProvisioned()
    {
        var service = await WithRulesAsync(Rule("Tokens", "secret-[0-9]+"));

        var match = Assert.Single(service.Scan("here is secret-4242 for you"));

        // Behavioural, not a string comparison: whatever is reported has to still
        // work as the detector it claims to be. A rule name is itself a valid
        // regex, so only running it reveals a pattern that detects nothing.
        Assert.Matches(new Regex(match.Pattern), "here is secret-4242 for you");
        Assert.Equal("secret-[0-9]+", match.Pattern);
    }

    [Fact]
    public async Task Scan_DisabledRule_DoesNotMatch()
    {
        var service = await WithRulesAsync(Rule("Tokens", "secret-[0-9]+", isEnabled: false));

        Assert.Empty(service.Scan("here is secret-4242 for you"));
    }

    [Fact]
    public async Task Scan_DisablingOneRule_LeavesTheOthersMatching()
    {
        var service = await WithRulesAsync(
            Rule("Disabled", "alpha", isEnabled: false),
            Rule("Enabled", "beta"));

        var match = Assert.Single(service.Scan("alpha and beta"));

        Assert.Equal("Enabled", match.RuleName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Scan_BlankContent_ReturnsNoMatches(string? content)
    {
        var service = await WithRulesAsync(Rule("MatchesAnything", "(?s).*"));

        Assert.Empty(service.Scan(content));
    }

    [Fact]
    public async Task Scan_ReportsEveryMatchingRule()
    {
        var service = await WithRulesAsync(
            Rule("First", "alpha"),
            Rule("Second", "beta"),
            Rule("Third", "gamma"));

        var names = service.Scan("alpha beta").Select(match => match.RuleName).ToList();

        Assert.Equal(new[] { "First", "Second" }, names);
    }

    [Fact]
    public void DefaultRules_DetectTheSecretsTheyAdvertise()
    {
        var service = new SensitivityService();

        Assert.Contains(service.Scan("AKIAIOSFODNN7EXAMPLE"), match => match.RuleName == "AWS Keys");
        Assert.Contains(service.Scan("-----BEGIN RSA PRIVATE KEY-----"), match => match.RuleName == "Private Keys");
        Assert.Contains(service.Scan("password = hunter2000"), match => match.RuleName == "Passwords");
        Assert.Empty(service.Scan("an entirely ordinary sentence"));
    }

    /// <summary>
    /// Patterns are user-authored and were persisted without ever being
    /// compiled. CompileRules ran after CommitAsync, so an invalid pattern was
    /// written to the database and only then threw: the bad rule survived the
    /// failure permanently, and every later GetRulesAsync threw on it too -
    /// leaving the user's whole rule set unloadable.
    /// </summary>
    [Fact]
    public async Task SaveRules_InvalidPattern_IsRejectedBeforeItReachesTheDatabase()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var service = new SensitivityService(scope.ConnectionFactory);
        await service.SaveRulesAsync([Rule("Good", "alpha")]);

        // ThrowsAny, because Regex reports a RegexParseException - a subclass of
        // ArgumentException, which the exact-match Throws would reject.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.SaveRulesAsync([Rule("Broken", "([unclosed")]));

        var reloaded = new SensitivityService(scope.ConnectionFactory);
        var stored = await reloaded.GetRulesAsync();

        Assert.Equal(new[] { "Good" }, stored.Select(rule => rule.Name));
        Assert.Single(reloaded.Scan("alpha"));
    }

    /// <summary>
    /// A rule set already poisoned by an older build must still load. Skipping
    /// the one bad pattern keeps the remaining rules working; throwing would
    /// leave sensitivity scanning permanently unable to load anything.
    /// </summary>
    [Fact]
    public async Task GetRules_PatternStoredByAnOlderBuild_DoesNotBlockTheOtherRules()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM sensitivity_rules;
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ('Broken', '([unclosed', 'warning', 1, 0),
                       ('Good', 'alpha', 'warning', 1, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var service = new SensitivityService(scope.ConnectionFactory);
        await service.GetRulesAsync();

        Assert.Single(service.Scan("alpha"));
    }

    /// <summary>
    /// Scan runs on the capture path for every clip, against regexes the user
    /// can type. Measured on .NET 10: "(a+)+$" against 32 characters had still
    /// not returned after three minutes, so without a match timeout one bad rule
    /// hangs clipboard capture permanently.
    ///
    /// The scan runs on a pool thread and is only read once it has completed, so
    /// an unbounded regex fails this test quickly instead of wedging the run.
    /// </summary>
    [Fact]
    public async Task Scan_CatastrophicPattern_GivesUpInsteadOfHanging()
    {
        var service = await WithRulesAsync(Rule("Runaway", "(a+)+$"));
        var input = new string('a', 32) + "!";

        var scan = Task.Run(() => service.Scan(input));
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, scan), "Scan never abandoned the catastrophic pattern.");
        Assert.Empty(await scan);
    }

    [Fact]
    public async Task Scan_CatastrophicPattern_DoesNotStopLaterRulesMatching()
    {
        var service = await WithRulesAsync(
            Rule("Runaway", "(a+)+$"),
            Rule("Normal", "needle"));
        var input = new string('a', 32) + "! needle";

        var scan = Task.Run(() => service.Scan(input));
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, scan), "Scan never abandoned the catastrophic pattern.");
        Assert.Equal(new[] { "Normal" }, (await scan).Select(match => match.RuleName));
    }
}
