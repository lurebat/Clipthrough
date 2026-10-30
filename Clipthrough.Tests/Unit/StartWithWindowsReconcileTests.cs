using System;
using System.IO;
using Clipthrough.Models;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// There is one Start with Windows entry per Windows user, and several
/// configurations of Clipthrough can share that user: the usual install, a copy
/// started with --data-dir for an isolated session, another unpacked ZIP.
/// Each applies its own saved setting at startup. Before this, applying it
/// meant overwriting or deleting whatever entry was there, so an isolated
/// session with the setting off - the default - deleted the usual install's
/// entry the moment it started.
///
/// At startup a configuration now touches only an entry that is missing or its
/// own: the same executable and the same --data-dir, compared after Windows
/// splits the stored command, not as raw text. Turning the setting on or off in
/// Settings is an explicit request and still writes or removes the entry.
/// </summary>
public sealed class StartWithWindowsReconcileTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Clipthrough\current\Clipthrough.exe";
    private const string OtherCopy = @"D:\Tools\Clipthrough\Clipthrough.exe";
    private static readonly string s_base = Path.Combine(Path.GetTempPath(), "ct-reconcile-unused");
    private static readonly AppDataRoot s_usual = AppDataRoot.Default(s_base);
    private static readonly AppDataRoot s_isolated = AppDataRoot.Chosen(Path.Combine(s_base, "isolated run"), AppDataRootSource.CommandLine);

    private sealed class FakeStartupEntry(string? value) : IStartupEntryStore
    {
        public string? Value { get; private set; } = value;

        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public string? Read() => Value;

        public void Write(string command)
        {
            Value = command;
            Writes++;
        }

        public void Delete()
        {
            Value = null;
            Deletes++;
        }
    }

    private static SystemInteractionService Session(AppDataRoot root, FakeStartupEntry entry, string exe = Exe)
        => new(root, entry, exe);

    private static string EntryOf(AppDataRoot root, string exe = Exe)
    {
        using var service = new SystemInteractionService(root, null, exe);
        return service.BuildStartupCommand(exe);
    }

    [Fact]
    public void AnIsolatedSessionWithTheSettingOff_LeavesTheUsualEntryAlone()
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual));
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: false);

        Assert.Equal(EntryOf(s_usual), entry.Value);
        Assert.Equal(0, entry.Deletes);
    }

    [Fact]
    public void AnIsolatedSessionWithTheSettingOn_DoesNotTakeOverTheUsualEntryAtStartup()
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual));
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: true);

        Assert.Equal(EntryOf(s_usual), entry.Value);
        Assert.Equal(0, entry.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheUsualSession_LeavesAnIsolatedEntryAlone(bool enabled)
    {
        var entry = new FakeStartupEntry(EntryOf(s_isolated));
        using var session = Session(s_usual, entry);

        session.ReconcileStartWithWindows(enabled);

        Assert.Equal(EntryOf(s_isolated), entry.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherCopyOfTheExecutable_IsAnotherConfiguration(bool enabled)
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual, OtherCopy));
        using var session = Session(s_usual, entry);

        session.ReconcileStartWithWindows(enabled);

        Assert.Equal(EntryOf(s_usual, OtherCopy), entry.Value);
    }

    [Fact]
    public void TheSameConfigurationWithTheSettingOff_RemovesItsEntry()
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual));
        using var session = Session(s_usual, entry);

        session.ReconcileStartWithWindows(enabled: false);

        Assert.Null(entry.Value);
    }

    // Compared as Windows reads the command, not as text: another casing, a
    // trailing separator and the = spelling are still this configuration.
    [Fact]
    public void ItsOwnEntrySpelledDifferently_IsStillItsOwn()
    {
        var spelled = $"\"{Exe.ToUpperInvariant()}\" --DATA-DIR=\"{s_isolated.Directory.ToUpperInvariant()}\\\\\"";
        var entry = new FakeStartupEntry(spelled);
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: false);

        Assert.Null(entry.Value);
    }

    // A different --data-dir is a different configuration even with the same
    // executable, and so is the bare executable to a --data-dir session.
    [Fact]
    public void AnotherDataFolderOnTheSameExecutable_IsAnotherConfiguration()
    {
        var other = AppDataRoot.Chosen(Path.Combine(s_base, "another run"), AppDataRootSource.CommandLine);
        var entry = new FakeStartupEntry(EntryOf(other));
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: false);

        Assert.Equal(EntryOf(other), entry.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TheSettingOnAndNoEntry_WritesItsOwn(string? existing)
    {
        var entry = new FakeStartupEntry(existing);
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: true);

        Assert.Equal(EntryOf(s_isolated), entry.Value);
    }

    [Fact]
    public void TheSettingOnAndItsOwnEntry_LeavesItAsItIs()
    {
        var entry = new FakeStartupEntry(EntryOf(s_isolated));
        using var session = Session(s_isolated, entry);

        session.ReconcileStartWithWindows(enabled: true);

        Assert.Equal(0, entry.Writes);
        Assert.Equal(0, entry.Deletes);
    }

    // The toggle is the user asking, in this configuration, for sign-in to
    // start it - or not at all - so it still replaces or removes any entry.
    [Fact]
    public void TurningItOnInSettings_ReplacesAnotherConfigurationsEntry()
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual));
        using var session = Session(s_isolated, entry);

        session.SyncStartWithWindows(enabled: true);

        Assert.Equal(EntryOf(s_isolated), entry.Value);
    }

    [Fact]
    public void TurningItOffInSettings_RemovesTheEntry()
    {
        var entry = new FakeStartupEntry(EntryOf(s_usual));
        using var session = Session(s_isolated, entry);

        session.SyncStartWithWindows(enabled: false);

        Assert.Null(entry.Value);
    }

    [Fact]
    public void TheFirstSettingsEvent_Reconciles()
    {
        var system = new TestSystemInteractionService();

        App.ApplyStartWithWindows(system, previous: null, AppSettings.Default with { StartWithWindows = true });

        Assert.Equal([("reconcile", true)], system.StartWithWindowsCalls);
    }

    [Fact]
    public void AChangeMadeInSettings_Syncs()
    {
        var system = new TestSystemInteractionService();

        App.ApplyStartWithWindows(system, AppSettings.Default, AppSettings.Default with { StartWithWindows = true });

        Assert.Equal([("sync", true)], system.StartWithWindowsCalls);
    }

    [Fact]
    public void ALaterEventWithoutAChange_LeavesTheEntryAlone()
    {
        var system = new TestSystemInteractionService();

        App.ApplyStartWithWindows(system, AppSettings.Default, AppSettings.Default with { MinimizeToTray = !AppSettings.Default.MinimizeToTray });

        Assert.Empty(system.StartWithWindowsCalls);
    }
}
