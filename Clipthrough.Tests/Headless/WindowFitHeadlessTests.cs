using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Tests.Layout;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Windows stay inside a screen's working area when they open, when they come
/// back from the tray, when their DPI changes, and when monitors come and go.
/// Each scenario here used to leave part of a window - and on the requester's
/// portrait screen, its buttons - off the screen.
/// </summary>
public sealed class WindowFitHeadlessTests
{
    // 1440x2992 at 250%, less the taskbar: 576 x 1149 DIP.
    private static readonly ScreenArea Portrait = TestScreens.Dip(576, 1149, 2.5);

    // 1280x720 at 150%: 853 x 432 DIP.
    private static readonly ScreenArea Laptop = TestScreens.Dip(853, 432, 1.5);

    private static readonly ScreenArea FullHd = TestScreens.Dip(1920, 1032, 1.0);

    [AvaloniaFact]
    public void OnOpen_TheMainWindowFitsAPortraitScreen()
    {
        using var screenScope = WindowFitter.UseScreens(new TestScreens(Portrait));
        using var harness = MainWindowTestHarness.Create();

        AssertInside(harness.Window, Portrait);
    }

    [AvaloniaFact]
    public void ADpiChange_ShrinksAWindowThatNoLongerFits()
    {
        var screens = new TestScreens(FullHd);
        using var screenScope = WindowFitter.UseScreens(screens);
        using var harness = MainWindowTestHarness.Create();
        Assert.Equal(1360, harness.Window.Bounds.Width);

        // Dragged onto the 250% portrait screen: Windows reports the move as a
        // DPI change, not as a change of screens.
        screens.Screens = [Portrait];
        harness.Window.SetRenderScaling(2.5);
        Dispatcher.UIThread.RunJobs();

        AssertInside(harness.Window, Portrait);
    }

    [AvaloniaFact]
    public void UnpluggingTheMonitor_MovesTheWindowOntoOneThatIsLeft()
    {
        var right = TestScreens.Dip(2560, 1392, 1.0, x: 1920);
        var screens = new TestScreens(FullHd, right);
        using var screenScope = WindowFitter.UseScreens(screens);
        using var harness = MainWindowTestHarness.Create();
        harness.Window.Position = new PixelPoint(2400, 200);
        Dispatcher.UIThread.RunJobs();

        screens.Change(FullHd);
        Dispatcher.UIThread.RunJobs();

        AssertInside(harness.Window, FullHd);
    }

    [AvaloniaFact]
    public void ShowingFromTheTray_RefitsOntoAScreenThatShrankWhileHidden()
    {
        var screens = new TestScreens(FullHd);
        using var screenScope = WindowFitter.UseScreens(screens);
        using var harness = MainWindowTestHarness.Create();
        harness.Window.Hide();
        Dispatcher.UIThread.RunJobs();

        // Docked laptop undocked while Clipthrough sat in the tray; nothing
        // tells a hidden window.
        screens.Screens = [Laptop];
        harness.Window.SetRenderScaling(Laptop.Scaling);
        harness.Window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertInside(harness.Window, Laptop);
    }

    [AvaloniaFact]
    public void NearTheCaret_TheWindowLandsOnTheCaretsScreenAndInsideIt()
    {
        var portraitRight = TestScreens.Dip(576, 1149, 2.5, x: 1920);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(FullHd, portraitRight));
        using var harness = MainWindowTestHarness.Create();

        WindowFitter.PlaceNearCaret(harness.Window, new PixelPoint(1920 + 100, 500));
        Dispatcher.UIThread.RunJobs();

