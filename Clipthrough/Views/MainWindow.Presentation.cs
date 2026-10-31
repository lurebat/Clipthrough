using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

/// <summary>How a summon out of standby ended.</summary>
public enum StandbyLeave
{
    /// <summary>The popup was not in standby; show it the ordinary way.</summary>
    NotInStandby,

    /// <summary>The popup is uncloaked; activate it.</summary>
    Left,

    /// <summary>
    /// Something else happened while the summon waited - a second toggle, a
    /// dismiss, another summon. The popup is left as that made it; do nothing.
    /// </summary>
    Superseded,
}
/// <summary>
/// Dismissing and summoning the popup.
/// </summary>
/// <remarks>
/// A hidden window stops rendering, and Windows shows its last frame again when
/// it comes back - so the popup used to reopen on the previous session's search
/// results, then reset, then jump the list, before settling some 300-400 ms
/// later. It is now dismissed into standby instead: DWM-cloaked, still shown as
/// far as Avalonia is concerned, so the reset a fresh popup needs happens out of
/// sight and the next summon presents one frame that is already right
/// (docs/solutions/popup-reopens-on-a-stale-frame.md). Where cloaking is not
/// available - not Windows, or DWM refuses - the window is hidden as before.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// How long a summon holds the window cloaked for a reset that has not landed
    /// yet - clearing a search re-queries the library - before showing it anyway.
    /// </summary>
    internal static readonly TimeSpan StandbyResetWait = TimeSpan.FromMilliseconds(250);

    private readonly List<Window> m_cloakedOwnedWindows = [];
    private Task m_standbyReset = Task.CompletedTask;
    private double m_standbyScaling;

    // Bumped by every change of presentation, so a summon that awaited can tell
    // whether it is still the latest request before it uncloaks anything.
    private long m_presentationGeneration;
    private long m_pendingSummonGeneration;

    /// <summary>Whether a summon is waiting for the dismiss-time reset to land.</summary>
    internal bool IsSummonPending => m_pendingSummonGeneration != 0;

    /// <summary>
    /// Drops a pending summon, leaving the popup in standby: a second Alt+V before
    /// the first has shown anything, which would have hidden a popup that had.
    /// </summary>
    internal void CancelPendingSummon()
    {
        m_presentationGeneration++;
        m_pendingSummonGeneration = 0;
    }
    /// <summary>Whether the popup is dismissed into standby (cloaked, still rendering).</summary>
    public bool IsInStandby { get; private set; }

    /// <summary>Whether the user can see the popup: shown, and not in standby.</summary>
    public bool IsPresentedToUser => IsVisible && !IsInStandby;

    /// <summary>
    /// Takes the popup out of sight and gives the foreground back to whatever the
    /// OS would activate next.
    /// </summary>
    internal void Dismiss()
    {
        if (!IsPresentedToUser)
        {
            return;
        }

        var viewModel = DataContext as MainWindowViewModel;
        if (m_systemInteractionService is { } systemInteraction && TryEnterStandby(systemInteraction, viewModel))
        {
            return;
        }

        viewModel?.SetMainWindowVisible(false);
        _ = viewModel?.ClearSearchFilterAsync(forceRefresh: false);
        Hide();
    }

    private bool TryEnterStandby(Services.ISystemInteractionService systemInteraction, MainWindowViewModel? viewModel)
    {
        // The reset runs first, so whatever of it is synchronous - the selection
        // back on the newest clip - is in place before the window leaves.
        m_presentationGeneration++;
        var reset = viewModel?.EnterStandbyAsync() ?? Task.CompletedTask;
        ScrollSelectedClipIntoView();

        // Before the cloak: releasing the foreground hides and re-shows the
        // window, and a window shown with a taskbar button gets it back.
        ShowInTaskbar = false;
        if (!systemInteraction.TryCloakWindow(this, releaseForeground: true))
        {
            ShowInTaskbar = true;
            return false;
        }

        IsInStandby = true;
        m_standbyScaling = RenderScaling;
        foreach (var owned in OwnedWindows.Where(static w => w.IsVisible))
        {
            if (systemInteraction.TryCloakWindow(owned, releaseForeground: false))
            {
                m_cloakedOwnedWindows.Add(owned);
            }
        }

        m_standbyReset = FinishStandbyResetAsync(reset);
        return true;
    }

    private async Task FinishStandbyResetAsync(Task reset)
    {
        try
        {
            await reset;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Popup reset on dismiss failed: {ex.Message}");
        }

        // A cleared search lands the newest clip in a list that may be scrolled
        // anywhere; bring it into view while nobody is looking.
        if (IsInStandby)
        {
            ScrollSelectedClipIntoView();
        }
    }

    /// <summary>
    /// Brings the popup out of standby, holding it cloaked until the dismiss-time
    /// reset has landed (at most <see cref="StandbyResetWait"/>). Activation is
    /// the caller's, and only on <see cref="StandbyLeave.Left"/>.
    /// </summary>
    internal async Task<StandbyLeave> TryLeaveStandbyAsync()
    {
        if (!IsInStandby)
        {
            return StandbyLeave.NotInStandby;
        }

        var generation = ++m_presentationGeneration;
        m_pendingSummonGeneration = generation;
        try
        {
            if (!m_standbyReset.IsCompleted)
            {
                await Task.WhenAny(m_standbyReset, Task.Delay(StandbyResetWait));
            }

            // Placed onto a screen with another scaling: the layout for it has not
            // been drawn yet, and uncloaking now would show the old one stretched.
            if (generation == m_presentationGeneration && !RenderScaling.Equals(m_standbyScaling))
            {
                await NextFrameAsync(StandbyResetWait);
            }

            if (generation != m_presentationGeneration)
            {
                return StandbyLeave.Superseded;
            }

            LeaveStandbyNow();
            return StandbyLeave.Left;
        }
        finally
        {
            if (m_pendingSummonGeneration == generation)
            {
                m_pendingSummonGeneration = 0;
            }
        }
    }

    /// <summary>
    /// Brings the popup out of standby at once, for paths that cannot wait (an
    /// owned window opening). Returns false when it was not in standby.
    /// </summary>
    internal bool LeaveStandbyNow()
    {
        if (!IsInStandby)
        {
            return false;
        }

        IsInStandby = false;
        ShowInTaskbar = true;

        // Opened is not raised for a window that was never hidden, so do what it
        // would have: refit to the screens connected now.
        WindowFitter.Fit(this);

        foreach (var owned in m_cloakedOwnedWindows)
        {
            m_systemInteractionService?.UncloakWindow(owned);
        }

        m_cloakedOwnedWindows.Clear();
        m_systemInteractionService?.UncloakWindow(this);
        (DataContext as MainWindowViewModel)?.LeaveStandby();
        return true;
    }

    /// <summary>
    /// A cloaked window is still a visible window to Windows, which can activate
    /// it - when the app in front closes, for one. Keystrokes would then go to a
    /// search box nobody can see, so the foreground is handed on again.
    /// </summary>
    private void OnActivatedInStandby(object? sender, EventArgs e)
    {
        if (IsInStandby)
        {
            m_systemInteractionService?.TryCloakWindow(this, releaseForeground: true);
        }
    }

    private Task NextFrameAsync(TimeSpan timeout)
    {
        var frame = new TaskCompletionSource();
        RequestAnimationFrame(_ => frame.TrySetResult());
        return Task.WhenAny(frame.Task, Task.Delay(timeout));
    }
}