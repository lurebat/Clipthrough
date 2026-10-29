using System;
using System.IO;
using System.Linq;
using Clipthrough.Localization;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-42. Five labels told the user a remembered database password is "saved as
/// plaintext in storage.json". It never is: <c>StorageOptionsService.BuildDocument</c>
/// writes only a DPAPI-protected copy, and where DPAPI is unavailable nothing at
/// all. A false warning is not a harmless overstatement - it talks users out of a
/// feature that is safe and into believing one that is not would be flagged.
/// </summary>
public sealed class PasswordStorageLabelTests
{
    private static readonly string[] PasswordViews =
    [
        Path.Combine("Clipthrough", "Views", "MainWindow.axaml"),
        Path.Combine("Clipthrough", "Views", "SettingsWindow.axaml"),
        Path.Combine("Clipthrough", "Views", "RekeyDialog.axaml"),
    ];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Clipthrough", "Views", "RekeyDialog.axaml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> PasswordStrings() => new()
    {
        AppText.SettingsDatabasePasswordWarning,
        AppText.SettingsConfirmEncryptionPasswordBody,
        AppText.SettingsConfirmEncryptionPasswordConfirm,
        AppText.SettingsRememberDatabasePassword,
        AppText.RekeyRememberNewPassword,
        AppText.RekeyRememberNewPasswordWarning,
    };

    [Theory]
    [MemberData(nameof(PasswordStrings))]
    public void NoPasswordLabel_ClaimsThePasswordIsStoredInPlaintext(string label)
    {
        Assert.DoesNotContain("plaintext", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWarnings_SayHowThePasswordIsActuallyKept()
    {
        foreach (var warning in new[] { AppText.SettingsDatabasePasswordWarning, AppText.SettingsConfirmEncryptionPasswordBody, AppText.RekeyRememberNewPasswordWarning })
        {
            Assert.Contains("encrypted", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("memory", warning, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The three AXAML labels were literals, which is how they escaped the AppText
    /// rule and how a reworded AppText entry could leave them stale. Ordinal, so the
    /// <c>PlainText</c> binding identifiers in the same files do not count.
    /// </summary>
    [Fact]
    public void ThePasswordViews_HoldNoPlaintextClaimOfTheirOwn()
    {
        var root = RepositoryRoot();
        var offenders = PasswordViews
            .Where(view => File.ReadAllText(Path.Combine(root, view)).Contains("plaintext", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0, "These views still claim plaintext storage: " + string.Join(", ", offenders));
    }
}
