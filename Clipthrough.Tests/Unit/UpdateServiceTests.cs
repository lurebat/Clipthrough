using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class UpdateServiceTests
{
    [Fact]
    public void ResolveFeedUrl_UsesDefaultFeedWhenNoOverrideIsConfigured()
    {
        var feedUrl = UpdateService.ResolveFeedUrl(null, null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_EnvironmentOverrideWinsOverSettings()
    {
        var feedUrl = UpdateService.ResolveFeedUrl(
            "https://example.test/settings",
            "https://example.test/environment");

        Assert.Equal("https://example.test/environment", feedUrl);
    }

    [Theory]
    [InlineData("https://github.com/lurebat/Clipthrough/releases/latest/download/RELEASES")]
    [InlineData("https://github.com/lurebat/Clipthrough/releases/latest/download/releases.win.json")]
    [InlineData("https://github.com/lurebat/Clipthrough/releases/latest/download/assets.win.json")]
    public void ResolveFeedUrl_AcceptsReleaseAssetUrls(string configuredFeedUrl)
    {
        var feedUrl = UpdateService.ResolveFeedUrl(configuredFeedUrl, null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }
}
