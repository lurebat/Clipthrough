using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class SensitivityService : ISensitivityService
{
    private static readonly IReadOnlyList<SensitivityRule> s_defaultRules =
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

    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<CompiledRule> _compiledRules;

    public SensitivityService()
    {
        _compiledRules = CompileRules(s_defaultRules);
    }

    public SensitivityService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _compiledRules = CompileRules(s_defaultRules);
    }

    public IReadOnlyList<SensitivityRule> GetDefaultRules() => s_defaultRules.Select(CloneRule).ToArray();

    public async Task<IReadOnlyList<SensitivityRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        if (_connectionFactory is null)
        {
            return GetDefaultRules();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = await LoadRulesCoreAsync(cancellationToken);
            if (rules.Count == 0)
            {
                rules = GetDefaultRules();
            }

            _compiledRules = CompileStoredRules(rules);
            return rules.Select(CloneRule).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveRulesAsync(IReadOnlyList<SensitivityRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (_connectionFactory is null)
        {
            _compiledRules = CompileRules(rules);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var normalizedRules = rules
                .Where(static rule => !string.IsNullOrWhiteSpace(rule.Name) && !string.IsNullOrWhiteSpace(rule.Pattern))
                .Select(NormalizeRule)
                .ToArray();

            // Compile before writing anything. This used to run after
            // CommitAsync, so a pattern that failed to compile was already
            // stored: the save threw, the bad rule stayed in the database, and
            // every later load threw on it as well.
            var compiledRules = CompileRules(normalizedRules);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            var retainedIds = new HashSet<long>();
            foreach (var rule in normalizedRules)
            {
                if (rule.Id > 0)
                {
                    await using var updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText = """
                        UPDATE sensitivity_rules
                        SET name = $name,
                            pattern = $pattern,
                            severity = $severity,
                            is_enabled = $isEnabled,
                            is_builtin = $isBuiltIn
                        WHERE id = $id;
                        """;
                    updateCommand.Parameters.AddWithValue("$id", rule.Id);
                    updateCommand.Parameters.AddWithValue("$name", rule.Name);
                    updateCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
                    updateCommand.Parameters.AddWithValue("$severity", rule.Severity);
                    updateCommand.Parameters.AddWithValue("$isEnabled", rule.IsEnabled ? 1 : 0);
                    updateCommand.Parameters.AddWithValue("$isBuiltIn", rule.IsBuiltIn ? 1 : 0);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                    retainedIds.Add(rule.Id);
                    continue;
                }

                await using var existingIdCommand = connection.CreateCommand();
                existingIdCommand.Transaction = transaction;
                existingIdCommand.CommandText = """
                    SELECT id
                    FROM sensitivity_rules
                    WHERE name = $name
                    LIMIT 1;
                    """;
                existingIdCommand.Parameters.AddWithValue("$name", rule.Name);
                var existingIdValue = await existingIdCommand.ExecuteScalarAsync(cancellationToken);
                if (existingIdValue is long existingId)
                {
                    await using var updateExistingCommand = connection.CreateCommand();
                    updateExistingCommand.Transaction = transaction;
                    updateExistingCommand.CommandText = """
                        UPDATE sensitivity_rules
                        SET pattern = $pattern,
                            severity = $severity,
                            is_enabled = $isEnabled,
                            is_builtin = $isBuiltIn
                        WHERE id = $id;
                        """;
                    updateExistingCommand.Parameters.AddWithValue("$id", existingId);
                    updateExistingCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
                    updateExistingCommand.Parameters.AddWithValue("$severity", rule.Severity);
                    updateExistingCommand.Parameters.AddWithValue("$isEnabled", rule.IsEnabled ? 1 : 0);
                    updateExistingCommand.Parameters.AddWithValue("$isBuiltIn", rule.IsBuiltIn ? 1 : 0);
                    await updateExistingCommand.ExecuteNonQueryAsync(cancellationToken);
                    retainedIds.Add(existingId);
                    continue;
                }

                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                    VALUES ($name, $pattern, $severity, $isEnabled, $isBuiltIn);
                    SELECT last_insert_rowid();
                    """;
                insertCommand.Parameters.AddWithValue("$name", rule.Name);
                insertCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
                insertCommand.Parameters.AddWithValue("$severity", rule.Severity);
                insertCommand.Parameters.AddWithValue("$isEnabled", rule.IsEnabled ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$isBuiltIn", rule.IsBuiltIn ? 1 : 0);
                var insertedId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
                retainedIds.Add(insertedId);
            }

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = retainedIds.Count == 0
                    ? "DELETE FROM sensitivity_rules;"
                    : $"DELETE FROM sensitivity_rules WHERE id NOT IN ({string.Join(", ", retainedIds)});";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _compiledRules = compiledRules;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetRulesAsync(cancellationToken);
    }

    public IReadOnlyList<SensitivityMatch> Scan(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var matches = new List<SensitivityMatch>();

        foreach (var rule in _compiledRules)
        {
            if (!rule.Rule.IsEnabled || !IsMatch(rule, content))
            {
                continue;
            }

            matches.Add(new SensitivityMatch
            {
                RuleId = rule.Rule.Id,
                RuleName = rule.Rule.Name,
                Pattern = rule.Rule.Pattern,
                Severity = rule.Rule.Severity,
            });
        }

        return matches;
    }

    private static bool IsMatch(CompiledRule rule, string content)
    {
        try
        {
            return rule.Regex.IsMatch(content);
        }
        catch (RegexMatchTimeoutException)
        {
            Trace.TraceWarning($"Sensitivity rule '{rule.Rule.Name}' timed out and was skipped for this clip.");
            return false;
        }
    }

    private async Task<IReadOnlyList<SensitivityRule>> LoadRulesCoreAsync(CancellationToken cancellationToken)
    {
        if (_connectionFactory is null)
        {
            return GetDefaultRules();
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, pattern, severity, is_enabled, is_builtin
                FROM sensitivity_rules
                ORDER BY is_builtin DESC, name ASC;
                """;

            var rules = new List<SensitivityRule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rules.Add(new SensitivityRule
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Pattern = reader.GetString(2),
                    Severity = reader.GetString(3),
                    IsEnabled = reader.GetInt64(4) == 1,
                    IsBuiltIn = reader.GetInt64(5) == 1,
                });
            }

            return rules;
        }
        catch (SqliteException)
        {
            return GetDefaultRules();
        }
    }

    private static SensitivityRule CreateRule(string name, string pattern, string severity) => new()
    {
        Name = name,
        Pattern = pattern,
        Severity = severity,
        IsEnabled = true,
        IsBuiltIn = true,
    };

    private static SensitivityRule NormalizeRule(SensitivityRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name.Trim(),
        Pattern = rule.Pattern.Trim(),
        Severity = string.IsNullOrWhiteSpace(rule.Severity) ? "warning" : rule.Severity.Trim().ToLowerInvariant(),
        IsEnabled = rule.IsEnabled,
        IsBuiltIn = rule.IsBuiltIn,
    };

    private static SensitivityRule CloneRule(SensitivityRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Pattern = rule.Pattern,
        Severity = rule.Severity,
        IsEnabled = rule.IsEnabled,
        IsBuiltIn = rule.IsBuiltIn,
    };

    // Scan runs on the capture path for every clip, against patterns the user
    // can type. Regex backtracking is exponential in the worst case - "(a+)+$"
    // against 32 characters had not returned after three minutes on .NET 10 -
    // so an unbounded match here hangs clipboard capture outright.
    //
    // A rule that times out is treated as not matching. Treating it as a match
    // would be the privacy-cautious reading, but a pattern that times out does
    // so on almost any input, which would flag the entire library sensitive -
    // and sensitive clips expire under a shorter lifetime that ignores pinning,
    // so failing that way loses data. Failing open costs detection by one
    // broken rule; failing closed silently deletes clips.
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    private static Regex CompilePattern(string pattern)
        => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexMatchTimeout);

    private static IReadOnlyList<CompiledRule> CompileRules(IEnumerable<SensitivityRule> rules)
        => rules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => new CompiledRule(CloneRule(rule), CompilePattern(rule.Pattern)))
            .ToArray();

    /// <summary>
    /// Compiles what it can and drops what it cannot. Only for rules read back
    /// from the database: an older build persisted patterns without ever
    /// compiling them, so a stored rule set can contain one that does not
    /// compile. Throwing on it would make the user's entire rule set
    /// permanently unloadable rather than costing them the one bad rule.
    /// </summary>
    private static IReadOnlyList<CompiledRule> CompileStoredRules(IEnumerable<SensitivityRule> rules)
    {
        var compiled = new List<CompiledRule>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                continue;
            }

            try
            {
                compiled.Add(new CompiledRule(CloneRule(rule), CompilePattern(rule.Pattern)));
            }
            catch (ArgumentException ex)
            {
                Trace.TraceWarning($"Sensitivity rule '{rule.Name}' has an invalid pattern and was skipped: {ex.Message}");
            }
        }

        return compiled;
    }

    private sealed record CompiledRule(SensitivityRule Rule, Regex Regex);
}
