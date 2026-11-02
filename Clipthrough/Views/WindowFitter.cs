using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Clipthrough.Views;

/// <summary>Where windows find out which screens exist.</summary>
internal interface IScreenSource
{
    IReadOnlyList<ScreenArea> All(Window window);

    ScreenArea? ScreenOf(Window window);

    ScreenArea? ScreenAt(Window window, PixelPoint point);

    /// <summary>Calls <paramref name="changed"/> when monitors are added, removed or rearranged.</summary>
    IDisposable OnChanged(Window window, Action changed);
}

/// <summary>
/// Keeps windows inside a screen's working area: when they open, when they are
/// shown again, when their DPI changes, when monitors come and go, and when a
/// drag leaves them on another screen.
/// </summary>
/// <remarks>
/// Avalonia does none of this for us. A <c>CenterOwner</c> window larger than
/// the screen is centred with both edges off it, a window keeps its DIP size
/// when it moves to a higher-scaling monitor, and a window hidden in the tray
/// keeps its position when the monitor it was on is unplugged.
/// </remarks>
internal static class WindowFitter
{
    private static IScreenSource s_screens = new AvaloniaScreenSource();

    public static IScreenSource Screens => s_screens;

    /// <summary>
    /// Substitutes the screens for the scope's lifetime. Headless has a single
    /// stub screen, so tests that need a portrait or laptop one set it here.
    /// </summary>
    internal static IDisposable UseScreens(IScreenSource screens)
    {
        var previous = s_screens;
        s_screens = screens;
        return new Disposer(() => s_screens = previous);
    }

