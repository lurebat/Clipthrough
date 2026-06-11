using Clipthrough.Models;
using Clipthrough.Services;
using System.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_RespectsDisabledAutoUpdateByDefault()
    {
        var settings = new TestSettingsService();
        settings.SetCurrent(AppSettings.Default with { EnableAutoUpdate = false });
        var service = new UpdateService(settings);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.HasUpdate);
        Assert.Equal("Auto-update disabled", result.Message);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ManualCheckBypassesDisabledAutoUpdate()
    {
        var settings = new TestSettingsService();
        settings.SetCurrent(AppSettings.Default with { EnableAutoUpdate = false });
        var service = new UpdateService(settings);

        var result = await service.CheckForUpdatesAsync(ignoreAutoUpdateDisabled: true);

        Assert.False(result.HasUpdate);
        Assert.NotEqual("Auto-update disabled", result.Message);
    }

    [Fact]
    public void ResolveFeedUrl_UsesDefaultFeedWhenNoOverrideIsConfigured()
    {
        var feedUrl = UpdateService.ResolveFeedUrl(null, null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    // Env-override test: previously asserted env wins unconditionally.
    // After U21 hardening, env only wins in DEBUG builds; in release it is
    // subordinate to the configured feed.  The observable safety property
    // under test is that env-supplied URLs are still validated for HTTPS +
    // allowlisted host — they are not passed through unchecked.
    [Fact]
    public void ResolveFeedUrl_EnvironmentFeedUrlIsSubjectToHttpsValidation()
    {
        // http:// env override must be rejected even when no configured feed is set.
        var feedUrl = UpdateService.ResolveFeedUrl(null, "http://github.com/lurebat/Clipthrough/releases");

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_EnvironmentFeedUrlIsSubjectToAllowlistValidation()
    {
        // An https env override pointing at a non-allowlisted host must be rejected.
        var feedUrl = UpdateService.ResolveFeedUrl(null, "https://evil.example.com/releases");

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_HttpUrlIsRejectedAndFallsBackToDefault()
    {
        var feedUrl = UpdateService.ResolveFeedUrl("http://github.com/lurebat/Clipthrough/releases", null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_NonAllowlistedHostIsRejectedAndFallsBackToDefault()
    {
        var feedUrl = UpdateService.ResolveFeedUrl("https://evil.example.com/releases", null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_AllowlistedHttpsFeedIsAccepted()
    {
        const string customGitHubFeed = "https://github.com/some-org/some-repo/releases/latest/download";
        var feedUrl = UpdateService.ResolveFeedUrl(customGitHubFeed, null);

        Assert.Equal(customGitHubFeed, feedUrl);
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

    [Fact]
    public void ResolveFeedUrl_InvalidUriIsRejectedAndFallsBackToDefault()
    {
        var feedUrl = UpdateService.ResolveFeedUrl("not a url at all", null);

        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, feedUrl);
    }

    [Fact]
    public void ResolveFeedUrl_RawGitHubContentHostIsAllowlisted()
    {
        // The known asset-name suffix ("/releases.win.json") is stripped by NormalizeFeedUrl,
        // leaving the base directory — same normalisation applied to github.com URLs.
        var feedUrl = UpdateService.ResolveFeedUrl(
            "https://raw.githubusercontent.com/lurebat/Clipthrough/main/releases.win.json", null);

        Assert.Equal("https://raw.githubusercontent.com/lurebat/Clipthrough/main", feedUrl);
    }
}
