using System.Collections.Generic;
using System.Linq;
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
}
