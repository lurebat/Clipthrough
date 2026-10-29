using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Clipthrough.Views;

/// <summary>A screen's working area in pixels and its DIP-to-pixel scaling.</summary>
internal readonly record struct ScreenArea(PixelRect WorkingArea, double Scaling);

/// <summary>A window's client size in DIP and its frame's top-left in pixels.</summary>
internal readonly record struct WindowPlacement(Size ClientSize, PixelPoint Position);

/// <summary>
/// The geometry that keeps a window on a screen, as pure functions so it can
/// be tested without one.
/// </summary>
/// <remarks>
/// Sizes are DIP and positions pixels, which is how Avalonia's Window exposes
/// them. Mixing the two is the bug this exists to prevent: positioning a
/// 1360 DIP window as if it were 1360 px placed it 556 DIP past the edge of a
/// 250% portrait screen. Every conversion goes through the scaling of the
/// screen the window is going to, not the one it is on.
/// </remarks>
internal static class WindowGeometry
{
    /// <summary>
    /// The client size and position that keep a window inside
    /// <paramref name="screen"/>'s working area, or null when it already fits.
    /// </summary>
    /// <param name="clientSize">Requested client size, in DIP.</param>
    /// <param name="minClientSize">The window's minimum, in DIP. A window is never
    /// fitted below it; one whose minimum is larger than the screen is pinned to
    /// the working area's top-left so its caption at least stays reachable.</param>
    /// <param name="frameExtra">What the frame adds to the client size, in DIP.</param>
    /// <param name="position">Current top-left of the frame, in pixels.</param>
    public static WindowPlacement? Fit(Size clientSize, Size minClientSize, Size frameExtra, PixelPoint position, ScreenArea screen)
    {
        var size = FitSize(clientSize, minClientSize, frameExtra, screen);
        var fitted = Clamp(position, OuterPixels(size, frameExtra, screen.Scaling), screen.WorkingArea);
        if (size == clientSize && fitted == position)
        {
            return null;
        }

        return new WindowPlacement(size, fitted);
    }

    /// <summary>The largest client size, up to <paramref name="clientSize"/>, whose frame fits.</summary>
    public static Size FitSize(Size clientSize, Size minClientSize, Size frameExtra, ScreenArea screen)
    {
        var extraWidth = Math.Max(0, frameExtra.Width);
        var extraHeight = Math.Max(0, frameExtra.Height);
        var availableWidth = (screen.WorkingArea.Width / screen.Scaling) - extraWidth;
        var availableHeight = (screen.WorkingArea.Height / screen.Scaling) - extraHeight;
        return new Size(
            Math.Max(minClientSize.Width, Math.Min(clientSize.Width, availableWidth)),
            Math.Max(minClientSize.Height, Math.Min(clientSize.Height, availableHeight)));
    }

    /// <summary>
    /// Where to put a window of <paramref name="clientSize"/> so it opens just
    /// below and right of the text caret, flipped above it when there is no
    /// room below, and clamped to the caret's screen.
    /// </summary>
    /// <param name="caret">The caret, in pixels.</param>
    /// <param name="screen">The screen the caret is on, which the window is
    /// moving to: its scaling, not the window's current one, sizes the frame.</param>
    public static PixelPoint PlaceNearCaret(PixelPoint caret, Size clientSize, Size frameExtra, ScreenArea screen)
    {
        const int GapBelow = 20;
        const int GapAbove = 10;
        var outer = OuterPixels(clientSize, frameExtra, screen.Scaling);
        var y = caret.Y + GapBelow;
        if (y + outer.Height > screen.WorkingArea.Bottom)
        {
            y = caret.Y - outer.Height - GapAbove;
        }

        return Clamp(new PixelPoint(caret.X, y), outer, screen.WorkingArea);
    }

    /// <summary>
    /// Where a remembered window goes back to: on the screen that holds most
    /// of it, or the first screen when that screen is gone, shrunk and moved
    /// to fit that screen's working area.
    /// </summary>
    /// <param name="remembered">The saved client size (DIP) and frame position (pixels).</param>
    /// <param name="screens">The screens connected now; the first is the primary.</param>
    public static WindowPlacement Restore(WindowPlacement remembered, Size minClientSize, Size frameExtra, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0)
        {
            return remembered;
        }

        var screen = screens
            .Select(s => (Screen: s, Overlap: OverlapArea(s, remembered, frameExtra)))
            .Where(s => s.Overlap > 0)
            .OrderByDescending(s => s.Overlap)
            .Select(s => (ScreenArea?)s.Screen)
            .FirstOrDefault() ?? screens[0];

        var size = FitSize(remembered.ClientSize, minClientSize, frameExtra, screen);
        var position = OverlapArea(screen, remembered, frameExtra) > 0
            ? remembered.Position
            : screen.WorkingArea.Position;
        return new WindowPlacement(size, Clamp(position, OuterPixels(size, frameExtra, screen.Scaling), screen.WorkingArea));
    }

    private static long OverlapArea(ScreenArea screen, WindowPlacement placement, Size frameExtra)
    {
        var rect = new PixelRect(placement.Position, OuterPixels(placement.ClientSize, frameExtra, screen.Scaling));
        var overlap = rect.Intersect(screen.WorkingArea);
        return (long)overlap.Width * overlap.Height;
    }

    private static PixelSize OuterPixels(Size clientSize, Size frameExtra, double scaling)
        => new(
            (int)Math.Ceiling((clientSize.Width + Math.Max(0, frameExtra.Width)) * scaling),
            (int)Math.Ceiling((clientSize.Height + Math.Max(0, frameExtra.Height)) * scaling));

    /// <summary>
    /// Moves a frame inside the working area. When it is larger than the area
    /// the top-left wins, which keeps the caption and the menu on screen.
    /// </summary>
    private static PixelPoint Clamp(PixelPoint position, PixelSize outer, PixelRect workingArea)
        => new(
            Math.Max(workingArea.X, Math.Min(position.X, workingArea.Right - outer.Width)),
            Math.Max(workingArea.Y, Math.Min(position.Y, workingArea.Bottom - outer.Height)));
}
