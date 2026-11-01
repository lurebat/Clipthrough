using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Keyboard focus ring and press feedback (O9). The ring marks the row the
/// keyboard is on, and only when the keyboard put it there - a click shows the
/// selection, not a ring. It is always on; it is a focus indicator, not motion.
/// Button press feedback (a 0.97 scale) is motion and follows the policy.
/// </summary>
public sealed class FocusAndPressHeadlessTests
{
    private static MainWindowTestHarness Create(bool motion)
    {
        var system = new TestSystemInteractionService { OsAnimationsEnabled = true };
        var harness = MainWindowTestHarness.Create(
            configureSettings: s => s with { Motion = motion ? MotionPreference.On : MotionPreference.Off },
            motionFactory: settings => new MotionService(settings, system));
        harness.SeedClips(6);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    private static void Settle(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(16);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Border Card(MainWindowTestHarness harness, int index)
        => ((ListBoxItem)harness.ClipList.ContainerFromIndex(index)!).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("clip-row-card"));

    [AvaloniaFact]
    public void ArrowKeys_PutARingOnTheFocusedRow()
    {
        using var harness = Create(motion: false);
        harness.FocusClipList();

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        harness.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Settle(100);

        Assert.Same(harness.ViewModel.Clips[1], harness.ViewModel.SelectedClip);
        var row = (ListBoxItem)harness.ClipList.ContainerFromIndex(1)!;
        Assert.True(row.IsFocused, "the selected row did not take keyboard focus");
        Assert.NotEqual(0, Card(harness, 1).BoxShadow.Count);
        Assert.Equal(0, Card(harness, 0).BoxShadow.Count);
    }

    [AvaloniaFact]
    public void AClick_SelectsWithoutARing()
    {
        using var harness = Create(motion: false);
        var row = (ListBoxItem)harness.ClipList.ContainerFromIndex(2)!;
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), harness.Window)!.Value;

        harness.Window.MouseDown(centre, MouseButton.Left);
        harness.Window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Settle(100);

        Assert.Same(harness.ViewModel.Clips[2], harness.ViewModel.SelectedClip);
        Assert.Equal(0, Card(harness, 2).BoxShadow.Count);
    }

    private static Button PressableButton(MainWindowTestHarness harness)
        => harness.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Bounds.Width > 20 && b.Content is string);

    private static double ScaleOf(Visual visual) => visual.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void APressedButton_ScalesDownOnlyWithMotion(bool motion)
    {
        using var harness = Create(motion);
        var button = PressableButton(harness);
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), harness.Window)!.Value;

        harness.Window.MouseMove(centre);
        harness.Window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsPressed, "the button never registered the press");
        Settle();

        if (motion)
        {
            Assert.Equal(0.97, ScaleOf(button), 3);
        }
        else
        {
            Assert.Equal(1, ScaleOf(button), 3);
        }

        harness.Window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Settle();
        Assert.Equal(1, ScaleOf(button), 3);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AConfirmDialog_FollowsItsOwnersMotionPolicy(bool motion)
    {
        using var harness = Create(motion);

        _ = Clipthrough.Views.ConfirmDialog.ShowAsync(harness.Window, "Title", "Message", "OK", "Cancel");
        Dispatcher.UIThread.RunJobs();
        var dialog = harness.Window.OwnedWindows.OfType<Clipthrough.Views.ConfirmDialog>().Single();

        Assert.Equal(motion, dialog.Classes.Contains(Clipthrough.Views.MainWindow.MotionClass));
        dialog.Close();
    }
}