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
