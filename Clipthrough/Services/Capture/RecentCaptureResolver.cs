using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Answers "which clip did the user just copy?" for the copy-and-mark hotkeys.
/// </summary>
/// <remarks>
/// Those hotkeys are pressed right after Ctrl+C and then act on the newest
/// stored clip, so they depend on the capture having landed first. Sleeping a
/// fixed 150 ms and hoping is a race: a large image, a slow disk or a busy
/// SQLite writer pushes the capture past the deadline, and the hotkey then
/// favourites - or marks sensitive - the *previous* clip. Mislabelling an
/// innocent clip as sensitive while leaving the secret unmarked is the worst
/// version of that.
///
/// So instead of guessing, this waits on what the monitor actually reports:
/// the captured clip itself when one arrives, and <see
/// cref="IClipboardMonitorService.CaptureBusy"/> to know whether one is still
/// coming. When no capture is in flight it falls back to the newest stored clip
/// after a single grace slice, which is the common case (a human presses the
/// hotkey well after the copy has been captured) and costs no more than before.
/// </remarks>
public static class RecentCaptureResolver
{
    /// <summary>How long to wait for a capture before checking whether one is in flight.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMilliseconds(150);

    /// <summary>Upper bound on the wait, so a wedged capture cannot hang the hotkey forever.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(5);

    public static Task<ClipEntry?> ResolveJustCopiedClipAsync(
        IClipboardMonitorService monitor,
        Func<Task<ClipEntry?>> readNewestClip)
        => ResolveJustCopiedClipAsync(monitor, readNewestClip, DefaultGrace, DefaultMaxWait, static d => Task.Delay(d));

    internal static async Task<ClipEntry?> ResolveJustCopiedClipAsync(
        IClipboardMonitorService monitor,
        Func<Task<ClipEntry?>> readNewestClip,
        TimeSpan grace,
        TimeSpan maxWait,
        Func<TimeSpan, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(readNewestClip);
        ArgumentNullException.ThrowIfNull(delay);

        var captured = new TaskCompletionSource<ClipEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = 0;

        // Subscribe before waiting so a capture that lands immediately is not missed.
        // An error on either stream means no further news is coming, which the loop
        // already handles by falling back to the newest stored clip.
        using var capturedSubscription = monitor.CapturedClips.Subscribe(
            clip => captured.TrySetResult(clip),
            _ => { });
        using var busySubscription = monitor.CaptureBusy.Subscribe(
            isBusy => Interlocked.Exchange(ref busy, isBusy ? 1 : 0),
            _ => Interlocked.Exchange(ref busy, 0));

        var waited = TimeSpan.Zero;
        while (true)
        {
            var completed = await Task.WhenAny(captured.Task, delay(grace)).ConfigureAwait(false);
            if (ReferenceEquals(completed, captured.Task))
            {
                return await captured.Task.ConfigureAwait(false);
            }

            waited += grace;

            // Nothing in flight means the capture either already finished - so the
            // newest stored clip is the right answer - or was suppressed, rejected
            // as a duplicate, or dropped by an exclusion rule, in which case no
            // capture is ever coming and waiting longer buys nothing.
            if (Volatile.Read(ref busy) == 0 || waited >= maxWait)
            {
                break;
            }
        }

        return await readNewestClip().ConfigureAwait(false);
    }
}
