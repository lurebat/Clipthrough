using System;
using System.Collections.Generic;
using Avalonia.Controls;
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

    private readonly List<Window> m_motionWindows = [];
    private IMotionService? m_motionService;
    private IDisposable? m_motionSubscription;

    private void InitializeMotion(IMotionService? motionService)
    {
        m_motionService = motionService;
        m_motionWindows.Add(this);
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