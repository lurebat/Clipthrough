using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
/// Hover and selection crossfade (O3). The highlight is the ListBoxItem
/// template's PART_ContentPresenter background - the clip card's own Background
/// is a local binding, so styling the card never showed anything. These read
/// that background right after a change, before the animation clock has moved:
/// a crossfade is still on its starting colour, an instant change is already
/// on its final one.
/// </summary>
public sealed class ListHighlightMotionHeadlessTests
{
    private static MainWindowTestHarness Create(bool motion, int clips = 10)
    {
        var system = new TestSystemInteractionService { OsAnimationsEnabled = true };
        var harness = MainWindowTestHarness.Create(
            configureSettings: s => s with { Motion = motion ? MotionPreference.On : MotionPreference.Off },
            motionFactory: settings => new MotionService(settings, system));
        harness.SeedClips(clips);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Settle();
        return harness;
    }

    /// <summary>Lets real time pass with the render clock ticking, so transitions finish.</summary>
    private static void Settle(int ms = 400)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(16);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ContentPresenter Presenter(MainWindowTestHarness harness, int index)
    {
        harness.ClipList.ScrollIntoView(index);
        harness.ClipList.UpdateLayout();
        var item = Assert.IsType<ListBoxItem>(harness.ClipList.ContainerFromIndex(index));
        return item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    [AvaloniaFact]
    public void WithMotion_SelectionCrossfades()
    {
        using var harness = Create(motion: true);
        var before = ColorOf(Presenter(harness, 1).Background);
        var selected = ColorOf(Presenter(harness, 0).Background);
        Assert.NotEqual(before, selected);

        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[1];
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(selected, ColorOf(Presenter(harness, 1).Background));

        Settle();
        Assert.Equal(selected, ColorOf(Presenter(harness, 1).Background));
    }

    [AvaloniaFact]
    public void WithoutMotion_SelectionIsInstant()
    {
        using var harness = Create(motion: false);
        var selected = ColorOf(Presenter(harness, 0).Background);

        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[1];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(selected, ColorOf(Presenter(harness, 1).Background));
    }

    /// <summary>
    /// Hover is a faint tint (about 10% alpha), and the headless clock does not
    /// carry it through a transition the way it does the selection, so this pins
    /// the transition itself rather than a mid-fade colour.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hover_FadesOnlyWithMotion(bool motion)
    {
        using var harness = Create(motion);
        var transition = Presenter(harness, 2).Transitions?.OfType<Avalonia.Animation.BrushTransition>()
            .SingleOrDefault(x => x.Property == ContentPresenter.BackgroundProperty);

        if (motion)
        {
            Assert.NotNull(transition);
            Assert.Equal(TimeSpan.FromMilliseconds(100), transition!.Duration);
        }
        else
        {
            Assert.Null(transition);
        }
    }
    /// <summary>
    /// Holding an arrow key moves the selection every ~33 ms; a highlight that
    /// fades in over 83 ms would trail behind the key. Repeats are instant.
    /// </summary>
    [AvaloniaFact]
    public void WithMotion_AKeyRepeat_MovesTheHighlightInstantly()
    {
        using var harness = Create(motion: true);
        harness.FocusClipList();
        var selected = ColorOf(Presenter(harness, 0).Background);

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[1], harness.ViewModel.SelectedClip);

        // Still held: the next KeyDown is the keyboard's auto-repeat.
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[2], harness.ViewModel.SelectedClip);

        Assert.Equal(selected, ColorOf(Presenter(harness, 2).Background));
    }

    /// <summary>Released and pressed again is a new press, and fades.</summary>
    [AvaloniaFact]
    public void WithMotion_AFreshPressAfterRelease_StillFades()
    {
        using var harness = Create(motion: true);
        harness.FocusClipList();
        var selected = ColorOf(Presenter(harness, 0).Background);

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        harness.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);

        // At once, well inside the repeat window: only the KeyUp says it is new.
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[2], harness.ViewModel.SelectedClip);

        Assert.NotEqual(selected, ColorOf(Presenter(harness, 2).Background));
    }

    /// <summary>
    /// A KeyUp can be lost - the key released while another window had focus.
    /// A press that comes long after the last one is still a fresh press and
    /// fades, rather than being taken for an auto-repeat.
    /// </summary>
    [AvaloniaFact]
    public void WithMotion_APressLongAfterAnUnreleasedOne_StillFades()
    {
        using var harness = Create(motion: true);
        harness.FocusClipList();
        var selected = ColorOf(Presenter(harness, 0).Background);

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Settle();

        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[2], harness.ViewModel.SelectedClip);

        Assert.NotEqual(selected, ColorOf(Presenter(harness, 2).Background));
    }

    /// <summary>A click after holding an arrow key fades again.</summary>
    [AvaloniaFact]
    public void WithMotion_AClickAfterAKeyRepeat_Fades()
    {
        using var harness = Create(motion: true);
        harness.FocusClipList();
        var selected = ColorOf(Presenter(harness, 0).Background);
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        harness.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Clipthrough.Views.MainWindow.KeyRepeatClass, harness.ClipList.Classes);

        var row = Presenter(harness, 5);
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.MouseDown(centre, MouseButton.Left);
        harness.Window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(harness.ViewModel.Clips[5], harness.ViewModel.SelectedClip);

        Assert.NotEqual(selected, ColorOf(Presenter(harness, 5).Background));
    }
    /// <summary>
    /// What makes a recycling guard unnecessary: Avalonia keeps the selected
    /// row's container realized however far the list scrolls, so a selected
    /// highlight is never handed to another row mid-fade. If an Avalonia update
    /// starts recycling it, this fails, and the crossfade needs a guard that
    /// drops the transition while a container is re-prepared.
    /// </summary>
    [AvaloniaFact]
    public void TheSelectedRowsContainer_IsNeverRecycled()
    {
        using var harness = Create(motion: true, clips: 80);
        var selectedContainer = harness.ClipList.ContainerFromIndex(0);
        Assert.NotNull(selectedContainer);
        harness.FocusSearchBox();

        harness.ClipList.ScrollIntoView(79);
        harness.ClipList.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(selectedContainer, harness.ClipList.ContainerFromIndex(0));
        Assert.Equal(0, harness.ClipList.IndexFromContainer(selectedContainer!));
    }
}