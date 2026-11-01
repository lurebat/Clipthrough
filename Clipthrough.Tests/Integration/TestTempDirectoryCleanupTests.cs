using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Per-test databases have to be gone when the test is. Disposal used to delete
/// the directory while Microsoft.Data.Sqlite's pool still held the database open,
/// and swallowed the IOException, so every test that used a database left a
/// directory behind - tens of gigabytes on a machine that runs the suite often.
/// </summary>
public sealed class TestTempDirectoryCleanupTests
{
    [Fact]
    public async Task DisposingAScopeThatUsedTheDatabase_RemovesItsDirectory()
    {
        string directory;
        using (var scope = new TemporaryDatabaseScope())
        {
            directory = scope.DirectoryPath;
            await scope.DatabaseInitializer.InitializeAsync();
            scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

            // A real write and read, so pooled connections exist when the scope ends.
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "pooled",
                ContentBytes = Encoding.UTF8.GetBytes("pooled"),
                SourceApp = "Tests",
            });
            Assert.NotNull(clip);
            Assert.True(File.Exists(scope.DatabasePath), "the test must exercise a real database file");
        }

        Assert.False(Directory.Exists(directory), $"per-test directory was left behind: {directory}");
    }

    [Fact]
    public void SweepStale_RemovesOnlyOldPerTestDirectories()
    {
        var root = Path.Combine(TestTempDirectories.Root, Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var fresh = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var foreign = Path.Combine(root, "not-a-test-directory");
        try
        {
            foreach (var path in new[] { old, fresh, foreign })
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "clipthrough.db"), "x");
            }

            var longAgo = DateTime.UtcNow.AddDays(-3);
            foreach (var path in new[] { old, foreign })
            {
                Directory.SetCreationTimeUtc(path, longAgo);
                Directory.SetLastWriteTimeUtc(path, longAgo);
            }

            var removed = TestTempDirectories.SweepStale(root, TimeSpan.FromHours(24));

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(old), "a stale per-test directory survived the sweep");
            Assert.True(Directory.Exists(fresh), "the sweep removed a directory a live run could still own");
            Assert.True(Directory.Exists(foreign), "the sweep removed a directory it did not create");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
