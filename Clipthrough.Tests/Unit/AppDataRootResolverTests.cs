using System;
using System.IO;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Where Clipthrough keeps its files, and which of the four ways to say so wins.
/// Every layout here is a temp directory: nothing reads or writes the real
/// %LOCALAPPDATA%, which is passed in rather than asked for.
/// </summary>
public sealed class AppDataRootResolverTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ct-dataroot-" + Guid.NewGuid().ToString("N"));
    private readonly string _localAppData;
    private readonly string _exeDirectory;

    public AppDataRootResolverTests()
    {
        _localAppData = Directory.CreateDirectory(Path.Combine(_work, "LocalAppData")).FullName;
        _exeDirectory = Directory.CreateDirectory(Path.Combine(_work, "app")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(_work, name);

    private void PlacePortableMarker(string directory)
        => File.WriteAllText(Path.Combine(directory, AppDataRootResolver.PortableMarkerFileName), string.Empty);

    private AppDataRoot Resolve(string[] args, string? environment = null)
        => AppDataRootResolver.Resolve(args, environment, _exeDirectory, _localAppData);

    [Fact]
    public void NothingChosen_UsesLocalAppDataWithTheLibraryInItsUninstallSafeSibling()
    {
        var root = Resolve([]);

        Assert.Equal(AppDataRootSource.Default, root.Source);
        Assert.Equal(Path.Combine(_localAppData, "Clipthrough"), root.Directory);
        Assert.Equal(Path.Combine(_localAppData, "Clipthrough", "settings.json"), root.SettingsPath);
        Assert.Equal(Path.Combine(_localAppData, "ClipthroughData", "clipthrough.db"), root.DefaultDatabasePath);
        Assert.Equal(Path.Combine(_localAppData, "Clipthrough", "clipthrough.db"), root.LegacyDatabasePath);
    }

    [Fact]
    public void CommandLine_WinsOverTheEnvironmentAndThePortableMarker()
    {
        PlacePortableMarker(_exeDirectory);

        var root = Resolve(["--data-dir", Dir("cli")], environment: Dir("env"));

        Assert.Equal(AppDataRootSource.CommandLine, root.Source);
        Assert.Equal(Dir("cli"), root.Directory);
    }

    [Fact]
    public void CommandLine_AcceptsTheEqualsSpelling()
    {
        var root = Resolve(["--data-dir=" + Dir("cli")]);

        Assert.Equal(AppDataRootSource.CommandLine, root.Source);
        Assert.Equal(Dir("cli"), root.Directory);
    }

    [Fact]
    public void Environment_WinsOverThePortableMarker()
    {
        PlacePortableMarker(_exeDirectory);

        var root = Resolve([], environment: Dir("env"));

        Assert.Equal(AppDataRootSource.EnvironmentVariable, root.Source);
        Assert.Equal(Dir("env"), root.Directory);
    }

    [Fact]
    public void PortableMarker_KeepsTheDataInAFolderBesideTheExecutable()
    {
        PlacePortableMarker(_exeDirectory);

        var root = Resolve([]);

        Assert.Equal(AppDataRootSource.Portable, root.Source);
        Assert.Equal(Path.Combine(_exeDirectory, "ClipthroughData"), root.Directory);
    }

    [Fact]
    public void ChosenRoot_KeepsEveryFileAndTheLibraryInside()
    {
        var root = Resolve(["--data-dir", Dir("cli")]);

        Assert.Equal(Path.Combine(Dir("cli"), "settings.json"), root.SettingsPath);
        Assert.Equal(Path.Combine(Dir("cli"), "storage.json"), root.StorageConfigPath);
        Assert.Equal(Path.Combine(Dir("cli"), "window.json"), root.WindowPlacementPath);
        Assert.Equal(Path.Combine(Dir("cli"), "copilot-token.bin"), root.CopilotTokenPath);
        Assert.Equal(Path.Combine(Dir("cli"), "logs", "clipthrough.log"), root.LogFilePath);
        Assert.Equal(Path.Combine(Dir("cli"), "clipthrough.db"), root.DefaultDatabasePath);
        Assert.Null(root.LegacyDatabasePath);
    }

    // Velopack launches the app with its own arguments during install and
    // update; none of them may be read as a data folder.
    [Theory]
    [InlineData("--veloapp-install", "1.2.3")]
    [InlineData("--squirrel-firstrun")]
    [InlineData("--log-timings")]
    public void UnrelatedArguments_DoNotChooseARoot(params string[] args)
        => Assert.Equal(AppDataRootSource.Default, Resolve(args).Source);

    // The password's value is consumed by --password, so a password that
    // happens to read "--data-dir" must not swallow the next argument.
    [Fact]
    public void ThePasswordValue_IsNotReadAsAnOption()
    {
        var root = Resolve(["--password", "--data-dir", "--log-timings"]);

        Assert.Equal(AppDataRootSource.Default, root.Source);
    }

    // Help is read before the data folder, so it prints even when that folder
    // is unusable; the password's value is still not an option.
    [Theory]
    [InlineData(true, "--help")]
    [InlineData(true, "--data-dir", "--help")]
    [InlineData(false, "--password", "--help")]
    [InlineData(false, "--data-dir", "D:\\data")]
    public void Help_IsReadWithoutTheRestOfTheCommandLine(bool expected, params string[] args)
        => Assert.Equal(expected, CommandLineOptions.IsHelpRequested(args));

    [Fact]
    public void ARelativeCommandLinePath_ResolvesAgainstTheCurrentDirectory()
    {
        var root = Resolve(["--data-dir", "relative-data"]);

        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "relative-data"), root.Directory);
    }

    /// <summary>
    /// Builds the layout a Velopack package unpacks to - the Setup install and
    /// the -Portable.zip alike: Update.exe and the launcher stub at the root,
    /// the real executable in current\ beside the sq.version manifest. An
    /// update replaces current\ wholesale, so nothing that must survive one -
    /// the marker, or the data - may live inside it.
    /// </summary>
    private (string Root, string Current) VelopackLayout()
    {
        var root = Directory.CreateDirectory(Path.Combine(_work, "velopack")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
        File.WriteAllText(Path.Combine(root, "Update.exe"), string.Empty);
        File.WriteAllText(Path.Combine(current, "sq.version"), "<package/>");
        return (root, current);
    }

    [Fact]
    public void UnderVelopack_TheMarkerAndTheDataSitBesideUpdateExe_OutsideCurrent()
    {
        var (root, current) = VelopackLayout();
        PlacePortableMarker(root);

        var dataRoot = AppDataRootResolver.Resolve([], null, current, _localAppData);

        Assert.Equal(AppDataRootSource.Portable, dataRoot.Source);
        Assert.Equal(Path.Combine(root, "ClipthroughData"), dataRoot.Directory);
    }

    // Honouring a marker inside current\ would move the data folder back to
    // the default at the next update, which deletes the marker with the rest
    // of current\.
    [Fact]
    public void UnderVelopack_AMarkerInsideCurrent_IsNotPortable()
    {
        var (_, current) = VelopackLayout();
        PlacePortableMarker(current);

        var dataRoot = AppDataRootResolver.Resolve([], null, current, _localAppData);

        Assert.Equal(AppDataRootSource.Default, dataRoot.Source);
    }

    // Only a manifest beside the executable makes the parent a Velopack root,
    // as in Velopack's own locator: a plain ZIP unpacked under some other
    // Squirrel-style folder is still its own root.
    [Fact]
    public void APlainCopyBelowAnUnrelatedUpdateExe_IsItsOwnRoot()
    {
        File.WriteAllText(Path.Combine(_work, "Update.exe"), string.Empty);
        PlacePortableMarker(_exeDirectory);

        var dataRoot = Resolve([]);

        Assert.Equal(Path.Combine(_exeDirectory, "ClipthroughData"), dataRoot.Directory);
    }

    // Velopack restarts the app after applying an update. The marker and the
    // variable survive that on their own; the command line has to be handed
    // back, or the updated app comes up on the default folder.
    [Fact]
    public void ARelaunch_KeepsACommandLineFolder()
    {
        var root = Resolve(["--data-dir", Dir("cli")]);

        Assert.Equal(["--data-dir", Dir("cli")], root.RelaunchArguments);
    }

    [Fact]
    public void ARelaunch_NeedsNothingForTheOtherSources()
    {
        PlacePortableMarker(_exeDirectory);

        Assert.Empty(Resolve([]).RelaunchArguments);
        Assert.Empty(Resolve([], environment: Dir("env")).RelaunchArguments);
        Assert.Empty(AppDataRoot.Default(_localAppData).RelaunchArguments);
    }
}
