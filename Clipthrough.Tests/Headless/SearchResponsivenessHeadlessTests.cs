using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Search responsiveness (O10). The list re-queries a fixed time after the last
/// keystroke, and the busy bar only appears for work slow enough to need it: a
/// bar that blinks on for every 30 ms refresh is a flicker, not feedback.
/// </summary>
public sealed class SearchResponsivenessHeadlessTests
{
    private static async Task<MainWindowTestHarness> ReadyAsync()
    {
        var harness = MainWindowTestHarness.Create();
        foreach (var text in new[] { "alpha invoice", "beta report", "gamma invoice", "delta notes" })
        {
            await harness.ClipStore.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
                SourceApp = "Tests",
            });
        }

        harness.Settings.SetHasSavedSettings(true);
        harness.StorageOptions.SetHasSavedConfig(true);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetMainWindowVisible(true);
        await PumpUntilAsync(() => !harness.ViewModel.IsLoadingDatabase && harness.ViewModel.Clips.Count == 4);
        await PumpAsync(TimeSpan.FromMilliseconds(400));
        Assert.Equal(4, harness.ViewModel.Clips.Count);
        return harness;
    }

    private static async Task PumpAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheSearchThrottleIs150Ms()
        => Assert.Equal(TimeSpan.FromMilliseconds(150), MainWindowViewModel.SearchThrottle);

    /// <summary>
    /// Results land well before the old 300 ms throttle would even have started
    /// the query - and not before the throttle, which is what stops every
    /// keystroke from re-querying the library.
    /// </summary>
    [AvaloniaFact]
    public async Task ResultsLandAfterTheThrottle_AndBeforeTheOldOne()
    {
        using var harness = await ReadyAsync();
        var started = DateTime.UtcNow;
        harness.ViewModel.SearchText = "invoice";

        await PumpAsync(TimeSpan.FromMilliseconds(90));
        Assert.Equal(4, harness.ViewModel.Clips.Count);

        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 2, timeoutMs: 2000);
        var landed = DateTime.UtcNow - started;

        Assert.Equal(2, harness.ViewModel.Clips.Count);
        Assert.True(landed < TimeSpan.FromMilliseconds(290), $"results landed after {landed.TotalMilliseconds} ms, as late as the old 300 ms throttle");
    }

    /// <summary>
    /// A refresh that finishes inside the delay never shows the bar, at any
    /// moment - asserted over every change, because the bug is a blink.
    /// </summary>
    [AvaloniaFact]
    public async Task AFastRefresh_NeverShowsTheBusyBar()
    {
        using var harness = await ReadyAsync();
        var shown = new List<bool>();
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ShowBusyIndicator))
            {
                shown.Add(harness.ViewModel.ShowBusyIndicator);
            }
        };
        var bar = harness.Window.GetVisualDescendants().OfType<ProgressBar>().Single(p => p.Classes.Contains("busy-indicator"));
        var barShown = false;
        bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && bar.IsVisible)
            {
                barShown = true;
            }
        };
        var sawBusy = false;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsBusyOrCapturing) && harness.ViewModel.IsBusyOrCapturing)
            {
                sawBusy = true;
            }
        };

        harness.ViewModel.SearchText = "report";
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 1);
        await PumpAsync(TimeSpan.FromMilliseconds(400));

        Assert.True(sawBusy, "the refresh never went busy, so the bar was never at risk");
        Assert.DoesNotContain(true, shown);
        Assert.False(barShown, "the bar on screen blinked on for a quick refresh");
        Assert.False(harness.ViewModel.ShowBusyIndicator);
    }

    [AvaloniaFact]
    public async Task SlowWork_ShowsTheBusyBar_AndItGoesWhenTheWorkDoes()
    {
        using var harness = await ReadyAsync();

        harness.ClipboardMonitor.SetCaptureBusy(true);
        await PumpUntilAsync(() => harness.ViewModel.ShowBusyIndicator, timeoutMs: 3000);
        Assert.True(harness.ViewModel.ShowBusyIndicator);

        harness.ClipboardMonitor.SetCaptureBusy(false);
        await PumpUntilAsync(() => !harness.ViewModel.ShowBusyIndicator, timeoutMs: 3000);
        Assert.False(harness.ViewModel.ShowBusyIndicator);
    }
}