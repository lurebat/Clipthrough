using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IUpdateService
{
    /// <summary>
    /// Checks for updates against the configured feed and, if one is available, downloads and applies it on next restart.
    /// No-op when the app is not deployed as a Velopack package or when no feed is configured.
    /// </summary>
    Task<UpdateCheckResult> CheckAndApplyAsync(bool ignoreAutoUpdateDisabled = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the Velopack updater for a previously downloaded update while the app is already shutting down.
    /// </summary>
    void ApplyDownloadedUpdateOnExit();
}

public sealed record UpdateCheckResult(bool HasUpdate, string? Version, string? Message);
