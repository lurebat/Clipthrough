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
/// activation, and only pasting into real ones can answer that -
/// tools/paste-timing/Invoke-PasteSweep.ps1 does, at several margins at once.
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

    /// <summary>
    /// Margin between the target holding the foreground and Ctrl+V.
    /// <see cref="SettleOverrideVariable"/> overrides it, for re-measuring
    /// (tools/paste-timing).
    /// </summary>
    public static readonly TimeSpan SettleAfterForeground =
        ResolveSettle(Environment.GetEnvironmentVariable(SettleOverrideVariable));

    /// <summary>Diagnostic override of the settle margin, in milliseconds (0-150).</summary>
    public const string SettleOverrideVariable = "CLIPTHROUGH_PASTE_SETTLE_MS";

    // A const, not a static field: SettleAfterForeground is initialized first.
    private const int DefaultSettleMs = 30;

    /// <summary>
    /// The settle margin for an override value: the default when it is unset
    /// or not a whole number, and never outside 0-150 ms (the larger budget).
    /// </summary>
    internal static TimeSpan ResolveSettle(string? overrideValue)
        => int.TryParse(overrideValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(Math.Min(ms, 150))
            : TimeSpan.FromMilliseconds(DefaultSettleMs);

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
