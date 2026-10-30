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
    private int _pendingForAnyWriter;
    private long _expiresAt;

    // The last text the application wrote, hashed, and until when a
    // notification carrying it counts as our own. Guarded by _contentLock.
    private readonly object _contentLock = new();
    private string? _selfWriteHash;
    private long _selfWriteExpiresAt;

    public ClipboardSuppressionGate(TimeSpan? window = null, Func<long>? clock = null)
    {
        _windowMilliseconds = (long)(window ?? DefaultWindow).TotalMilliseconds;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    /// <summary>Records that the application is about to write to the clipboard.</summary>
    /// <param name="content">The text being written, when there is any.</param>
    public void Arm(string? content = null)
    {
        // The deadline is refreshed before the count rises so a notification arriving
        // concurrently can never observe a pending suppression against a stale deadline.
        var expiresAt = _clock() + _windowMilliseconds;
        Interlocked.Exchange(ref _expiresAt, expiresAt);
        if (!string.IsNullOrEmpty(content))
        {
            var hash = Hash(content);
            lock (_contentLock)
            {
                _selfWriteHash = hash;
                _selfWriteExpiresAt = expiresAt;
            }
        }

        Interlocked.Increment(ref _pending);
    }

    /// <summary>
    /// Records that the next clipboard change is to be skipped whichever application
    /// makes it - the copy-without-saving hotkey, whose target is a copy the user makes
    /// elsewhere.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Arm"/> because the monitor only honours an ordinary
    /// arm while Clipthrough owns the clipboard. Counted in the same batch and window.
    /// </remarks>
    public void ArmForAnyWriter()
    {
        Interlocked.Exchange(ref _expiresAt, _clock() + _windowMilliseconds);
        Interlocked.Increment(ref _pendingForAnyWriter);
    }

    /// <summary>
    /// Withdraws one pending suppression after a write that threw before reaching the
    /// clipboard.
    /// </summary>
    /// <remarks>
    /// The expiry window already bounds a leaked suppression, so this is not what makes
    /// the gate safe - it narrows the window from two seconds to nothing in the one case
    /// where the caller knows for certain that no write landed.
    ///
    /// The residual risk runs the other way: if the write threw *after* partially setting
    /// the clipboard, withdrawing means the application captures its own output as a new
    /// clip. That is the better failure. A spurious clip is visible and the user can
    /// delete it; a swallowed copy is invisible and gone, which is the same trade this
    /// class already makes when it drains as a batch.
    /// </remarks>
    public void Disarm()
    {
        // Floor at zero rather than decrementing blindly: a concurrent ShouldSuppress may
        // already have drained the count, and driving it negative would make the next
        // genuine arm invisible.
        var drained = Interlocked.Exchange(ref _pending, 0);
        if (drained > 1)
        {
            Interlocked.Add(ref _pending, drained - 1);
        }

        // Nothing was written, so nothing can be re-published.
        lock (_contentLock)
        {
            _selfWriteHash = null;
        }
    }

    /// <summary>
    /// Consumes any pending suppressions and reports whether this clipboard notification
    /// should be ignored. Always clears the pending state, expired or not.
    /// </summary>
    public bool ShouldSuppress() => ShouldSuppress(out _);

    /// <inheritdoc cref="ShouldSuppress()"/>
    /// <param name="coversAnyWriter">
    /// True when the suppression came from <see cref="ArmForAnyWriter"/>, so it applies
    /// whoever wrote. False when it only stands for Clipthrough's own writes.
    /// </param>
    public bool ShouldSuppress(out bool coversAnyWriter)
    {
        var forAnyWriter = Interlocked.Exchange(ref _pendingForAnyWriter, 0);
        var pending = Interlocked.Exchange(ref _pending, 0);
        coversAnyWriter = forAnyWriter > 0;
        if (pending + forAnyWriter <= 0)
        {
            return false;
        }

        return _clock() <= Interlocked.Read(ref _expiresAt);
    }

    /// <summary>
    /// True when <paramref name="content"/> is what the application itself wrote
    /// within the window, so a further notification carrying it is a re-publish
    /// of our own write rather than a copy the user made.
    /// </summary>
    /// <remarks>
    /// Matching on content rather than counting notifications is what survives
    /// a clipboard redirector. Over Remote Desktop, rdpclip re-publishes every
    /// write and raises a second notification well after the monitor's
    /// coalescing; one Arm swallowed only the first, so every paste was
    /// captured again (F-52). Content that differs is never suppressed, even
    /// inside the window - a real copy the user makes is not lost to this.
    /// </remarks>
    public bool IsRecentSelfWrite(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        string? hash;
        long expiresAt;
        lock (_contentLock)
        {
            hash = _selfWriteHash;
            expiresAt = _selfWriteExpiresAt;
        }

        return hash is not null
            && _clock() <= expiresAt
            && string.Equals(hash, Hash(content), StringComparison.Ordinal);
    }

    // Line endings are normalised because a redirector may re-publish text
    // with CRLF where we wrote LF. A hash rather than the text itself, so the
    // gate does not hold a second copy of a possibly sensitive clip.
    private static string Hash(string content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal))));
}
