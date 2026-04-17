using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Clipthrough.Services;

public sealed class UpdateService : IUpdateService
{
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

        var feedUrl = string.IsNullOrWhiteSpace(settings.UpdateFeedUrl)
            ? Environment.GetEnvironmentVariable("CLIPTHROUGH_UPDATE_FEED")
            : settings.UpdateFeedUrl;

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

            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return new UpdateCheckResult(false, null, "Up to date");
            }

            await mgr.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
            // Apply on next restart — don't force relaunch from a background check.
            mgr.WaitExitThenApplyUpdates(info);
            var version = info.TargetFullRelease?.Version?.ToString();
            return new UpdateCheckResult(true, version, $"Update {version} downloaded; will apply on next launch");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(false, null, ex.Message);
        }
    }
}
