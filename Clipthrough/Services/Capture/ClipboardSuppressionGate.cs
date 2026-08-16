using System;
using System.Threading;

namespace Clipthrough.Services;

/// <summary>
/// Tracks clipboard writes the application makes itself, so the monitor can ignore the
/// resulting change notification instead of re-capturing its own output.
/// </summary>
/// <remarks>
/// <para>
/// A suppression is armed immediately before a write. If that write fails, or the clipboard
/// contents happen to be identical so Windows raises no notification, the suppression would
/// otherwise stay armed forever and silently swallow the user's next real copy. The expiry
/// window bounds that: a stale suppression stops mattering after <see cref="DefaultWindow"/>.
/// </para>
/// <para>
/// Pending suppressions are drained as a batch rather than one per notification, because the
/// monitor coalesces rapid changes - several writes can legitimately produce a single
/// notification. Draining one at a time would leave a residue that eats a real copy, and
/// losing a copy is worse than capturing a duplicate.
/// </para>
/// </remarks>
internal sealed class ClipboardSuppressionGate
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);

    private readonly long _windowMilliseconds;
    private readonly Func<long> _clock;

    private int _pending;
    private long _expiresAt;

    public ClipboardSuppressionGate(TimeSpan? window = null, Func<long>? clock = null)
    {
        _windowMilliseconds = (long)(window ?? DefaultWindow).TotalMilliseconds;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    /// <summary>Records that the application is about to write to the clipboard.</summary>
    public void Arm()
    {
        // The deadline is refreshed before the count rises so a notification arriving
        // concurrently can never observe a pending suppression against a stale deadline.
        Interlocked.Exchange(ref _expiresAt, _clock() + _windowMilliseconds);
        Interlocked.Increment(ref _pending);
    }

    /// <summary>
    /// Consumes any pending suppressions and reports whether this clipboard notification
    /// should be ignored. Always clears the pending state, expired or not.
    /// </summary>
    public bool ShouldSuppress()
    {
        var pending = Interlocked.Exchange(ref _pending, 0);
        if (pending <= 0)
        {
            return false;
        }

        return _clock() <= Interlocked.Read(ref _expiresAt);
    }
}
