using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Remembers where the main window was. Kept apart from <see cref="ISettingsService"/>
/// on purpose: the window moves on every hotkey show, and a settings save
/// re-applies hotkeys, theme and capture rules to everything listening.
/// </summary>
public interface IWindowPlacementStore
{
    /// <summary>The remembered placement, or null when there is none or it is unreadable.</summary>
    SavedWindowPlacement? Load();

    /// <summary>Remembers <paramref name="placement"/>. Never throws.</summary>
    void Save(SavedWindowPlacement placement);

    /// <summary>
    /// Waits, briefly, for the last <see cref="Save"/> to reach the disk. Saves
    /// are written in the background, and the process exits as soon as the
    /// main window has closed, which would cut off the final one. Never throws.
    /// </summary>
    void Flush();
}