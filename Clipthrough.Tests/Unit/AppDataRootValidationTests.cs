using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// A data folder that cannot be used stops Clipthrough with a message, and
/// never silently becomes another folder. Falling back would be the worst
/// failure available: the user's clips would go somewhere they did not choose,
/// and an isolated test session would quietly share the real install's state.
/// </summary>
public sealed class AppDataRootValidationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ct-dataroot-valid-" + Guid.NewGuid().ToString("N"));
    private readonly string _localAppData;
    private readonly string _exeDirectory;

    public AppDataRootValidationTests()
    {
        _localAppData = Directory.CreateDirectory(Path.Combine(_work, "LocalAppData")).FullName;
        _exeDirectory = Directory.CreateDirectory(Path.Combine(_work, "app")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AppDataRoot Resolve(string[] args, string? environment = null)
        => AppDataRootResolver.Resolve(args, environment, _exeDirectory, _localAppData);

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir=")]
    [InlineData("--data-dir=   ")]
    public void ADataDirOptionWithoutAFolder_IsAnError(string arg)
    {
        var error = Assert.Throws<AppDataRootException>(() => Resolve([arg], environment: Path.Combine(_work, "env")));

        Assert.Equal(AppText.DataFolderMissingValue, error.Message);
    }

    [Fact]
    public void ABlankEnvironmentVariable_IsAnError()
    {
        var error = Assert.Throws<AppDataRootException>(() => Resolve([], environment: "   "));

        Assert.Equal(AppText.FormatDataFolderBlank(AppDataRootSource.EnvironmentVariable), error.Message);
    }

    // An option that names an impossible path does not hand over to the next
    // way of choosing a folder: precedence picks the source, and the source
    // either works or is reported.
    [Fact]
    public void AnInvalidCommandLinePath_DoesNotFallBackToTheEnvironment()
    {
        var error = Assert.Throws<AppDataRootException>(() => Resolve(["--data-dir", "bad\0path"], environment: Path.Combine(_work, "env")));

        Assert.Contains("--data-dir", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWhereTheFolderShouldBe_IsUnusable()
    {
        var path = Path.Combine(_work, "taken");
        File.WriteAllText(path, "not a folder");
        var root = Resolve(["--data-dir", path]);

        var error = Assert.Throws<AppDataRootException>(() => AppDataRootResolver.EnsureUsable(root));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains(AppText.GetDataFolderSourceLabel(AppDataRootSource.CommandLine), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderThatCannotBeWritten_IsUnusable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Directory.CreateDirectory(Path.Combine(_work, "read-only")).FullName;
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.AppendData,
            AccessControlType.Deny);
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            var root = Resolve([], environment: path);

            var error = Assert.Throws<AppDataRootException>(() => AppDataRootResolver.EnsureUsable(root));

            Assert.Contains(path, error.Message, StringComparison.Ordinal);
            Assert.Contains(AppText.GetDataFolderSourceLabel(AppDataRootSource.EnvironmentVariable), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            directory.SetAccessControl(security);
        }
    }

    [Fact]
    public void AUsableFolder_IsCreatedAndLeftEmpty()
    {
        var path = Path.Combine(_work, "fresh", "nested");
        var root = Resolve(["--data-dir", path]);

        AppDataRootResolver.EnsureUsable(root);

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    [Fact]
    public void Startup_ReportsAnUnusableFolder_AndHasNoRootToRunFrom()
    {
        var path = Path.Combine(_work, "taken");
        File.WriteAllText(path, "not a folder");
        string? title = null;
        string? message = null;

        var root = Program.ResolveDataRootOrReport(
            () => Resolve(["--data-dir", path]),
            (t, m) => (title, message) = (t, m));

        Assert.Null(root);
        Assert.Equal(AppText.DataFolderErrorTitle, title);
        Assert.Contains(path, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_ReportsABadOption_AndHasNoRootToRunFrom()
    {
        string? message = null;

        var root = Program.ResolveDataRootOrReport(() => Resolve(["--data-dir"]), (_, m) => message = m);

        Assert.Null(root);
        Assert.Equal(AppText.DataFolderMissingValue, message);
    }
}
