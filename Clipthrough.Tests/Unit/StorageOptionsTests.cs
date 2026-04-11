using System;
using System.IO;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class StorageOptionsTests
{
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

            var normalized = options.Normalize();

            Assert.Equal(Path.Combine(Path.GetTempPath(), "clipthrough-test.db"), normalized.DatabasePath);
            Assert.Equal("secret", normalized.DatabasePassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPTHROUGH_TEST_DB_ROOT", null);
        }
    }

    [Fact]
    public void Normalize_UsesDefaultPathWhenMissing()
    {
        var normalized = new StorageOptions { DatabasePath = " " }.Normalize();

        Assert.Equal(StorageOptions.GetDefaultDatabasePath(), normalized.DatabasePath);
    }
}
