namespace Clipthrough.Models;

/// <summary>
/// Whether Clipthrough animates its own UI (list, selection, toasts, presses).
/// </summary>
public enum MotionPreference
{
    /// <summary>Follow Windows' "Animation effects" (SPI_GETCLIENTAREAANIMATION).</summary>
    System,

    /// <summary>Always animate.</summary>
    On,

    /// <summary>Never animate.</summary>
    Off,
}
