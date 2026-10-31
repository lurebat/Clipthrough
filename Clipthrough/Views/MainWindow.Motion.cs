using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Clipthrough.Services;

namespace Clipthrough.Views;

/// <summary>
/// The motion policy, applied: the <c>motion</c> class on the popup and on the
/// windows it owns. Every transition and animation in ModernTheme.axaml is
/// scoped under that class, so a window without it does not animate at all.
/// </summary>
public partial class MainWindow
{
    internal const string MotionClass = "motion";

    /// <summary>
    /// On the clip list while an arrow key is auto-repeating: the selection then
    /// moves every ~33 ms, and an 83 ms crossfade would trail behind the key.
    /// </summary>
    internal const string KeyRepeatClass = "key-repeat";


    private Key? m_heldNavigationKey;
    private readonly List<Window> m_motionWindows = [];
    private IMotionService? m_motionService;
    private IDisposable? m_motionSubscription;

    private void InitializeMotion(IMotionService? motionService)
    {
        m_motionService = motionService;
        m_motionWindows.Add(this);
        AddHandler(KeyDownEvent, TrackNavigationKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, TrackNavigationKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

        if (motionService is null)
        {
            return;
        }

        m_motionSubscription = motionService.MotionEnabled.Subscribe(ApplyMotion);
        Opened += (_, _) => motionService.Attach(this);
        Closed += (_, _) => m_motionSubscription?.Dispose();
    }

    /// <summary>Whether this popup's UI animates right now.</summary>
    internal bool IsMotionEnabled => m_motionService?.IsMotionEnabled == true;

    private static bool IsNavigationKey(Key key)
        => key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    /// <summary>
    /// A second KeyDown for the same navigation key without a KeyUp between
    /// them is the keyboard's auto-repeat.
    /// </summary>
    private void TrackNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsNavigationKey(e.Key))
        {
            return;
        }

        var repeating = m_heldNavigationKey == e.Key;
        m_heldNavigationKey = e.Key;
        this.FindControl<ListBox>("ClipsListBox")?.Classes.Set(KeyRepeatClass, repeating);
    }

    private void TrackNavigationKeyUp(object? sender, KeyEventArgs e)
    {
        if (m_heldNavigationKey != e.Key)
        {
            return;
        }

        m_heldNavigationKey = null;
        this.FindControl<ListBox>("ClipsListBox")?.Classes.Set(KeyRepeatClass, false);
    }

    private void TrackMotion(Window window)
    {
        m_motionWindows.Add(window);
        window.Classes.Set(MotionClass, IsMotionEnabled);
        window.Closed += (_, _) => m_motionWindows.Remove(window);
    }

    private void ApplyMotion(bool enabled)
    {
        foreach (var window in m_motionWindows.ToArray())
        {
            window.Classes.Set(MotionClass, enabled);
        }
    }
}