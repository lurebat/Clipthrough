using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// A scan can report a match against a rule that is not in <c>sensitivity_rules</c>
/// yet. <see cref="SensitivityService.LoadRulesCoreAsync"/> falls back to the
/// in-memory defaults whenever the rules table cannot be read - an encrypted
/// database before the password arrives, a locked file, a corrupt page - and those
/// defaults carry <c>Id = 0</c> because nothing has assigned them one. The store
/// then has to provision the rule so the match row has something to point at.
///
/// What it provisions has to be the rule's actual regex. Writing the display name
/// into the pattern column produces a rule that still looks right in Settings -
/// same name, same severity, enabled - while matching only its own name as literal
/// text. "Credit Card" stops detecting card numbers and starts detecting the words
/// "Credit Card", and because the row exists, the <c>ON CONFLICT(name)</c> upsert
/// that would otherwise install the real pattern never fires again. The damage is
/// silent, permanent, and in the direction of leaking secrets.
/// </summary>
public class SensitivityRuleProvisioningTests
{
    private const string RuleName = "Credit Card";
    private const string RulePattern = @"\b(?:\d[ -]*?){13,16}\b";
    private const string CardNumber = "4111 1111 1111 1111";

    [Fact]
    public async Task ProvisionedRule_StoresTheRegexRatherThanTheRuleName()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        // The rules table starts seeded with the built-ins, and provisioning only
        // happens for a name it does not already hold - so clear it to reach the
        // path a fallback scan takes on a database whose rules could not be read.
        Execute(scope, "DELETE FROM sensitivity_rules;");

        var store = new ClipStoreService(
            scope.ConnectionFactory,
            new UnpersistedRuleSensitivityService(),
            scope.SettingsService,
            scope.NotificationService);

        var clip = await store.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = CardNumber,
            ContentBytes = Encoding.UTF8.GetBytes(CardNumber),
            SkipPostInsertMaintenance = true,
        });
        Assert.NotNull(clip);

        var stored = ScalarText(scope, $"SELECT pattern FROM sensitivity_rules WHERE name = '{RuleName}';");
        Assert.NotNull(stored);

        // The assertion is behavioural rather than a string comparison: whatever was
        // written has to work as the detector it claims to be. A name written into
        // the pattern column is a perfectly valid regex, so only running it reveals
        // that it no longer detects anything it is named for.
        Assert.Matches(new Regex(stored!), CardNumber);
        Assert.Equal(RulePattern, stored);
    }

    /// <summary>
    /// Provisioning must not overwrite a rule the user has already edited. The
    /// upsert exists to create a missing row, not to reinstate a default over a
    /// customised pattern.
    /// </summary>
    [Fact]
    public async Task ProvisionedRule_LeavesAnExistingPatternAlone()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        Execute(scope, "DELETE FROM sensitivity_rules;");
        Execute(scope, $"INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin) VALUES ('{RuleName}', 'user-edited-pattern', 'warning', 1, 1);");

        var store = new ClipStoreService(
            scope.ConnectionFactory,
            new UnpersistedRuleSensitivityService(),
            scope.SettingsService,
            scope.NotificationService);

        await store.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = CardNumber,
            ContentBytes = Encoding.UTF8.GetBytes(CardNumber),
            SkipPostInsertMaintenance = true,
        });

        Assert.Equal(
            "user-edited-pattern",
            ScalarText(scope, $"SELECT pattern FROM sensitivity_rules WHERE name = '{RuleName}';"));
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? ScalarText(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Reports exactly what the real service reports when it has fallen back to the
    /// in-memory defaults: the rule's name, severity and pattern, with no id,
    /// because no row has been assigned one.
    /// </summary>
    private sealed class UnpersistedRuleSensitivityService : ISensitivityService
    {
        private static readonly Regex s_regex = new(RulePattern);

        public IReadOnlyList<SensitivityMatch> Scan(string? content)
            => content is not null && s_regex.IsMatch(content)
                ? [new SensitivityMatch { RuleId = 0, RuleName = RuleName, Pattern = RulePattern, Severity = "warning" }]
                : [];

        public IReadOnlyList<SensitivityRule> GetDefaultRules() => [];

        public Task<IReadOnlyList<SensitivityRule>> GetRulesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SensitivityRule>>([]);

        public Task SaveRulesAsync(IReadOnlyList<SensitivityRule> rules, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
