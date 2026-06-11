using System;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>
    /// Hosts from which update feeds are accepted. Must be HTTPS. Prevents feed
    /// hijacking via the env-override or a tampered settings.json.
    /// </summary>
    private static readonly string[] s_allowedFeedHosts =
    [
        "github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
    ];

    private readonly ISettingsService _settingsService;

    public UpdateService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool ignoreAutoUpdateDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Current;
        if (!settings.EnableAutoUpdate && !ignoreAutoUpdateDisabled)
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

            // An update from a previous run may already be sitting on disk.
            // Don't auto-restart — surface it as "available, ready to install"
            // so the user can apply it on their own schedule.
            if (mgr.UpdatePendingRestart is { } pendingUpdate)
            {
                if (settings.AutoApplyUpdatesOnStartup)
                {
                    var pendingVersion = pendingUpdate.Version?.ToString();
                    Trace.TraceInformation($"Applying downloaded update {pendingVersion} on startup (AutoApplyUpdatesOnStartup=true).");
                    mgr.ApplyUpdatesAndRestart(pendingUpdate);
                    return new UpdateCheckResult(true, pendingVersion, $"Applying update {pendingVersion}");
                }

                var readyVersion = pendingUpdate.Version?.ToString();
                return new UpdateCheckResult(true, readyVersion, $"Update {readyVersion} downloaded and ready to install");
            }

            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return new UpdateCheckResult(false, null, "Up to date");
            }

            await mgr.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
            var version = info.TargetFullRelease?.Version?.ToString();
            return new UpdateCheckResult(true, version, $"Update {version} downloaded and ready to install");
        }
        catch (Exception ex)
        {
            // Velopack throws "Failed to acquire exclusive lock file" when another
            // Clipthrough process is already checking for updates (e.g. when a dev
            // build is running alongside the Velopack-installed release). That is
            // routine, not an error, so don't pollute the session log with it.
            if (ex.Message.Contains("Failed to acquire exclusive lock", StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceInformation("Update check skipped: another Clipthrough instance is already checking.");
                return new UpdateCheckResult(false, null, "Another Clipthrough instance is checking for updates");
            }

            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(false, null, ex.Message);
        }
    }

    public bool ApplyDownloadedUpdateAndRestart()
    {
        var settings = _settingsService.Current;
        var feedUrl = ResolveFeedUrl(settings);
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return false;
        }

        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(feedUrl));
            if (!mgr.IsInstalled || mgr.UpdatePendingRestart is not { } pendingUpdate)
            {
                return false;
            }

            var version = pendingUpdate.Version?.ToString();
            Trace.TraceInformation($"User-initiated apply: restarting to install update {version}.");
            mgr.ApplyUpdatesAndRestart(pendingUpdate);
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"User-initiated update apply failed: {ex.Message}");
            return false;
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
        // In production builds the configured feed always wins; the env-var is a
        // development escape hatch and must not outrank a real configuration.
#if DEBUG
        var feedUrl = string.IsNullOrWhiteSpace(environmentFeedUrl)
            ? configuredFeedUrl
            : environmentFeedUrl;
#else
        var feedUrl = string.IsNullOrWhiteSpace(configuredFeedUrl)
            ? environmentFeedUrl
            : configuredFeedUrl;
#endif
        if (string.IsNullOrWhiteSpace(feedUrl))
            feedUrl = AppSettings.DefaultUpdateFeedUrl;

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
                trimmed = trimmed[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        // Enforce HTTPS and an allowlisted host to prevent feed hijacking.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            Trace.TraceWarning($"Update feed URL is not a valid absolute URI; using default. (Input: '{trimmed}')");
            return AppSettings.DefaultUpdateFeedUrl;
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            Trace.TraceWarning($"Update feed URL must use HTTPS; using default. (Scheme: '{uri.Scheme}')");
            return AppSettings.DefaultUpdateFeedUrl;
        }

        var host = uri.Host;
        if (!s_allowedFeedHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
        {
            Trace.TraceWarning($"Update feed URL host '{host}' is not on the allowlist; using default.");
            return AppSettings.DefaultUpdateFeedUrl;
        }

        return trimmed;
    }
}
