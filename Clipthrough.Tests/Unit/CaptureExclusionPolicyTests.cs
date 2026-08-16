using System;
using System.Collections.Generic;
using System.Linq;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class CaptureExclusionPolicyTests
{
    private static ClipboardSourceApplicationInfo Source(string? name, string? path) => new(name, path, null);

    [Fact]
    public void IsExcluded_WithNoPatterns_CapturesEverything()
    {
        var source = Source("1Password", @"C:\Program Files\1Password\1Password.exe");

        Assert.False(CaptureExclusionPolicy.IsExcluded(source, null));
        Assert.False(CaptureExclusionPolicy.IsExcluded(source, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("1Password")]          // friendly name
    [InlineData("1password")]          // case-insensitive
    [InlineData("1Password.exe")]      // executable file name
    [InlineData(@"C:\Program Files\1Password\1Password.exe")] // full path
    [InlineData("  1Password  ")]      // user typed stray whitespace
    public void IsExcluded_MatchesEveryFormAUserWouldPlausiblyType(string pattern)
    {
        var source = Source("1Password", @"C:\Program Files\1Password\1Password.exe");

        Assert.True(CaptureExclusionPolicy.IsExcluded(source, new[] { pattern }));
    }

    [Theory]
    [InlineData("*keepass*")]
    [InlineData("KeePass?.exe")]
    [InlineData(@"C:\Tools\*")]
    public void IsExcluded_SupportsWildcardPatterns(string pattern)
    {
        var source = Source("KeePass", @"C:\Tools\KeePass2.exe");

        Assert.True(CaptureExclusionPolicy.IsExcluded(source, new[] { pattern }));
    }

    /// <summary>
    /// A pattern must not match a longer name that merely contains it, or
    /// excluding "code" would silently kill capture from "vscode" too. Only an
    /// explicit wildcard opts into substring behaviour.
    /// </summary>
    [Fact]
    public void IsExcluded_WithoutWildcards_DoesNotMatchSubstrings()
    {
        var source = Source("Visual Studio Code", @"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\Code.exe");

        Assert.False(CaptureExclusionPolicy.IsExcluded(source, new[] { "od" }));
        Assert.False(CaptureExclusionPolicy.IsExcluded(source, new[] { "Visual" }));
        Assert.True(CaptureExclusionPolicy.IsExcluded(source, new[] { "Code" }));
    }

    [Fact]
    public void IsExcluded_ScansEveryPatternNotJustTheFirst()
    {
        var source = Source("Bitwarden", @"C:\Apps\Bitwarden.exe");

        Assert.True(CaptureExclusionPolicy.IsExcluded(source, new[] { "notepad", "chrome", "Bitwarden" }));
    }

    [Fact]
    public void IsExcluded_IgnoresBlankPatternEntries()
    {
        var source = Source("Notepad", @"C:\Windows\notepad.exe");

        Assert.False(CaptureExclusionPolicy.IsExcluded(source, new[] { "", "   ", "\t" }));
    }

    /// <summary>
    /// The list is edited in a multi-line text box, so blank lines are routine.
    /// One must not stop the scan and quietly disable every entry below it.
    /// </summary>
    [Fact]
    public void IsExcluded_KeepsScanningPastABlankEntry()
    {
        var source = Source("Bitwarden", @"C:\Apps\Bitwarden.exe");

        Assert.True(CaptureExclusionPolicy.IsExcluded(source, new[] { "notepad", "   ", "Bitwarden" }));
    }

    /// <summary>
    /// A wildcard must match the whole candidate, not merely occur inside it.
    /// Unanchored, "KeePass?.exe" would also exclude "MyKeePass2.exe.bak".
    /// </summary>
    [Fact]
    public void IsExcluded_AnchorsWildcardsToTheWholeCandidate()
    {
        var source = Source("MyKeePass2 Helper", @"C:\Other\MyKeePass2.exe.bak");

        Assert.False(CaptureExclusionPolicy.IsExcluded(source, new[] { "KeePass?.exe" }));
    }

    /// <summary>
    /// Deliberate fail-open. Dropping every clip whose owner Windows would not
    /// report - hidden helper windows, already-exited processes, protected
    /// processes - would destroy far more history than it protects.
    /// </summary>
    [Fact]
    public void IsExcluded_WithUnresolvableSource_CapturesRatherThanDiscards()
    {
        Assert.False(CaptureExclusionPolicy.IsExcluded(null, new[] { "*" }));
    }

    [Fact]
    public void IsExcluded_MatchesOnNameWhenPathIsUnknown()
    {
        Assert.True(CaptureExclusionPolicy.IsExcluded(Source("Vault", null), new[] { "Vault" }));
    }

    [Fact]
    public void IsExcluded_MatchesOnPathWhenNameIsUnknown()
    {
        Assert.True(CaptureExclusionPolicy.IsExcluded(Source(null, @"C:\Apps\Vault.exe"), new[] { "Vault" }));
    }

    /// <summary>
    /// The monitor has to read the *saved* exclusion list on every clipboard
    /// change. Wiring it to the wrong source, or forgetting it entirely, is
    /// invisible from the policy's own tests.
    /// </summary>
    [Fact]
    public void ClipboardMonitor_ResolveCaptureSource_ReadsTheSavedExclusionList()
    {
        var resolver = new StubSourceApplicationResolver(Source("1Password", @"C:\Apps\1Password.exe"));
        var settings = new TestSettingsService();
        using var monitor = new ClipboardMonitorService(null!, resolver, null!, settings);

        monitor.ResolveCaptureSource(out var excludedByDefault);
        Assert.False(excludedByDefault);

        settings.SetCurrent(AppSettings.Default with { ExcludedCaptureApps = new[] { "1Password" } });
        var source = monitor.ResolveCaptureSource(out var excludedAfterSave);

        Assert.True(excludedAfterSave);
        Assert.Equal("1Password", source?.Name);
    }

    /// <summary>
    /// The resolve must not fetch the icon: it happens on every clipboard
    /// change, before we know whether the clip is even wanted.
    /// </summary>
    [Fact]
    public void ClipboardMonitor_ResolveCaptureSource_DoesNotPayForTheIcon()
    {
        var resolver = new StubSourceApplicationResolver(Source("Notepad", @"C:\Windows\notepad.exe"));
        using var monitor = new ClipboardMonitorService(null!, resolver, null!, new TestSettingsService());

        monitor.ResolveCaptureSource(out _);

        Assert.Equal(new[] { false }, resolver.IncludeIconCalls);
    }

    private sealed class StubSourceApplicationResolver(ClipboardSourceApplicationInfo? result) : ISourceApplicationResolver
    {
        public List<bool> IncludeIconCalls { get; } = new();

        public ClipboardSourceApplicationInfo? TryResolve(bool includeIcon = true)
        {
            IncludeIconCalls.Add(includeIcon);
            return result;
        }

        public byte[]? TryResolveIcon(string? processPath) => null;
    }

    [Fact]
    public void ParsePatterns_SplitsTrimsAndDropsBlanksAndDuplicates()
    {
        var parsed = CaptureExclusionPolicy.ParsePatterns("1Password\r\n\r\n  KeePass.exe  \n1PASSWORD\n");

        Assert.Equal(new[] { "1Password", "KeePass.exe" }, parsed);
    }

    [Fact]
    public void ParsePatterns_WithNoContent_ReturnsEmpty()
    {
        Assert.Empty(CaptureExclusionPolicy.ParsePatterns(null));
        Assert.Empty(CaptureExclusionPolicy.ParsePatterns("   \r\n  "));
    }

    [Fact]
    public void FormatPatterns_RoundTripsThroughParse()
    {
        var original = new[] { "1Password", "*keepass*", @"C:\Tools\Vault.exe" };

        Assert.Equal(original, CaptureExclusionPolicy.ParsePatterns(CaptureExclusionPolicy.FormatPatterns(original)));
    }

    [Fact]
    public void Normalize_TrimsDropsBlanksAndDeduplicatesExcludedApps()
    {
        var settings = AppSettings.Default with
        {
            ExcludedCaptureApps = new[] { "  1Password ", "", "1PASSWORD", "KeePass.exe" },
        };

        Assert.Equal(new[] { "1Password", "KeePass.exe" }, settings.Normalize().ExcludedCaptureApps);
    }

    /// <summary>
    /// The exclusion list is not view state, so changing it has to be visible
    /// to <see cref="AppSettings.OnlyViewStateChanged"/> - otherwise the gate
    /// would skip re-applying system state on a save that really did change
    /// what gets captured.
    /// </summary>
    [Fact]
    public void OnlyViewStateChanged_IsFalseWhenTheExclusionListChanges()
    {
        var before = AppSettings.Default.Normalize();
        var after = (AppSettings.Default with { ExcludedCaptureApps = new[] { "1Password" } }).Normalize();

        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
        Assert.True(AppSettings.OnlyViewStateChanged(before, AppSettings.Default.Normalize()));
    }

    /// <summary>
    /// Normalize rebuilds the list into a fresh instance on every save, so the
    /// reference-equality trap that WithoutViewState exists to dodge applies
    /// here too: two saves of the same list must still compare equal.
    /// </summary>
    [Fact]
    public void OnlyViewStateChanged_IsTrueAcrossTwoSavesOfTheSameExclusionList()
    {
        var source = AppSettings.Default with { ExcludedCaptureApps = new[] { "1Password", "KeePass.exe" } };
        var first = source.Normalize();
        var second = source.Normalize();

        Assert.NotSame(first.ExcludedCaptureApps, second.ExcludedCaptureApps);
        Assert.True(AppSettings.OnlyViewStateChanged(first, second));
    }
}
