using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Tests.Layout;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Moving between workspace breakpoints, in any order and at any scaling,
/// lays the window out exactly as opening it at that size would.
/// </summary>
/// <remarks>
/// Changing the display mode between 1280x720 at 150% and the native mode froze
/// and then killed Clipthrough, with an ArgumentOutOfRangeException in
/// <c>Grid.MeasureCellsGroup</c>. Avalonia's Grid caches each child's row and
/// column, clamped to the definition counts at the time, and assigning new
/// RowDefinitions or ColumnDefinitions does not clear that cache: only a change
/// to a child's Grid.Row/Column (or to the children) does. The filter row goes
/// from three columns and one row (Short) to two columns and two rows (not
/// Short) without any child changing cell, so the Search options button kept
/// its cached column 2 against two columns. Crossing the 600 DIP height line is
/// what the display-mode change did. (Avalonia 12.1.1, Grid.cs ColumnDefinitions
/// setter and ValidateCells)
/// </remarks>
public sealed class WorkspaceBreakpointTransitionHeadlessTests
{
    // One size per layout, with the width class and the Short flag each
    // changed on their own as well as together.
    private static readonly Size[] Sizes =
    [
        new(1366, 689),  // Wide
        new(1366, 500),  // Wide, Short
        new(853, 401),   // Medium, Short
        new(853, 700),   // Medium
        new(576, 1118),  // Narrow
        new(576, 400),   // Narrow, Short
        new(480, 360),   // Narrow, Short, the minimum
        new(1000, 700),  // Medium
        new(3440, 1361), // Wide, ultrawide
    ];

    private static readonly string[] Watched =
    [
        "HeroSearchHost", "HeroTypeSegments", "CategoryFilters", "SortComboBox", "SearchModifiers",
        "SearchOptionsButton", "ClipListPane", "WorkspaceSplitter", "PreviewPane", "FooterStatus",
        "FooterStats", "FooterActions",
    ];

    [AvaloniaTheory]
    [InlineData(853, 401, 853, 700)]  // Medium: Short to not Short
    [InlineData(853, 700, 853, 401)]  // Medium: not Short to Short
    [InlineData(576, 400, 576, 1118)] // Narrow: Short to not Short
    [InlineData(576, 1118, 576, 400)] // Narrow: not Short to Short
    public void CrossingTheShortLine_AtTheSameWidth_DoesNotThrowAndMatchesAFreshLayout(double fromWidth, double fromHeight, double toWidth, double toHeight)
    {
        using var errors = new LayoutErrors();
        using var expected = Open(new Size(toWidth, toHeight));
        using var harness = Open(new Size(fromWidth, fromHeight));

        Resize(harness.Window, new Size(toWidth, toHeight));

        errors.AssertNone();

        AssertSameLayout(expected.Window, harness.Window, $"{fromWidth}x{fromHeight} -> {toWidth}x{toHeight}");
    }

    [AvaloniaFact]
    public void EveryTransitionBetweenEveryLayout_MatchesAFreshLayout()
    {
        using var errors = new LayoutErrors();
        var fresh = Sizes.ToDictionary(size => size, size =>
        {
            using var h = Open(size);
            return Snapshot(h.Window);
        });

        using var harness = Open(Sizes[0]);
        var current = Sizes[0];
        foreach (var from in Sizes)
        {
            foreach (var to in Sizes.Where(s => s != from))
            {
                if (current != from)
                {
                    Resize(harness.Window, from);
                    current = from;
                }

                Resize(harness.Window, to);
                current = to;
                errors.AssertNone();
                AssertSameLayout(fresh[to], Snapshot(harness.Window), $"{from.Width}x{from.Height} -> {to.Width}x{to.Height}");
            }
        }
    }

    [AvaloniaFact]
    public void RapidResizesAndScalingChanges_WithoutALayoutPassBetween_SettleToAFreshLayout()
    {
        using var errors = new LayoutErrors();
        using var harness = Open(Sizes[0]);
        var window = harness.Window;
        var scalings = new[] { 1.0, 1.5, 2.5, 1.25 };

        // Many size changes, several crossing a breakpoint, before any layout
        // pass runs - as a display-mode change delivers them - then one pass.
        for (var round = 0; round < 6; round++)
        {
            foreach (var size in Sizes.Reverse().Concat(Sizes))
            {
                window.Width = size.Width;
                window.Height = size.Height;
            }

            window.SetRenderScaling(scalings[round % scalings.Length]);
            Dispatcher.UIThread.RunJobs();
        }

        // Back to the scaling the fresh window is compared at, so layout
        // rounding does not differ between the two.
        var last = Sizes[^1];
        window.SetRenderScaling(1.0);
        Resize(window, last);

        errors.AssertNone();
        using var expected = Open(last);
        AssertSameLayout(expected.Window, window, "after rapid resizes and scaling changes");
    }

    private static MainWindowTestHarness Open(Size size)
    {
        var scope = WindowFitter.UseScreens(new TestScreens(TestScreens.Dip(4000, 2000, 1.0)));
        var harness = MainWindowTestHarness.Create();
        scope.Dispose();
        harness.Window.Position = new PixelPoint(0, 0);
        Resize(harness.Window, size);
        return harness;
    }

    private static void Resize(Window window, Size size)
    {
        window.Width = size.Width;
        window.Height = size.Height;

        Dispatcher.UIThread.RunJobs();

        // The exception first: a layout pass that throws leaves the old size.
        LayoutErrors.Current?.AssertNone();
        Assert.Equal(size, window.Bounds.Size);
    }

    private static (string Name, bool Visible, Rect Bounds)[] Snapshot(Window window)
        => Watched.Select(name =>
        {
            var control = window.FindControl<Control>(name)!;
            var origin = control.IsEffectivelyVisible ? control.TranslatePoint(default, window) ?? default : default;
            return (name, control.IsEffectivelyVisible, control.IsEffectivelyVisible ? new Rect(origin, control.Bounds.Size) : default);
        }).ToArray();

    private static void AssertSameLayout(Window expected, Window actual, string transition)
        => AssertSameLayout(Snapshot(expected), Snapshot(actual), transition);

    private static void AssertSameLayout((string Name, bool Visible, Rect Bounds)[] expected, (string Name, bool Visible, Rect Bounds)[] actual, string transition)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            var (name, visible, bounds) = expected[i];
            Assert.True(
                visible == actual[i].Visible && Close(bounds, actual[i].Bounds),
                $"{name} after {transition} is {(actual[i].Visible ? actual[i].Bounds.ToString() : "hidden")}; opened at that size it is {(visible ? bounds.ToString() : "hidden")}");
        }
    }

    private static bool Close(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    /// <summary>
    /// Exceptions raised on the dispatcher while a test lays the window out.
    /// The app's handler marks them handled, so headless only shows them as a
    /// window that silently kept its old size; on Windows the layout pass runs
    /// inside the window procedure, where the same exception kills the process.
    /// </summary>
    private sealed class LayoutErrors : IDisposable
    {
        private readonly System.Collections.Generic.List<Exception> _errors = [];

        public LayoutErrors()
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandled;
            Current = this;
        }

        public static LayoutErrors? Current { get; private set; }

        public void AssertNone()
            => Assert.True(_errors.Count == 0, $"layout threw: {string.Join("\n", _errors.Select(e => e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace))}");

        public void Dispose()
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            Current = null;
        }

        private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e) => _errors.Add(e.Exception);
    }
}