using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The copy-and-mark hotkeys used to sleep a flat 150 ms and then act on
/// whatever clip happened to be newest. These pin the replacement, which waits
/// on what the monitor actually reports.
/// </summary>
public sealed class RecentCaptureResolverTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The fast path: the capture lands while we are still inside the first
    /// grace slice, so the resolver hands back that exact clip and never has to
    /// guess from the store.
    /// </summary>
    [Fact]
    public async Task CaptureDuringGrace_ReturnsTheCapturedClip()
    {
        var monitor = new TestClipboardMonitorService();
        var delay = new ManualDelay();
        var captured = new ClipEntry { Id = 7 };
        var newestReads = 0;

        var resolve = RecentCaptureResolver.ResolveJustCopiedClipAsync(
            monitor,
            () => { newestReads++; return Task.FromResult<ClipEntry?>(new ClipEntry { Id = 99 }); },
            Grace,
            TimeSpan.FromSeconds(1),
            delay.Delay);

        await delay.WaitForSliceAsync();
        monitor.Emit(captured);

        var result = await resolve;

        Assert.Same(captured, result);
        Assert.Equal(0, newestReads);
    }

    /// <summary>
    /// The common case: the human pressed the hotkey well after the copy was
    /// already captured, so nothing is in flight and one grace slice is all the
    /// resolver spends before reading the newest stored clip.
    /// </summary>
    [Fact]
    public async Task NoCaptureInFlight_FallsBackAfterASingleSlice()
    {
        var monitor = new TestClipboardMonitorService();
        var delay = new ManualDelay();
        var newest = new ClipEntry { Id = 3 };

        var resolve = RecentCaptureResolver.ResolveJustCopiedClipAsync(
            monitor,
            () => Task.FromResult<ClipEntry?>(newest),
            Grace,
            TimeSpan.FromSeconds(1),
            delay.Delay);

        await delay.WaitForSliceAsync();
        delay.CompleteSlice();

        Assert.Same(newest, await resolve);
        Assert.Equal(1, delay.Calls);
    }

    /// <summary>
    /// The regression the whole class exists for. A slow capture - a large
    /// image, a busy SQLite writer - lands after the old fixed 150 ms deadline
    /// had already expired. Marking the previous clip sensitive while leaving
    /// the secret unmarked is the failure that matters, so the resolver must
    /// keep waiting while the monitor says a capture is still in flight.
    /// </summary>
    [Fact]
    public async Task CaptureLandingAfterTheFirstSlice_StillWins()
    {
        var monitor = new TestClipboardMonitorService();
        var delay = new ManualDelay();
        var slowCapture = new ClipEntry { Id = 42 };
        var newestReads = 0;

        monitor.SetCaptureBusy(true);

        var resolve = RecentCaptureResolver.ResolveJustCopiedClipAsync(
            monitor,
            () => { newestReads++; return Task.FromResult<ClipEntry?>(new ClipEntry { Id = 41 }); },
            Grace,
            TimeSpan.FromSeconds(1),
            delay.Delay);

        await delay.WaitForSliceAsync();
        delay.CompleteSlice();

        // Second slice: the capture finally arrives, well past the old deadline.
        await delay.WaitForSliceAsync();
        monitor.Emit(slowCapture);

        var result = await resolve;

        Assert.Same(slowCapture, result);
        Assert.Equal(0, newestReads);
    }

    /// <summary>
    /// A capture that is suppressed, deduplicated or dropped by an exclusion
    /// rule never emits. Once the monitor stops reporting itself busy no clip
    /// is coming, so waiting longer buys nothing and the resolver falls back.
    /// </summary>
    [Fact]
    public async Task BusyCaptureThatNeverArrives_FallsBackOnceBusyClears()
    {
        var monitor = new TestClipboardMonitorService();
        var delay = new ManualDelay();
        var newest = new ClipEntry { Id = 5 };

        monitor.SetCaptureBusy(true);

        var resolve = RecentCaptureResolver.ResolveJustCopiedClipAsync(
            monitor,
            () => Task.FromResult<ClipEntry?>(newest),
            Grace,
            TimeSpan.FromSeconds(1),
            delay.Delay);

        await delay.WaitForSliceAsync();
        delay.CompleteSlice();

        await delay.WaitForSliceAsync();
        monitor.SetCaptureBusy(false);
        delay.CompleteSlice();

        Assert.Same(newest, await resolve);
        Assert.Equal(2, delay.Calls);
    }

    /// <summary>
    /// A wedged capture that stays busy forever must not hang the hotkey. The
    /// wait is bounded, after which the resolver takes the newest stored clip.
    /// </summary>
    [Fact]
    public async Task CaptureWedgedBusy_IsBoundedByMaxWait()
    {
        var monitor = new TestClipboardMonitorService();

        // Every slice completes on its own here. A resolver that lost its bound
        // would keep spinning slices instead of deadlocking the test, so the
        // slice count stays a meaningful assertion either way.
        var delay = new ManualDelay { AutoComplete = true };
        var newest = new ClipEntry { Id = 8 };

        monitor.SetCaptureBusy(true);

        var resolve = RecentCaptureResolver.ResolveJustCopiedClipAsync(
            monitor,
            () => Task.FromResult<ClipEntry?>(newest),
            Grace,
            TimeSpan.FromMilliseconds(Grace.TotalMilliseconds * 3),
            delay.Delay);

        Assert.Same(newest, await resolve);
        Assert.Equal(3, delay.Calls);
    }

    /// <summary>
    /// Hands out a delay the test completes by hand, so the resolver's slice
    /// boundaries are observable instead of a race against the wall clock.
    /// </summary>
    private sealed class ManualDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();
        private readonly SemaphoreSlim _entered = new(0);
        private int _calls;

        /// <summary>Completes each slice as soon as it is handed out.</summary>
        public bool AutoComplete { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public Task Delay(TimeSpan _)
        {
            Interlocked.Increment(ref _calls);
            if (AutoComplete)
            {
                return Task.CompletedTask;
            }

            var slice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(slice);
            _entered.Release();
            return slice.Task;
        }

        /// <summary>Blocks until the resolver has entered its next wait.</summary>
        public async Task WaitForSliceAsync()
            => Assert.True(await _entered.WaitAsync(TimeSpan.FromSeconds(5)), "resolver never started a delay slice");

        public void CompleteSlice()
        {
            Assert.True(_pending.TryDequeue(out var slice), "no delay slice was pending");
            slice.SetResult();
        }
    }
}
