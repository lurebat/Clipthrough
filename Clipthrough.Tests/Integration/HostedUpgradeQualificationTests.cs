using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class HostedUpgradeQualificationTests
{
    private const string Marker = "Clipthrough hosted upgrade qualification synthetic clip ";
    private const string RootVariable = "CLIPTHROUGH_QUALIFICATION_DATA_ROOT";
    private const string ModeVariable = "CLIPTHROUGH_QUALIFICATION_MODE";
    private const string SourceVariable = "CLIPTHROUGH_QUALIFICATION_SOURCE";
    private const string DatabasePathVariable = "CLIPTHROUGH_QUALIFICATION_DATABASE_PATH";

    [Fact]
    public async Task SeedOrVerifySyntheticLibraryThroughShippingStorageServices()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        var sourceValue = Environment.GetEnvironmentVariable(SourceVariable);
        var configuredDatabasePath = Environment.GetEnvironmentVariable(DatabasePathVariable);
        var sourceIsValid = Enum.TryParse<AppDataRootSource>(sourceValue, out _);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(root)
            && (mode is "seed" or "verify")
            && sourceIsValid,
            $"Runs only as a hosted qualification child with {RootVariable}, {ModeVariable}, and {SourceVariable} set.");
        Assert.True(Enum.TryParse<AppDataRootSource>(sourceValue, out var source));

        Directory.CreateDirectory(root!);
        var dataRoot = AppDataRoot.Chosen(root!, source);
        using var services = new ServiceCollection().BuildServiceProvider();
        var storage = new StorageOptionsService(new FakeDataProtectionService(), dataRoot, services);
        var expectedDatabasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
            ? Path.GetFullPath(Path.Combine(root!, AppDataRoot.DatabaseFileName))
            : Path.GetFullPath(configuredDatabasePath, root!);
        Assert.Equal(expectedDatabasePath, storage.Current.DatabasePath);
        var factory = new SqliteConnectionFactory(storage);
        var sensitivity = new SensitivityService(factory);
        await new DatabaseInitializer(factory, sensitivity).InitializeAsync();
        var store = new ClipStoreService(factory, sensitivity, new TestSettingsService(), new TestNotificationService());

        if (mode == "seed")
        {
            foreach (var suffix in new[] { "alpha", "beta" })
            {
                var content = Marker + suffix;
                var captured = await store.CaptureAsync(new ClipCaptureRequest
                {
                    ContentType = ContentType.Text,
                    ContentFormat = ClipContentFormat.PlainText,
                    ContentText = content,
                    ContentBytes = Encoding.UTF8.GetBytes(content),
                    SourceApp = "HostedQualificationFixture",
                });
                Assert.NotNull(captured);
            }
        }

        var results = await store.SearchAsync(new ClipSearchFilters());
        var clips = results.Items.Where(clip => clip.Content?.StartsWith(Marker, StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, clips.Length);
        Assert.Equal(new[] { Marker + "alpha", Marker + "beta" }, clips.Select(clip => clip.Content).OrderBy(value => value, StringComparer.Ordinal));

        foreach (var clip in clips)
        {
            var hydrated = await store.GetByIdAsync(clip.Id);
            Assert.NotNull(hydrated);
            Assert.StartsWith(Marker, hydrated.Content, StringComparison.Ordinal);
            Assert.Equal(clip.Id, hydrated.Id);
        }
    }
}
