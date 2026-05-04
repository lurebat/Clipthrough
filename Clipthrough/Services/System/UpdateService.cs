using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Velopack;
using Velopack.Sources;

namespace Clipthrough.Services;

public sealed class UpdateService : IUpdateService
{
    private const string UpdateFeedEnvironmentVariable = "CLIPTHROUGH_UPDATE_FEED";
    private static readonly string[] s_knownFeedAssetNames =
    [
        "RELEASES",
        "releases.win.json",
        "assets.win.json",
    ];

    private readonly ISettingsService _settingsService;

    public UpdateService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<UpdateCheckResult> CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Current;
        if (!settings.EnableAutoUpdate)
        {
            return new UpdateCheckResult(false, null, "Auto-update disabled");
        }

        var feedUrl = ResolveFeedUrl(settings);

        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return new UpdateCheckResult(false, null, "No update feed configured");
        }

        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(feedUrl));
            if (!mgr.IsInstalled)
            {
                return new UpdateCheckResult(false, null, "Not installed via Velopack");
            }

            if (mgr.UpdatePendingRestart is { } pendingUpdate)
            {
                var pendingVersion = pendingUpdate.Version?.ToString();
                Trace.TraceInformation($"Applying downloaded update {pendingVersion}.");
                mgr.ApplyUpdatesAndRestart(pendingUpdate);
                return new UpdateCheckResult(true, pendingVersion, $"Applying update {pendingVersion}");
            }

            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return new UpdateCheckResult(false, null, "Up to date");
            }

            await mgr.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
            var version = info.TargetFullRelease?.Version?.ToString();
            return new UpdateCheckResult(true, version, $"Update {version} downloaded; will apply when Clipthrough exits");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(false, null, ex.Message);
        }
    }

    public void ApplyDownloadedUpdateOnExit()
    {
        var settings = _settingsService.Current;
        if (!settings.EnableAutoUpdate)
        {
            return;
        }

        var feedUrl = ResolveFeedUrl(settings);
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return;
        }

        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(feedUrl));
            if (!mgr.IsInstalled || mgr.UpdatePendingRestart is not { } pendingUpdate)
            {
                return;
            }

            var version = pendingUpdate.Version?.ToString();
            Trace.TraceInformation($"Scheduling downloaded update {version} to apply after exit.");
            mgr.WaitExitThenApplyUpdates(pendingUpdate, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Applying downloaded update on exit failed: {ex.Message}");
        }
    }

    public static string ResolveFeedUrl(AppSettings settings)
        => ResolveFeedUrl(settings.UpdateFeedUrl, Environment.GetEnvironmentVariable(UpdateFeedEnvironmentVariable));

    public static string ResolveFeedUrl(string? configuredFeedUrl, string? environmentFeedUrl)
    {
        var feedUrl = string.IsNullOrWhiteSpace(environmentFeedUrl)
            ? configuredFeedUrl
            : environmentFeedUrl;

        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            feedUrl = AppSettings.DefaultUpdateFeedUrl;
        }

        return NormalizeFeedUrl(feedUrl);
    }

    private static string NormalizeFeedUrl(string feedUrl)
    {
        var trimmed = feedUrl.Trim().TrimEnd('/');
        foreach (var assetName in s_knownFeedAssetNames)
        {
            var suffix = "/" + assetName;
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length].TrimEnd('/');
            }
        }

        return trimmed;
    }
}
