using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Settings are written from several places at once - debounced filter toggles,
/// the content/image view mode, preset seeding, and the settings dialog. Each
/// used to build a whole record from <c>Current</c> and hand it to
/// <c>SaveAsync</c>, so two saves that started from the same snapshot both
/// succeeded and the later one reverted the earlier one's field. The write gate
/// serialized them, which prevented a torn file and nothing else.
/// </summary>
public sealed class SettingsServiceUpdateTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsPath;

    public SettingsServiceUpdateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "clipthrough-settings-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _settingsPath = Path.Combine(_tempRoot, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    private SettingsService NewService()
    {
        var storageOptions = new TestStorageOptionsService(Path.Combine(_tempRoot, "test.db"));
        return new SettingsService(new SqliteConnectionFactory(storageOptions), new FakeDataProtectionService(), _settingsPath);
    }

    /// <summary>
    /// The load-bearing one. A second writer queued behind the gate must see
    /// what the first writer published, not the value it would have read when it
    /// was queued - otherwise its record silently reverts the first change.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_AppliesTheMutationToWhatTheGateHolderPublished()
    {
        var service = NewService();
        await service.InitializeAsync();

        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() => service.UpdateAsync(current =>
        {
            firstIsInside.SetResult();
            releaseFirst.Task.GetAwaiter().GetResult();
            return current with { LastContentDisplayMode = ContentDisplayMode.Raw };
        }));

        await firstIsInside.Task;

        ContentDisplayMode seenBySecond = default;
        var second = Task.Run(() => service.UpdateAsync(current =>
        {
            seenBySecond = current.LastContentDisplayMode;
            return current with { LastShowFavoritesOnly = true };
        }));

        // Give the second writer every chance to run early if it is allowed to.
        await Task.Delay(50);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(ContentDisplayMode.Raw, seenBySecond);
        Assert.Equal(ContentDisplayMode.Raw, service.Current.LastContentDisplayMode);
        Assert.True(service.Current.LastShowFavoritesOnly);
    }

    /// <summary>
    /// The same interleaving expressed the way callers actually hit it: a writer
    /// that composed its change from a snapshot taken before an unrelated save
    /// landed must not roll that save back.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_FromAStaleSnapshot_DoesNotRevertAnUnrelatedSave()
    {
        var service = NewService();
        await service.InitializeAsync();
        await service.SaveAsync(service.Current with { LastShowFavoritesOnly = false });

        // What a debounced filter save would have captured.
        var staleWantsFavorites = true;

        // Meanwhile the view-mode save lands.
        await service.UpdateAsync(c => c with { LastImageViewMode = ImageViewMode.Preview });

        await service.UpdateAsync(c => c with { LastShowFavoritesOnly = staleWantsFavorites });

        Assert.True(service.Current.LastShowFavoritesOnly);
        Assert.Equal(ImageViewMode.Preview, service.Current.LastImageViewMode);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsTheNormalizedResult()
    {
        var service = NewService();
        await service.InitializeAsync();

        var result = await service.UpdateAsync(c => c with { LastShowPastedOnly = true });

        Assert.True(result.LastShowPastedOnly);
        Assert.Equal(service.Current, result);
    }

    /// <summary>
    /// The settings dialog composes its record when it opens, so its copy of the
    /// session state is stale by the time the user hits Save.
    /// </summary>
    [Fact]
    public void WithSessionStateFrom_KeepsConfigurationAndTakesSessionState()
    {
        var dialog = AppSettings.Default with
        {
            ThemeMode = ThemeMode.Dark,
            LastShowFavoritesOnly = false,
            LastContentDisplayMode = ContentDisplayMode.Rendered,
            LastUseWildcardSearch = false,
            LastContentTypeFilters = new[] { ContentType.Text },
        };

        var live = AppSettings.Default with
        {
            ThemeMode = ThemeMode.Light,
            LastShowFavoritesOnly = true,
            LastContentDisplayMode = ContentDisplayMode.Raw,
            LastUseWildcardSearch = true,
            LastContentTypeFilters = new[] { ContentType.Image },
        };

        var merged = dialog.WithSessionStateFrom(live);

        Assert.Equal(ThemeMode.Dark, merged.ThemeMode);
        Assert.True(merged.LastShowFavoritesOnly);
        Assert.Equal(ContentDisplayMode.Raw, merged.LastContentDisplayMode);
        Assert.True(merged.LastUseWildcardSearch);
        Assert.Equal(new[] { ContentType.Image }, merged.LastContentTypeFilters);
    }
}
