using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Toast entrance (O5): a new toast rises 16 px and fades in over 167 ms under
/// the motion policy, and simply appears without it.
/// </summary>
public sealed class ToastMotionHeadlessTests
{
    private static MainWindowTestHarness Create(bool motion)
    {
        var system = new TestSystemInteractionService { OsAnimationsEnabled = true };
        return MainWindowTestHarness.Create(
            configureSettings: s => s with { Motion = motion ? MotionPreference.On : MotionPreference.Off },
            motionFactory: settings => new MotionService(settings, system));
    }

    private static Border PublishToast(MainWindowTestHarness harness)
    {
        harness.Notifications.PublishInfo("Copied to clipboard", "The selected clip is on the clipboard.");
        Dispatcher.UIThread.RunJobs();
        return harness.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toast"));
    }

    private static double OffsetY(Visual visual) => visual.RenderTransform?.Value.M32 ?? 0;

    /// <summary>
    /// Sampled from the first frame, because the headless animation clock runs
    /// on wall time and how far it gets in a given number of ticks varies with
    /// load. What is pinned: it starts out of place and faint, and every later
    /// frame is closer to in place and opaque. The end state is the 100%
    /// keyframe, held by FillMode=Forward.
    /// </summary>
    [AvaloniaFact]
    public void WithMotion_ANewToastRisesAndFadesIn()
    {
        using var harness = Create(motion: true);

        var toast = PublishToast(harness);
        var opacity = new System.Collections.Generic.List<double> { toast.Opacity };
        var offset = new System.Collections.Generic.List<double> { OffsetY(toast) };
        for (var i = 0; i < 12; i++)
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            opacity.Add(toast.Opacity);
            offset.Add(OffsetY(toast));
        }

        Assert.True(opacity.Min() < 0.6, $"never faint: {string.Join(", ", opacity)}");
        Assert.True(offset.Max() > 6, $"never below its place: {string.Join(", ", offset)}");
        Assert.True(opacity[^1] > opacity.Min(), $"did not fade in: {string.Join(", ", opacity)}");
        Assert.True(offset[^1] < offset.Max(), $"did not rise: {string.Join(", ", offset)}");
        for (var i = 1; i < opacity.Count; i++)
        {
            Assert.True(opacity[i] >= opacity[i - 1] - 1e-9, $"faded back out: {string.Join(", ", opacity)}");
        }
    }
    [AvaloniaFact]
    public void WithoutMotion_ANewToastSimplyAppears()
    {
        using var harness = Create(motion: false);

        var toast = PublishToast(harness);

        Assert.Equal(1, toast.Opacity);
        Assert.Equal(0, OffsetY(toast));
    }
}