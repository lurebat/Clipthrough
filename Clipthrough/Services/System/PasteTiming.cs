using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

/// <summary>
/// How long a synthesized paste waits for its target, and after it.
/// </summary>
/// <remarks>
/// Every paste used to sleep a fixed 150 ms (paste-and-hide) or 120 ms (the
/// hotkeys) whether the target needed it or not. Now it waits only until the
/// target actually holds the foreground - the thing those sleeps stood in for -
/// and then <see cref="SettleAfterForeground"/>, all within the old sleep as a
/// budget, so a target that never comes forward is pasted into exactly as late
/// as before and never later. The settle margin is the one number here no test
/// can choose: it is about how soon a foreign window acts on input after
/// activation, and only pasting into real ones can answer that.
/// </remarks>
public static class PasteTiming
{
    /// <summary>
    /// The whole budget of a paste that has just handed the foreground back
    /// (paste-and-hide, local custom hotkeys): the fixed sleep it replaces.
    /// </summary>
    public static readonly TimeSpan AfterHideBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The whole budget of a paste fired by a global hotkey, where the target
    /// already has the foreground: the fixed sleep it replaces.
    /// </summary>
    public static readonly TimeSpan HotkeyBudget = TimeSpan.FromMilliseconds(120);

    /// <summary>Margin between the target holding the foreground and Ctrl+V.</summary>
    public static readonly TimeSpan SettleAfterForeground = TimeSpan.FromMilliseconds(30);

    /// <summary>How often the foreground is sampled while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Waits until <paramref name="isReady"/> holds or <paramref name="timeout"/>
    /// passes. Returns whether it held. Checks once before waiting at all, so a
    /// target that is already forward costs nothing.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> isReady,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isReady);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (isReady())
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The whole wait a paste does before sending Ctrl+V: until the target is
    /// forward, then the settle margin - never longer in total than
    /// <paramref name="budget"/>, the fixed sleep this replaced. A target that
    /// never reports as forward is pasted into when the budget runs out, as
    /// it was before.
    /// </summary>
    public static async Task BeforePasteAsync(ISystemInteractionService systemInteraction, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemInteraction);
        var started = Stopwatch.GetTimestamp();
        await systemInteraction.WaitForPasteTargetAsync(budget, cancellationToken).ConfigureAwait(true);
        var settle = RemainingSettle(Stopwatch.GetElapsedTime(started), budget);
        if (settle > TimeSpan.Zero)
        {
            await Task.Delay(settle, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The settle margin still owed after <paramref name="waited"/>: the full
    /// margin, clipped so the paste never runs past <paramref name="budget"/>.
    /// </summary>
    public static TimeSpan RemainingSettle(TimeSpan waited, TimeSpan budget)
    {
        var left = budget - waited;
        if (left <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return left < SettleAfterForeground ? left : SettleAfterForeground;
    }
}