    /// <summary>
    /// Fits <paramref name="window"/> when it opens, and shrinks it into the
    /// new screen after a DPI change, when the set of monitors changes, or when the
    /// user drags it onto another screen (Windows only; see <see cref="MoveSizeLoop"/>).
    /// </summary>
    /// <param name="fitOnOpen">False for a window that places itself when it opens.</param>
    public static void Attach(Window window, bool fitOnOpen = true)
    {
        if (fitOnOpen)
        {
            window.Opened += OnOpened;
        }

        window.ScalingChanged += OnScalingChanged;
        var screensChanged = s_screens.OnChanged(window, OnScreensChanged);
        var moveSizeLoop = new MoveSizeLoop(window);
        s_moveSizeLoops.AddOrUpdate(window, moveSizeLoop);
        Win32Properties.CustomWndProcHookCallback moveSizeHook = moveSizeLoop.Hook;
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, moveSizeHook);
        }

        window.Closed += OnClosed;

        // Opened is raised on every Show, so a window hidden to the tray is refitted
        // when it comes back, onto whatever screens exist by then.
        void OnOpened(object? sender, EventArgs e)
        {
            Fit(window);

            // Again once laid out: a SizeToContent window only learns its real
            // height in its first layout pass, after Opened.
            Dispatcher.UIThread.Post(() => Fit(window), DispatcherPriority.Loaded);
        }

        // Posted rather than run inline: the scaling changes partway through
        // Windows' own WM_DPICHANGED resize, and while the user is dragging the
        // window across monitors moving it would fight the drag. Only a window
        // that no longer fits is resized.
        void OnScalingChanged(object? sender, EventArgs e)
            => Dispatcher.UIThread.Post(() => Fit(window, onlyIfTooLarge: true), DispatcherPriority.Background);

        void OnScreensChanged()
            => Dispatcher.UIThread.Post(() => Fit(window), DispatcherPriority.Background);

        void OnClosed(object? sender, EventArgs e)
        {
            window.Opened -= OnOpened;
            window.ScalingChanged -= OnScalingChanged;
            screensChanged.Dispose();
            if (OperatingSystem.IsWindows())
            {
                Win32Properties.RemoveWndProcHookCallback(window, moveSizeHook);
            }

            s_moveSizeLoops.Remove(window);
            window.Closed -= OnClosed;
        }
    }

    /// <summary>
    /// Shrinks and moves <paramref name="window"/> into its screen's working
    /// area. Returns whether anything changed.
    /// </summary>
    /// <param name="onlyIfTooLarge">Leave a window that is merely hanging off
    /// an edge where it is; only resize one that cannot fit as it is.</param>
    public static bool Fit(Window window, bool onlyIfTooLarge = false)
    {
        if (window.WindowState != WindowState.Normal || !window.IsVisible || s_screens.ScreenOf(window) is not { } screen)
        {
            return false;
        }

        var requested = RequestedClientSize(window);
        var placement = WindowGeometry.Fit(requested, MinimumClientSize(window), FrameExtra(window), window.Position, screen);
        if (placement is not { } fit || (onlyIfTooLarge && fit.ClientSize == requested))
        {
            return false;
        }

        Apply(window, fit, requested);
        return true;
    }

    /// <summary>
    /// Puts <paramref name="window"/> beside the text caret on whichever screen
    /// the caret is on, first shrinking it to fit that screen.
    /// </summary>
    public static void PlaceNearCaret(Window window, PixelPoint caret)
    {
        if (window.WindowState != WindowState.Normal || s_screens.ScreenAt(window, caret) is not { } screen)
        {
            return;
        }

        var requested = RequestedClientSize(window);
        var frameExtra = FrameExtra(window);
        var size = WindowGeometry.FitSize(requested, MinimumClientSize(window), frameExtra, screen);
        Apply(window, new WindowPlacement(size, WindowGeometry.PlaceNearCaret(caret, size, frameExtra, screen)), requested);
    }

    /// <summary>
    /// Moves a remembered placement back onto the screens connected now.
    /// </summary>
    public static void Restore(Window window, WindowPlacement remembered)
    {
        var screens = s_screens.All(window);
        if (screens.Count == 0)
        {
            return;
        }

        var placement = WindowGeometry.Restore(remembered, MinimumClientSize(window), FrameExtra(window), screens);
        Apply(window, placement, RequestedClientSize(window));
    }

    internal static Size RequestedClientSize(Window window)
        => new(
            double.IsNaN(window.Width) ? window.ClientSize.Width : window.Width,
            double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height);

    private static Size MinimumClientSize(Window window)
        => new(
            double.IsNaN(window.MinWidth) ? 0 : window.MinWidth,
            double.IsNaN(window.MinHeight) ? 0 : window.MinHeight);

    private static Size FrameExtra(Window window)
        => window.FrameSize is { } frame
            ? new Size(Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height))
            : default;

    private static void Apply(Window window, WindowPlacement placement, Size requested)
    {
        // A window sized to its content would grow straight back past the edge.
        if (placement.ClientSize.Height < requested.Height)
        {
            window.SizeToContent &= ~SizeToContent.Height;
        }

        if (placement.ClientSize.Width < requested.Width)
        {
            window.SizeToContent &= ~SizeToContent.Width;
        }

        if (placement.ClientSize != requested)
        {
            window.Width = placement.ClientSize.Width;
            window.Height = placement.ClientSize.Height;
        }

        window.Position = placement.Position;
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private static readonly ConditionalWeakTable<Window, MoveSizeLoop> s_moveSizeLoops = new();

    /// <summary>The window's move/size loop tracker, for tests: headless has no window messages.</summary>
    internal static MoveSizeLoop? MoveSizeLoopOf(Window window)
        => s_moveSizeLoops.TryGetValue(window, out var loop) ? loop : null;

    /// <summary>
    /// A user dragging the window by its caption or border, which Windows runs
    /// as a modal loop: WM_ENTERSIZEMOVE, then WM_MOVING or WM_SIZING for each
    /// step, then WM_EXITSIZEMOVE when the button is released.
    /// </summary>
    /// <remarks>
    /// A drag between two screens at the same scaling raises no DPI change, so
    /// a window dragged from an ultrawide onto a smaller monitor stayed wider
    /// than it. Only the end of a move is acted on, never a step of it, and only
    /// when it ended on another screen: a resize is the user choosing a size,
    /// and a window that still fits is left where it was put.
    /// </remarks>
    internal sealed class MoveSizeLoop(Window window)
    {
        private const uint WmSizing = 0x0214;
        private const uint WmMoving = 0x0216;
        private const uint WmEnterSizeMove = 0x0231;
        private const uint WmExitSizeMove = 0x0232;

        private ScreenArea? _startScreen;
        private bool _moved;
        private bool _sized;

        public void Enter()
        {
            _startScreen = s_screens.ScreenOf(window);
            _moved = false;
            _sized = false;
        }

        public void Moving() => _moved = true;

        public void Sizing() => _sized = true;

        public void Exit()
        {
            var start = _startScreen;
            _startScreen = null;
            if (!_moved || _sized || start is null || s_screens.ScreenOf(window) is not { } end || end == start)
            {
                return;
            }

            // Posted, like the DPI path: WM_EXITSIZEMOVE arrives inside the window procedure.
            Dispatcher.UIThread.Post(() => Fit(window, onlyIfTooLarge: true), DispatcherPriority.Background);
        }

        public nint Hook(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
        {
            switch (msg)
            {
                case WmEnterSizeMove:
                    Enter();
                    break;
                case WmMoving:
                    Moving();
                    break;
                case WmSizing:
                    Sizing();
                    break;
                case WmExitSizeMove:
                    Exit();
                    break;
            }

            return 0;
        }
    }

    private sealed class AvaloniaScreenSource : IScreenSource
    {
        public IReadOnlyList<ScreenArea> All(Window window)
            => window.Screens.All
                .OrderByDescending(s => s.IsPrimary)
                .Select(s => new ScreenArea(s.WorkingArea, s.Scaling))
                .ToList();

        public ScreenArea? ScreenOf(Window window)
            => (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } s
                ? new ScreenArea(s.WorkingArea, s.Scaling)
                : null;

        public ScreenArea? ScreenAt(Window window, PixelPoint point)
            => window.Screens.ScreenFromPoint(point) is { } s
                ? new ScreenArea(s.WorkingArea, s.Scaling)
                : null;

        public IDisposable OnChanged(Window window, Action changed)
        {
            void Handler(object? sender, EventArgs e) => changed();
            window.Screens.Changed += Handler;
            return new Disposer(() => window.Screens.Changed -= Handler);
        }
    }
}
