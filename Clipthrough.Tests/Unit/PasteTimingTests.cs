using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The wait before a synthesized Ctrl+V. It replaced fixed sleeps of 150 and
/// 120 ms, and the one promise it must keep is that it is never slower than
/// they were: a paste whose target never reports as forward still goes out when
/// the old sleep would have ended, not after it.
/// </summary>
public sealed class PasteTimingTests
{
    [Fact]
    public async Task WaitUntil_ReturnsAsSoonAsTheTargetIsForward()
    {
        var calls = 0;
        var started = Stopwatch.GetTimestamp();

        var ready = await PasteTiming.WaitUntilAsync(() => ++calls >= 3, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5));

        Assert.True(ready);
        Assert.Equal(3, calls);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1), "the wait ran on past a target that was already forward");
    }

    [Fact]
    public async Task WaitUntil_AlreadyForward_DoesNotWaitAtAll()
    {
        var started = Stopwatch.GetTimestamp();

        Assert.True(await PasteTiming.WaitUntilAsync(() => true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500), "a target already forward cost a poll interval");
    }

    [Fact]
    public async Task WaitUntil_GivesUpAtTheTimeout()
    {
        var started = Stopwatch.GetTimestamp();

        var ready = await PasteTiming.WaitUntilAsync(() => false, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5));

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.False(ready);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(80), $"gave up early, after {elapsed.TotalMilliseconds} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"never gave up: {elapsed.TotalMilliseconds} ms");
    }

    [Theory]
    [InlineData(0, 150, 30)]     // forward at once: the whole settle margin
    [InlineData(100, 150, 30)]   // forward late: still the whole margin, it fits
    [InlineData(135, 150, 15)]   // only 15 ms of budget left: clipped to it
    [InlineData(150, 150, 0)]    // never forward: the budget is spent, paste now
    [InlineData(400, 150, 0)]    // overran (a slow poll): never negative
    public void RemainingSettle_NeverRunsPastTheBudget(int waitedMs, int budgetMs, int expectedMs)
    {
        Assert.Equal(30, (int)PasteTiming.SettleAfterForeground.TotalMilliseconds);

        var settle = PasteTiming.RemainingSettle(TimeSpan.FromMilliseconds(waitedMs), TimeSpan.FromMilliseconds(budgetMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), settle);
    }

    /// <summary>
    /// The budget is the old fixed sleep. A target that never comes forward must
    /// be pasted into when that sleep would have ended - not a settle margin later.
    /// </summary>
    [Fact]
    public async Task BeforePaste_TargetNeverForward_TakesTheOldSleepAndNoMore()
    {
        var system = new ScriptedForeground(forwardAfter: null);
        var started = Stopwatch.GetTimestamp();

        await PasteTiming.BeforePasteAsync(system, PasteTiming.HotkeyBudget);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Equal(PasteTiming.HotkeyBudget, system.Timeout);
        Assert.True(elapsed >= PasteTiming.HotkeyBudget, $"pasted before the old sleep ended: {elapsed.TotalMilliseconds} ms");
        // Without the clip the margin would come on top of the whole budget.
        Assert.True(elapsed < PasteTiming.HotkeyBudget + PasteTiming.SettleAfterForeground,
            $"took longer than the sleep it replaced: {elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task BeforePaste_TargetForwardAtOnce_IsMuchShorterThanTheOldSleep()
    {
        var system = new ScriptedForeground(forwardAfter: TimeSpan.Zero);
        var started = Stopwatch.GetTimestamp();

        await PasteTiming.BeforePasteAsync(system, PasteTiming.AfterHideBudget);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed >= PasteTiming.SettleAfterForeground - TimeSpan.FromMilliseconds(2), $"skipped the settle margin: {elapsed.TotalMilliseconds} ms");
        Assert.True(elapsed < PasteTiming.AfterHideBudget - TimeSpan.FromMilliseconds(50), $"still waited out the old sleep: {elapsed.TotalMilliseconds} ms");
    }

    /// <summary>A target that comes forward after a set time, or never.</summary>
    private sealed class ScriptedForeground(TimeSpan? forwardAfter) : ISystemInteractionService
    {
        public TimeSpan Timeout { get; private set; }

        public async Task<bool> WaitForPasteTargetAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Timeout = timeout;
            var started = Stopwatch.GetTimestamp();
            return await PasteTiming.WaitUntilAsync(
                () => forwardAfter is { } after && Stopwatch.GetElapsedTime(started) >= after,
                timeout,
                PasteTiming.PollInterval,
                cancellationToken);
        }

        public Task CopyTextAsync(string text) => throw new NotSupportedException();
        public Task CopyRichContentAsync(string richContent, string plainText, Models.ClipContentFormat contentFormat) => throw new NotSupportedException();
        public Task CopyBitmapAsync(Avalonia.Media.Imaging.Bitmap bitmap) => throw new NotSupportedException();
        public Task OpenPathAsync(string path) => throw new NotSupportedException();
        public Task OpenUrlAsync(string url) => throw new NotSupportedException();
        public Task OpenContainingDirectoryAsync(string path) => throw new NotSupportedException();
        public Task OpenInEditorAsync(string filePath, string editorPath) => throw new NotSupportedException();
        public Task OpenInDiffToolAsync(string leftPath, string rightPath, string diffToolPath) => throw new NotSupportedException();
        public void CaptureTargetWindowForPaste() => throw new NotSupportedException();
        public void ClearTargetWindowCapture() => throw new NotSupportedException();
        public void RestoreCapturedForeground() => throw new NotSupportedException();
        public bool TryCloakWindow(Avalonia.Controls.Window window, bool releaseForeground) => throw new NotSupportedException();
        public void UncloakWindow(Avalonia.Controls.Window window) => throw new NotSupportedException();
        public bool AreOsAnimationsEnabled() => throw new NotSupportedException();
        public IDisposable WatchOsAnimationSetting(Avalonia.Controls.Window window, Action onChanged) => throw new NotSupportedException();
        public void SimulatePasteKeystroke() => throw new NotSupportedException();
        public void ShowNotification(Models.AppNotification notification) => throw new NotSupportedException();
        public bool TryRegisterGlobalHotKey(Avalonia.Controls.Window window, Models.HotkeyGesture hotkey, Action callback) => throw new NotSupportedException();
        public bool TryRegisterGlobalHotKey(Avalonia.Controls.Window window, string name, Models.HotkeyGesture hotkey, Action callback) => throw new NotSupportedException();
        public void UnregisterGlobalHotKey() => throw new NotSupportedException();
        public void UnregisterGlobalHotKey(string name) => throw new NotSupportedException();
        public void UnregisterAllGlobalHotKeys() => throw new NotSupportedException();
        public Avalonia.PixelPoint? GetCaretScreenPosition() => throw new NotSupportedException();
        public bool IsTargetWindowElevated() => throw new NotSupportedException();
        public void SyncStartWithWindows(bool enabled) => throw new NotSupportedException();
        public void ReconcileStartWithWindows(bool enabled) => throw new NotSupportedException();
        public void Dispose() { }
    }
}