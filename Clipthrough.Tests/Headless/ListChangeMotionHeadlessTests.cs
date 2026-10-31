using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// List insert and remove (O4). A single clip arriving (capture, undo) or
/// leaving (delete) slides its neighbours into place over 160 ms and fades the
/// new row in above them. Anything else - a search, a page load, a refresh -
/// must not animate: those replace many rows at once, and a list that animates
/// them reads as noise, not feedback. The slide is a composition implicit
/// animation armed on the realized rows for one change and then disarmed, so
/// these check what is armed when.
/// </summary>
public sealed class ListChangeMotionHeadlessTests
{
    private static async Task<MainWindowTestHarness> ReadyAsync(bool motion)
    {
        var system = new TestSystemInteractionService { OsAnimationsEnabled = true };
        var harness = MainWindowTestHarness.Create(
            configureSettings: s => s with { Motion = motion ? MotionPreference.On : MotionPreference.Off },
            motionFactory: settings => new MotionService(settings, system));
        for (var i = 0; i < 12; i++)
        {
            await CaptureAsync(harness, i == 5 ? "needle twelve" : $"clip number {i}");
        }

        harness.Settings.SetHasSavedSettings(true);
        harness.StorageOptions.SetHasSavedConfig(true);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetMainWindowVisible(true);
        await PumpUntilAsync(() => !harness.ViewModel.IsLoadingDatabase && harness.ViewModel.Clips.Count == 12);
        await PumpUntilAsync(() => false, 400);
        Assert.Equal(12, harness.ViewModel.Clips.Count);
        return harness;
    }

