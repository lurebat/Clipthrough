using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface ICopilotAuthService
{
    /// <summary>
    /// Whether we currently hold a valid (or potentially refreshable) Copilot token.
    /// </summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// Raised when <see cref="IsSignedIn"/> changes.
    /// </summary>
    event System.Action? SignedInChanged;

    /// <summary>
    /// Begin a GitHub device-code flow. Returns the user code and verification URI.
    /// The caller should display the code and open the URI in a browser.
    /// Once the user authorizes, the service stores the tokens internally.
    /// </summary>
    Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Poll GitHub until the user completes authorization or the code expires.
    /// Returns true if sign-in succeeded, false if it expired or was denied.
    /// </summary>
    Task<bool> PollForAuthorizationAsync(DeviceCodeResult deviceCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a valid Copilot API token, refreshing if needed.
    /// Throws if not signed in.
    /// </summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all stored tokens and sign out.
    /// </summary>
    void SignOut();
}

public sealed record DeviceCodeResult(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);
