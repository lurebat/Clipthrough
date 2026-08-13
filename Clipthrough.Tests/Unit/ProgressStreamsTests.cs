using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Clipthrough.Presentation;
using Microsoft.Reactive.Testing;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Background progress has to keep moving while the work is still running.
///
/// These streams used to be coalesced with Rx's <c>Throttle</c>, which is a
/// debounce: it emits only once the source has been quiet for a whole window.
/// A draining embedding backlog fires a batch every few hundred milliseconds
/// for as long as it takes, so a debounce reported nothing at all until the
/// drain was over — the figure on screen sat frozen for the entire period it
/// was supposed to be describing, then jumped to the finished value.
/// </summary>
public sealed class ProgressStreamsTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void RateLimit_KeepsReportingWhileTheSourceNeverGoesQuiet()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.RateLimit(Window, scheduler).Subscribe(seen.Add);

        // A batch every 200ms for three seconds: never a 500ms gap, which is
        // exactly what a debounce waits for and never gets.
        for (var i = 1; i <= 15; i++)
        {
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200).Ticks);
            source.OnNext(i);
        }

        scheduler.AdvanceBy(Window.Ticks);

        Assert.True(seen.Count >= 5, $"a 3s run of updates reported {seen.Count} times");
        Assert.Equal(15, seen[^1]);
    }

    [Fact]
    public void RateLimit_DoesNotReportFasterThanItsWindow()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.RateLimit(Window, scheduler).Subscribe(seen.Add);

        for (var i = 1; i <= 100; i++)
        {
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(10).Ticks);
            source.OnNext(i);
        }

        // One second of source activity may not produce more than two reports.
        Assert.True(seen.Count <= 2, $"a 1s run of updates reported {seen.Count} times");
    }

    [Fact]
    public void RateLimit_ReportsAnIsolatedUpdate()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.RateLimit(Window, scheduler).Subscribe(seen.Add);

        source.OnNext(7);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);

        Assert.Equal([7], seen);
    }

    [Fact]
    public void RateLimit_StaysSilentWhileTheSourceIsIdle()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var subscription = source.RateLimit(Window, scheduler).Subscribe(seen.Add);

        scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

        Assert.Empty(seen);
    }
}
