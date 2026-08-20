using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// settings.json is written through a temp file and an atomic rename, and an
/// unparseable one is moved aside and reported rather than silently replaced.
/// </summary>
/// <remarks>
/// These two belong together. A direct write over the live file can truncate it,
/// and the recovery path for an unreadable settings file is not "defaults, and
/// say so" - it falls back to a legacy copy in the database and then to
/// defaults, with nothing but a trace line to show for it. Either defect alone
/// is survivable; together they lose a user's configuration without telling
/// them. (round 2, arch-opus A23 and A24)
/// </remarks>
public sealed class SettingsFileIntegrityTests
{
    private sealed class Workspace : IDisposable
    {
        public TemporaryDatabaseScope Scope { get; } = new();
        public string Directory { get; }
        public string SettingsPath { get; }

        public Workspace()
        {
            Directory = Path.Combine(Path.GetTempPath(), "clipthrough-settings-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            SettingsPath = Path.Combine(Directory, "settings.json");
        }

        public SettingsService NewService() => new(Scope.ConnectionFactory, new NoOpDataProtectionService(), SettingsPath);

        public void Dispose()
        {
            Scope.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class NoOpDataProtectionService : IDataProtectionService
    {
        public bool CanPersistSecrets => true;
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    [Fact]
    public async Task SavingSettings_LeavesNoTemporaryFileBehind()
    {
        using var workspace = new Workspace();
        var service = workspace.NewService();

        await service.SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 4321 });

        Assert.True(File.Exists(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.Directory, "*.tmp"));
    }

    /// <summary>
    /// The rename must replace an existing file, not fail against it. A save is
    /// almost always over settings that are already there, so getting this wrong
    /// would break every save after the first.
    /// </summary>
    [Fact]
    public async Task SavingTwice_ReplacesTheExistingFile()
    {
        using var workspace = new Workspace();

        await workspace.NewService().SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 1111 });
        await workspace.NewService().SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 2222 });

        var reloaded = workspace.NewService();
        await reloaded.InitializeAsync();

        Assert.Equal(2222, reloaded.Current.MaxClipSizeBytes);
        Assert.Empty(Directory.GetFiles(workspace.Directory, "*.tmp"));
    }

    /// <summary>
    /// The property that matters: a write that fails part-way must not damage
    /// the settings already on disk. Staging into a temp file and renaming is
    /// what provides it.
    /// </summary>
    /// <remarks>
    /// Occupying the temp path with a directory is the only deterministic way to
    /// fail the staging write from outside. It is also the only assertion here
    /// that distinguishes staging from a direct write - "no .tmp left behind"
    /// and "the second save wins" are both trivially true of a direct write,
    /// which a mutant proved by surviving them.
    /// </remarks>
    [Fact]
    public async Task WhenTheStagingWriteFails_TheSettingsAlreadyOnDiskSurvive()
    {
        using var workspace = new Workspace();
        await workspace.NewService().SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 1111 });
        var before = await File.ReadAllTextAsync(workspace.SettingsPath);

        Directory.CreateDirectory(workspace.SettingsPath + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(
            () => workspace.NewService().SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 2222 }));

        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SettingsPath));
    }

    [Fact]
    public async Task ACorruptSettingsFile_IsMovedAsideAndReported()
    {
        using var workspace = new Workspace();
        await File.WriteAllTextAsync(workspace.SettingsPath, "{ this is not json");

        var service = workspace.NewService();
        await service.InitializeAsync();

        Assert.NotNull(service.LoadFault);
        Assert.Contains("could not be read", service.LoadFault!, StringComparison.Ordinal);

        var quarantined = Directory.GetFiles(workspace.Directory, "settings.json.corrupt-*");
        Assert.Single(quarantined);
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(quarantined[0]));
    }

    /// <summary>
    /// Quarantining is what stops the next save destroying the evidence, so the
    /// original has to be gone from its own name as well as present under the
    /// new one.
    /// </summary>
    [Fact]
    public async Task ACorruptSettingsFile_NoLongerOccupiesTheRealPath()
    {
        using var workspace = new Workspace();
        await File.WriteAllTextAsync(workspace.SettingsPath, "{ this is not json");

        var service = workspace.NewService();
        await service.InitializeAsync();

        Assert.False(File.Exists(workspace.SettingsPath));
    }

    /// <summary>
    /// The control. A readable settings file must be left exactly where it is
    /// and reported as fine - a quarantine that fired on every load would
    /// satisfy the tests above while destroying working settings on startup.
    /// </summary>
    [Fact]
    public async Task AValidSettingsFile_IsNeitherMovedNorReported()
    {
        using var workspace = new Workspace();
        await workspace.NewService().SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 9876 });

        var service = workspace.NewService();
        await service.InitializeAsync();

        Assert.Null(service.LoadFault);
        Assert.True(File.Exists(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.Directory, "settings.json.corrupt-*"));
        Assert.Equal(9876, service.Current.MaxClipSizeBytes);
    }
}
