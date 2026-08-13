using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace Clipthrough.Presentation;

/// <summary>
/// Rate limiting for background-progress streams.
///
/// Exists because Rx's <c>Throttle</c> is a debounce, not a rate limiter: it
/// emits only after the source has been quiet for the whole window, so a source
/// that keeps firing emits nothing at all. That is the wrong shape for progress.
/// A backlog draining batch after batch fires continuously, and a debounce means
/// the figure on screen stays frozen for the entire drain and only moves once the
/// work is already finished - the one stretch where progress was worth showing.
/// </summary>
internal static class ProgressStreams
{
    /// <summary>
    /// Emits the most recent value at most once per <paramref name="interval"/>,
    /// and emits nothing while the source is idle.
    /// </summary>
    public static IObservable<T> RateLimit<T>(this IObservable<T> source, TimeSpan interval, IScheduler scheduler)
        => source.Sample(interval, scheduler);
}
