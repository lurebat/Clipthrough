using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Clipthrough.Views;

namespace Clipthrough.Tests.Layout;

/// <summary>
/// Screens for headless tests, which otherwise only ever see Avalonia's single
/// 1920x1280 stub. Install with <see cref="WindowFitter.UseScreens"/>.
/// </summary>
internal sealed class TestScreens : IScreenSource
{
    private readonly List<(Window Window, Action Changed)> _subscribers = [];

    public TestScreens(params ScreenArea[] screens)
    {
        Screens = [.. screens];
    }

    /// <summary>The connected screens; the first is the primary. Setting it does not notify:
    /// that models a change Windows reports some other way, such as a DPI change.</summary>
    public List<ScreenArea> Screens { get; set; }

    /// <summary>A screen whose working area is <paramref name="widthDip"/> x <paramref name="heightDip"/> DIP.</summary>
    public static ScreenArea Dip(double widthDip, double heightDip, double scaling, int x = 0, int y = 0)
        => new(new PixelRect(x, y, (int)Math.Round(widthDip * scaling), (int)Math.Round(heightDip * scaling)), scaling);

    /// <summary>Replaces the screens and tells every attached window, like a monitor being unplugged.</summary>
    public void Change(params ScreenArea[] screens)
    {
        Screens = [.. screens];
        foreach (var (_, changed) in _subscribers.ToList())
        {
            changed();
        }
    }

    public IReadOnlyList<ScreenArea> All(Window window) => Screens;

    public ScreenArea? ScreenOf(Window window)
    {
        if (Screens.Count == 0)
        {
            return null;
        }

        var centre = window.Position;
        return Screens.FirstOrDefault(s => s.WorkingArea.Contains(centre)) is { WorkingArea.Width: > 0 } hit ? hit : Screens[0];
    }

    public ScreenArea? ScreenAt(Window window, PixelPoint point)
        => Screens.FirstOrDefault(s => s.WorkingArea.Contains(point)) is { WorkingArea.Width: > 0 } hit ? hit : null;

    public IDisposable OnChanged(Window window, Action changed)
    {
        var entry = (window, changed);
        _subscribers.Add(entry);
        return new Unsubscribe(() => _subscribers.Remove(entry));
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
