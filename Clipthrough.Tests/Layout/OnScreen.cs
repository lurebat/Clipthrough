using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit;

namespace Clipthrough.Tests.Layout;

internal enum Reach
{
    Visible,
    ScrollReachable,
    Clipped,
    OffScreen,
    Missing,
}

/// <summary>
/// Whether the user can actually see and reach a control: inside every
/// ancestor that clips it (a scroller's viewport, a ClipToBounds border) and
/// inside the screen's working area.
/// </summary>
/// <remarks>
/// "Inside the window's client area" proves nothing on its own: a window held
/// above its screen by a minimum size lays out at that minimum, so a button
/// can be inside the window and still off the screen. Every check here is
/// against the working area.
/// </remarks>
internal static class OnScreen
{
    /// <param name="clientOrigin">Where the window's client area sits on the
    /// working area, in DIP relative to the working area's top-left.</param>
    /// <param name="workArea">The working area's size, in DIP.</param>
    public static (Reach Reach, double VisibleFraction) Classify(Control? control, Window window, Point clientOrigin, Size workArea)
    {
        if (control is null || !control.IsEffectivelyVisible || control.TranslatePoint(default, window) is not { } topLeft)
        {
            return (Reach.Missing, 0);
        }

        var rect = new Rect(topLeft, control.Bounds.Size);
        var visible = rect;
        var inScrollableViewer = false;
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is ScrollViewer sv && (sv.Extent.Width > sv.Viewport.Width + 0.5 || sv.Extent.Height > sv.Viewport.Height + 0.5))
            {
                inScrollableViewer = true;
            }

            if (ancestor.ClipToBounds && ancestor.TranslatePoint(default, window) is { } at)
            {
                visible = visible.Intersect(new Rect(at, ancestor.Bounds.Size));
            }
        }

        var area = rect.Width * rect.Height;
        var screen = visible.Translate(clientOrigin).Intersect(new Rect(workArea));
        var fraction = area <= 0 ? 0 : Math.Max(0, screen.Width) * Math.Max(0, screen.Height) / area;
        var clippedInWindow = visible.Width * visible.Height < area - 1;

        var reach = fraction >= 0.99 ? Reach.Visible
            : clippedInWindow && inScrollableViewer ? Reach.ScrollReachable
            : clippedInWindow ? Reach.Clipped
            : Reach.OffScreen;
        return (reach, fraction);
    }

    /// <summary>
    /// Asserts <paramref name="control"/> is wholly visible on a window that
    /// fills a <paramref name="workArea"/> working area from its top-left.
    /// </summary>
    public static void AssertVisible(string label, Control? control, Window window, Size workArea)
        => AssertVisible(label, control, window, default, workArea);

    public static void AssertVisible(string label, Control? control, Window window, Point clientOrigin, Size workArea)
    {
        var (reach, fraction) = Classify(control, window, clientOrigin, workArea);
        Assert.True(
            reach == Reach.Visible,
            $"{label} is {reach} ({fraction:P0} visible) on a {workArea.Width}x{workArea.Height} DIP working area; the window is {window.Bounds.Width:F0}x{window.Bounds.Height:F0}");
    }

    /// <summary>Visible now, or brought fully into view by scrolling its scroller.</summary>
    public static void AssertReachable(string label, Control? control, Window window, Size workArea)
    {
        Assert.NotNull(control);
        var (reach, fraction) = Classify(control, window, default, workArea);
        if (reach == Reach.ScrollReachable)
        {
            // Scrolled by hand: headless accepts BringIntoView and never scrolls.
            ScrollTo(control!);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            (reach, fraction) = Classify(control, window, default, workArea);
        }

        Assert.True(
            reach == Reach.Visible,
            $"{label} is {reach} ({fraction:P0} visible) on a {workArea.Width}x{workArea.Height} DIP working area, even after scrolling to it");
    }

    private static void ScrollTo(Control control)
    {
        foreach (var scroller in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroller.Content is not Visual content || control.TranslatePoint(default, content) is not { } at)
            {
                continue;
            }

            var maxX = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
            var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            var x = Math.Clamp(at.X + control.Bounds.Width - scroller.Viewport.Width, 0, maxX);
            var y = Math.Clamp(at.Y + control.Bounds.Height - scroller.Viewport.Height, 0, maxY);
            scroller.Offset = new Vector(Math.Min(x, at.X), Math.Min(y, at.Y));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    public static bool Overlaps(Control a, Control b, Visual relativeTo)
    {
        var ra = new Rect(a.TranslatePoint(default, relativeTo)!.Value, a.Bounds.Size);
        var rb = new Rect(b.TranslatePoint(default, relativeTo)!.Value, b.Bounds.Size);
        var overlap = ra.Intersect(rb);
        return overlap.Width > 0.5 && overlap.Height > 0.5;
    }

    public static T? FindByContent<T>(Window window, string content)
        where T : ContentControl
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => Equals(c.Content, content) && c.IsEffectivelyVisible);

    public static Button? FindByCommand(Window window, System.Windows.Input.ICommand command)
        => window.GetVisualDescendants().OfType<Button>()
            .LastOrDefault(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);
}