        AssertInside(harness.Window, portraitRight);
    }

    [AvaloniaFact]
    public void Settings_OnAPortraitScreen_OpensWithSaveAndCancelOnScreen()
    {
        using var screenScope = WindowFitter.UseScreens(new TestScreens(Portrait));
        using var harness = MainWindowTestHarness.Create();
        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();

        AssertInside(settings, Portrait);
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string && !b.GetVisualAncestors().OfType<ScrollViewer>().Any()))
        {
            OnScreen.AssertVisible($"Settings '{button.Content}'", button, settings, OriginOf(settings, Portrait), WorkArea(Portrait));
        }

        settings.Close();
    }

    [AvaloniaFact]
    public void ReEncrypt_OnAShortLaptop_KeepsItsButtonsOnScreen()
    {
        using var screenScope = WindowFitter.UseScreens(new TestScreens(Laptop));
        using var harness = MainWindowTestHarness.Create();
        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();

        _ = RekeyDialog.ShowAsync(settings, harness.StorageOptions);
        Dispatcher.UIThread.RunJobs();
        var rekey = settings.OwnedWindows.OfType<RekeyDialog>().Single();

        AssertInside(rekey, Laptop);
        OnScreen.AssertVisible("Re-encrypt", rekey.FindControl<Button>("ApplyButton"), rekey, OriginOf(rekey, Laptop), WorkArea(Laptop));
        OnScreen.AssertVisible("Cancel", rekey.FindControl<Button>("CancelButton"), rekey, OriginOf(rekey, Laptop), WorkArea(Laptop));

        rekey.Close();
        settings.Close();
    }

    [AvaloniaFact]
    public void SessionLogs_OnAPortraitScreen_KeepsCopyAllOnScreen()
    {
        using var screenScope = WindowFitter.UseScreens(new TestScreens(Portrait));
        using var harness = MainWindowTestHarness.Create();
        var logs = new SessionLogsWindow { DataContext = harness.ViewModel.SessionLogs };
        logs.Show(harness.Window);
        Dispatcher.UIThread.RunJobs();

        AssertInside(logs, Portrait);
        var copyAll = logs.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string s && s.Contains("Copy All"));
        OnScreen.AssertVisible("Copy All", copyAll, logs, OriginOf(logs, Portrait), WorkArea(Portrait));

        logs.Close();
    }

    [AvaloniaFact]
    public void DraggingOntoASmallerScreen_ShrinksTheWindowOnlyWhenTheDragEnds()
    {
        // Same scaling on both, so no DPI change reports the move.
        var ultrawide = TestScreens.Dip(3440, 1400, 1.0);
        var fullHdRight = TestScreens.Dip(1920, 1032, 1.0, x: 3440);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(ultrawide, fullHdRight));
        using var harness = MainWindowTestHarness.Create();
        var window = harness.Window;
        window.Position = new PixelPoint(100, 100);
        window.Width = 2200;
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();

        var drag = WindowFitter.MoveSizeLoopOf(window)!;
        drag.Enter();
        foreach (var x in new[] { 1800, 3000, 3500 })
        {
            drag.Moving();
            window.Position = new PixelPoint(x, 100);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Size(2200, 900), window.Bounds.Size);
            Assert.Equal(new PixelPoint(x, 100), window.Position);
        }

        drag.Exit();
        Dispatcher.UIThread.RunJobs();

        AssertInside(window, fullHdRight);
    }

    [AvaloniaFact]
    public void DraggingAWindowThatFitsOntoAnotherScreen_LeavesItWhereTheUserPutIt()
    {
        var left = TestScreens.Dip(2560, 1392, 1.0);
        var fullHdRight = TestScreens.Dip(1920, 1032, 1.0, x: 2560);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(left, fullHdRight));
        using var harness = MainWindowTestHarness.Create();
        var window = harness.Window;
        window.Position = new PixelPoint(100, 100);
        window.Width = 1200;
        window.Height = 800;
        Dispatcher.UIThread.RunJobs();

        // Fits the right screen, though dropped hanging off its right edge.
        var drag = WindowFitter.MoveSizeLoopOf(window)!;
        drag.Enter();
        drag.Moving();
        window.Position = new PixelPoint(3500, 100);
        drag.Exit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Size(1200, 800), window.Bounds.Size);
        Assert.Equal(new PixelPoint(3500, 100), window.Position);
    }

    [AvaloniaFact]
    public void ResizingAcrossOntoAnotherScreen_IsNotAMove()
    {
        var left = TestScreens.Dip(2560, 1392, 1.0);
        var fullHdRight = TestScreens.Dip(1920, 1032, 1.0, x: 2560);
        using var screenScope = WindowFitter.UseScreens(new TestScreens(left, fullHdRight));
        using var harness = MainWindowTestHarness.Create();
        var window = harness.Window;
        window.Position = new PixelPoint(100, 100);
        window.Width = 1200;
        window.Height = 800;
        Dispatcher.UIThread.RunJobs();

        // Dragging the left border moves the frame's top-left as well.
        var drag = WindowFitter.MoveSizeLoopOf(window)!;
        drag.Enter();
        drag.Sizing();
        window.Position = new PixelPoint(2600, 100);
        window.Width = 2200;
        drag.Exit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Size(2200, 800), window.Bounds.Size);
        Assert.Equal(new PixelPoint(2600, 100), window.Position);
    }

    internal static Size WorkArea(ScreenArea screen)
        => new(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);

    /// <summary>The window's client top-left on the working area, in DIP.</summary>
    internal static Point OriginOf(Window window, ScreenArea screen)
        => new((window.Position.X - screen.WorkingArea.X) / screen.Scaling, (window.Position.Y - screen.WorkingArea.Y) / screen.Scaling);

    internal static void AssertInside(Window window, ScreenArea screen)
    {
        var origin = OriginOf(window, screen);
        var area = WorkArea(screen);
        var size = window.Bounds.Size;
        Assert.True(
            origin.X >= -0.5 && origin.Y >= -0.5 && origin.X + size.Width <= area.Width + 0.5 && origin.Y + size.Height <= area.Height + 0.5,
            $"{window.GetType().Name} spans ({origin.X:F0},{origin.Y:F0})..({origin.X + size.Width:F0},{origin.Y + size.Height:F0}) DIP on a {area.Width:F0}x{area.Height:F0} DIP working area");
    }
}
