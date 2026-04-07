using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class SensitivityService : ISensitivityService
{
    private static readonly IReadOnlyList<(SensitivityRule Rule, Regex Regex)> BuiltInRules =
    [
        CreateRule("API Keys", @"(?i)(api[_-]?key|apikey)['\"":\s=]+[a-zA-Z0-9_\-]{16,}", "warning"),
        CreateRule("AWS Keys", @"AKIA[0-9A-Z]{16}", "critical"),
        CreateRule("JWT Tokens", @"eyJ[a-zA-Z0-9_-]+\.eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+", "warning"),
        CreateRule("Private Keys", @"-----BEGIN (RSA|EC|DSA|OPENSSH) PRIVATE KEY-----", "critical"),
        CreateRule("Passwords", @"(?i)(password|passwd|pwd)['\"":\s=]+\S{6,}", "warning"),
        CreateRule("Connection Strings", @"(?i)(server|data source)=.+;(user id|uid|password|pwd)=", "critical"),
        CreateRule("Credit Cards", @"\b(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14})\b", "critical"),
        CreateRule("Generic Secrets", @"(?i)(secret|token|auth)['\"":\s=]+[a-zA-Z0-9_\-/+=]{16,}", "warning"),
    ];

    public IReadOnlyList<SensitivityRule> GetDefaultRules() => BuiltInRules.Select(static x => x.Rule).ToArray();

    public IReadOnlyList<SensitivityMatch> Scan(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var matches = new List<SensitivityMatch>();

        foreach (var (rule, regex) in BuiltInRules)
        {
            if (!rule.IsEnabled || !regex.IsMatch(content))
            {
                continue;
            }

            matches.Add(new SensitivityMatch
            {
                RuleName = rule.Name,
                Severity = rule.Severity,
            });
        }

        return matches;
    }

    private static (SensitivityRule Rule, Regex Regex) CreateRule(string name, string pattern, string severity)
    {
        var rule = new SensitivityRule
        {
            Name = name,
            Pattern = pattern,
            Severity = severity,
            IsEnabled = true,
            IsBuiltIn = true,
        };

        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return (rule, regex);
    }
}

