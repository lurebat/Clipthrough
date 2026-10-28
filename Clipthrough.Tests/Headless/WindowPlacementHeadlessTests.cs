using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Tests.Layout;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The main window comes back at the size and state it was left in, but only
/// as far as the screens connected now allow.
/// </summary>
public sealed class WindowPlacementHeadlessTests
{
    private static readonly ScreenArea FullHd = TestScreens.Dip(1920, 1032, 1.0);

    [AvaloniaFact]
    public void TheRememberedSizeAndPositionAreRestored()
    {
        var store = new RecordingPlacementStore(new SavedWindowPlacement(200, 100, 1200, 800, Maximized: false));
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd));
        using var harness = MainWindowTestHarness.Create(placementStore: store);

        Assert.Equal(new Size(1200, 800), harness.Window.Bounds.Size);
        Assert.Equal(new PixelPoint(200, 100), harness.Window.Position);
    }

    [AvaloniaFact]
    public void APlacementOnAMonitorThatIsGone_ComesBackInsideTheScreenThatIsLeft()
    {
        // Saved on a 2560 DIP monitor to the right that has since been unplugged.
        var store = new RecordingPlacementStore(new SavedWindowPlacement(2300, 100, 2200, 1300, Maximized: false));
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd));
        using var harness = MainWindowTestHarness.Create(placementStore: store);

        WindowFitHeadlessTests.AssertInside(harness.Window, FullHd);
    }

    [AvaloniaFact]
    public void AMaximizedWindow_IsRestoredMaximized()
    {
        var store = new RecordingPlacementStore(new SavedWindowPlacement(200, 100, 1200, 800, Maximized: true));
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd));
        using var harness = MainWindowTestHarness.Create(placementStore: store);

        Assert.Equal(WindowState.Maximized, harness.Window.WindowState);
    }

    [AvaloniaFact]
    public void HidingTheWindow_RemembersItsNormalGeometry()
    {
        var store = new RecordingPlacementStore(null);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd));
        using var harness = MainWindowTestHarness.Create(placementStore: store);
        harness.Window.Position = new PixelPoint(300, 150);
        harness.Window.Width = 1000;
        harness.Window.Height = 700;
        Dispatcher.UIThread.RunJobs();

        harness.Window.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new SavedWindowPlacement(300, 150, 1000, 700, Maximized: false), store.Saved[^1]);
    }

    [AvaloniaFact]
    public void HidingAMaximizedWindow_KeepsItsNormalGeometryAndTheState()
    {
        var store = new RecordingPlacementStore(null);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd));
        using var harness = MainWindowTestHarness.Create(placementStore: store);
        harness.Window.Position = new PixelPoint(300, 150);
        harness.Window.Width = 1000;
        harness.Window.Height = 700;
        Dispatcher.UIThread.RunJobs();

        harness.Window.WindowState = WindowState.Maximized;

        // A real platform resizes a maximized window to the screen; headless only
        // records the state, so do its part. This is the size that must not be
        // remembered as the normal one.
        harness.Window.Width = 1920;
        harness.Window.Height = 1001;
        Dispatcher.UIThread.RunJobs();
        harness.Window.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new SavedWindowPlacement(300, 150, 1000, 700, Maximized: true), store.Saved[^1]);
    }

    private sealed class RecordingPlacementStore(SavedWindowPlacement? saved) : IWindowPlacementStore
    {
        public List<SavedWindowPlacement> Saved { get; } = [];

        public SavedWindowPlacement? Load() => saved;

        public void Save(SavedWindowPlacement placement) => Saved.Add(placement);
    }
}