    private static async Task<ClipEntry> CaptureAsync(MainWindowTestHarness harness, string text)
        => (await harness.ClipStore.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            SourceApp = "Tests",
        }))!;

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int ArmedRows(MainWindowTestHarness harness)
        => harness.ClipList.GetRealizedContainers()
            .Count(c => ElementComposition.GetElementVisual(c)?.ImplicitAnimations is not null);

    /// <summary>
    /// What was armed and lifted as each change landed. Read at the change,
    /// not afterwards: on a loaded machine the 300 ms disarm can run before a
    /// test gets to look, which would fail the positive tests and let the
    /// negative ones pass without having seen anything.
    /// </summary>
    private sealed class ArmingProbe
    {
        public int MaxArmed { get; private set; }

        public System.Collections.Generic.HashSet<Control> Lifted { get; } = [];

        public ArmingProbe(MainWindowTestHarness harness)
        {
            harness.ViewModel.Clips.CollectionChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => MaxArmed = Math.Max(MaxArmed, ArmedRows(harness)), DispatcherPriority.Send);
            foreach (var row in harness.ClipList.GetRealizedContainers()) Watch(row);
            harness.ClipList.ContainerPrepared += (_, e) => Watch(e.Container);
        }

        private void Watch(Control row) => row.PropertyChanged += (_, e) =>
        {
            if (e.Property == Panel.ZIndexProperty && row.ZIndex > 0) Lifted.Add(row);
        };
    }

    [AvaloniaFact]
    public async Task ACapture_SlidesTheRowsDown_AndLiftsTheNewRowAboveThem()
    {
        using var harness = await ReadyAsync(motion: true);
        var probe = new ArmingProbe(harness);

        var copied = await CaptureAsync(harness, "freshly copied");
        harness.ClipboardMonitor.Emit(copied);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("freshly copied", harness.ViewModel.Clips[0].Clip.Content);
        Assert.True(probe.MaxArmed > 1, "the neighbours were not armed to slide");
        var newRow = Assert.IsType<ListBoxItem>(harness.ClipList.ContainerFromIndex(0));
        Assert.Equal([newRow], probe.Lifted);

        await PumpUntilAsync(() => false, 500);
        Assert.Equal(0, ArmedRows(harness));
        Assert.Equal(0, newRow.ZIndex);
    }

    [AvaloniaFact]
    public async Task ADelete_SlidesTheRowsBelowUp()
    {
        using var harness = await ReadyAsync(motion: true);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[2];
        var probe = new ArmingProbe(harness);

        harness.ViewModel.DeleteSelectedCommand.Execute().Subscribe(_ => { }, _ => { });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(11, harness.ViewModel.Clips.Count);
        Assert.True(probe.MaxArmed > 1);
    }

    [AvaloniaFact]
    public async Task UndoingASingleDelete_AnimatesTheRowBackIn()
    {
        using var harness = await ReadyAsync(motion: true);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[2];
        harness.ViewModel.DeleteSelectedCommand.Execute().Subscribe(_ => { }, _ => { });
        await PumpUntilAsync(() => harness.ViewModel.Notifications.SelectMany(n => n.Actions).Any());
        await PumpUntilAsync(() => false, 500);
        Assert.Equal(0, ArmedRows(harness));

        var probe = new ArmingProbe(harness);
        harness.ViewModel.Notifications.SelectMany(n => n.Actions).Last().ExecuteCommand.Execute().Subscribe(_ => { }, _ => { });
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 12);
        Dispatcher.UIThread.RunJobs();

        Assert.True(probe.MaxArmed > 1, "the restored row popped back in");
    }

    [AvaloniaFact]
    public async Task ASearch_ReplacesTheRowsWithoutAnimating()
    {
        using var harness = await ReadyAsync(motion: true);
        var probe = new ArmingProbe(harness);

        harness.ViewModel.SearchText = "needle";
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 1);
        harness.ViewModel.SearchText = string.Empty;
        await PumpUntilAsync(() => harness.ViewModel.Clips.Count == 12);

        Assert.Equal(0, probe.MaxArmed);
        Assert.Empty(probe.Lifted);
    }

    [AvaloniaFact]
    public async Task WithoutMotion_ACaptureDoesNotAnimate()
    {
        using var harness = await ReadyAsync(motion: false);
        var probe = new ArmingProbe(harness);

        harness.ClipboardMonitor.Emit(await CaptureAsync(harness, "freshly copied"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("freshly copied", harness.ViewModel.Clips[0].Clip.Content);
        Assert.Equal(0, probe.MaxArmed);
        Assert.Empty(probe.Lifted);
    }

    /// <summary>A capture while the popup is dismissed is already in place on the summon.</summary>
    [AvaloniaFact]
    public async Task InStandby_ACaptureDoesNotAnimate()
    {
        using var harness = await ReadyAsync(motion: true);
        harness.Window.Dismiss();
        var probe = new ArmingProbe(harness);

        harness.ClipboardMonitor.Emit(await CaptureAsync(harness, "freshly copied"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("freshly copied", harness.ViewModel.Clips[0].Clip.Content);
        Assert.Equal(0, probe.MaxArmed);
        Assert.Empty(probe.Lifted);
    }

    /// <summary>
    /// Two clips arriving together: both are lifted while they fade, and both
    /// come back down - a row left raised draws over its neighbours for good,
    /// and keeps doing so after it is recycled for another row.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoQuickCaptures_LeaveNoRowRaised()
    {
        using var harness = await ReadyAsync(motion: true);
        var probe = new ArmingProbe(harness);
        var first = await CaptureAsync(harness, "first quick copy");
        var second = await CaptureAsync(harness, "second quick copy");

        // Back to back, so the second arrives while the first is still lifted.
        harness.ClipboardMonitor.Emit(first);
        Dispatcher.UIThread.RunJobs();
        harness.ClipboardMonitor.Emit(second);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("second quick copy", harness.ViewModel.Clips[0].Clip.Content);
        Assert.True(probe.Lifted.Count >= 2, $"only {probe.Lifted.Count} row(s) were lifted");

        await PumpUntilAsync(() => false, 500);

        Assert.All(probe.Lifted, row => Assert.Equal(0, row.ZIndex));
    }
}