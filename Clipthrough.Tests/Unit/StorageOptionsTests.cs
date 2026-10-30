using System;
using System.IO;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class StorageOptionsTests
{
    private static readonly string s_fakeLocalAppData = Path.Combine(Path.GetTempPath(), "ct-storage-options-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Normalize_ExpandsEnvironmentVariablesAndTrimsPassword()
    {
        Environment.SetEnvironmentVariable("CLIPTHROUGH_TEST_DB_ROOT", Path.GetTempPath());
        try
        {
            var options = new StorageOptions
            {
                DatabasePath = " %CLIPTHROUGH_TEST_DB_ROOT%\\clipthrough-test.db ",
                DatabasePassword = "  secret  ",
            };

            var normalized = options.Normalize(AppDataRoot.Default(s_fakeLocalAppData));

            Assert.Equal(Path.Combine(Path.GetTempPath(), "clipthrough-test.db"), normalized.DatabasePath);
            Assert.Equal("secret", normalized.DatabasePassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPTHROUGH_TEST_DB_ROOT", null);
        }
    }

    [Fact]
    public void Normalize_UsesTheDefaultRootsUninstallSafeLibraryWhenMissing()
    {
        var normalized = new StorageOptions { DatabasePath = " " }.Normalize(AppDataRoot.Default(s_fakeLocalAppData));

        Assert.Equal(Path.Combine(s_fakeLocalAppData, "ClipthroughData", "clipthrough.db"), normalized.DatabasePath);
    }

    [Fact]
    public void Normalize_UsesTheLibraryInsideAChosenRootWhenMissing()
    {
        var root = AppDataRoot.Chosen(Path.Combine(s_fakeLocalAppData, "chosen"), AppDataRootSource.CommandLine);

        var normalized = new StorageOptions().Normalize(root);

        Assert.Equal(Path.Combine(s_fakeLocalAppData, "chosen", "clipthrough.db"), normalized.DatabasePath);
    }

    // Against the data folder, never the working directory: that is current\
    // under Velopack, which the next update deletes, and whatever folder a
    // shortcut or script happened to start in otherwise.
    [Fact]
    public void Normalize_ResolvesARelativeLibraryAgainstTheDataRoot()
    {
        var root = AppDataRoot.Chosen(Path.Combine(s_fakeLocalAppData, "chosen"), AppDataRootSource.Portable);

        var normalized = new StorageOptions { DatabasePath = Path.Combine("library", "clips.db") }.Normalize(root);

        Assert.Equal(Path.Combine(s_fakeLocalAppData, "chosen", "library", "clips.db"), normalized.DatabasePath);
    }
}