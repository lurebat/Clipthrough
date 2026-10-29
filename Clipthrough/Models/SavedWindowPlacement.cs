namespace Clipthrough.Models;

/// <summary>
/// The main window's last normal (not maximized) geometry, and whether it was
/// maximized, as remembered between runs.
/// </summary>
/// <param name="X">Left edge of the frame, in pixels.</param>
/// <param name="Y">Top edge of the frame, in pixels.</param>
/// <param name="Width">Client width, in DIP.</param>
/// <param name="Height">Client height, in DIP.</param>
public sealed record SavedWindowPlacement(int X, int Y, double Width, double Height, bool Maximized);