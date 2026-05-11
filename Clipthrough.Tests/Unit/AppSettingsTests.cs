using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultMaxClipSize_Is2048Kilobytes()
    {
        Assert.Equal(2_048 * 1_024, AppSettings.Default.MaxClipSizeBytes);
    }

    [Fact]
    public void Defaults_EnableAutoUpdateWithGitHubReleaseFeed()
    {
        Assert.True(AppSettings.Default.EnableAutoUpdate);
        Assert.Equal(
            "https://github.com/lurebat/Clipthrough/releases/latest/download",
            AppSettings.Default.UpdateFeedUrl);
    }

    [Fact]
    public void Defaults_AutoApplyUpdatesOnStartup_IsOffByDefault()
    {
        // The aggressive "silently restart on next launch when an update was
        // previously downloaded" behavior is opt-in only; the friendly default
        // surfaces a notification with explicit user consent.
        Assert.False(AppSettings.Default.AutoApplyUpdatesOnStartup);
    }

    [Fact]
    public void Normalize_AllowsDefaultMaxClipSize()
    {
        var settings = new AppSettings
        {
            MaxClipSizeBytes = 2_048 * 1_024,
        };

        var normalized = settings.Normalize();

        Assert.Equal(2_048 * 1_024, normalized.MaxClipSizeBytes);
    }
}
