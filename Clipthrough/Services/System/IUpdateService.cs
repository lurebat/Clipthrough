using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IUpdateService
{
    /// <summary>
    /// Checks for updates against the configured feed and downloads any newer
    /// build in the background. Does NOT restart the running application —
    /// applying the update is gated behind explicit user consent via
    /// <see cref="ApplyDownloadedUpdateAndRestart"/> or the on-exit handler.
    /// No-op when the app is not deployed as a Velopack package, when no
    /// feed is configured, or when auto-update is disabled in settings.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a previously downloaded update and restarts the application
    /// immediately. Used for user-initiated "Restart and install" actions.
    /// Returns false if there is no pending update to apply.
    /// </summary>
    bool ApplyDownloadedUpdateAndRestart();

    /// <summary>
    /// Starts the Velopack updater for a previously downloaded update while
    /// the app is already shutting down. The friendly default — applies the
    /// update after the user closes Clipthrough.
    /// </summary>
    void ApplyDownloadedUpdateOnExit();
}

public sealed record UpdateCheckResult(bool HasUpdate, string? Version, string? Message);
