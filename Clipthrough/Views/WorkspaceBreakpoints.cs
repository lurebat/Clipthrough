using Avalonia;

namespace Clipthrough.Views;

internal enum WorkspaceWidth
{
    Wide,
    Medium,
    Narrow,
}

/// <summary>
/// Which layout the main window uses for a given client size.
/// (docs/plans/2026-10-28-001-multi-monitor-fit-design.md)
/// </summary>
/// <remarks>
/// Wide is the layout Clipthrough has always had. Medium keeps the list beside
/// the preview but drops the title and moves the search modifiers to their own
/// row. Narrow stacks the preview under the list, which is what a portrait
/// screen has room for. Short, independent of width, collapses the search
/// modifiers into a button and hides the footer and the counters, so a
/// 1280x720 laptop at 150% still shows the clip list.
/// </remarks>
internal static class WorkspaceBreakpoints
{
    /// <summary>The old window minimum: at or above it nothing is rearranged.</summary>
    public const double MediumBelowWidth = 1100;

    public const double NarrowBelowWidth = 760;

    public const double ShortBelowHeight = 600;

    /// <summary>The window minimum. Every breakpoint keeps its controls reachable down to it.</summary>
    public const double MinWidth = 480;

    public const double MinHeight = 360;

    /// <summary>Widest the search box grows. (B18)</summary>
    public const double SearchMaxWidth = 960;

    /// <summary>Widest the five type toggles grow together. (B18)</summary>
    public const double TypeFilterMaxWidth = 900;

    /// <summary>Widest the preview grows beside the list. (B18)</summary>
    public const double PreviewMaxWidth = 1600;

    /// <summary>What the welcome screen's two columns need side by side.</summary>
    public const double WelcomeTwoColumnMinWidth = 1024;

    public static (WorkspaceWidth Width, bool Short) Classify(Size clientSize)
    {
        var width = clientSize.Width >= MediumBelowWidth ? WorkspaceWidth.Wide
            : clientSize.Width >= NarrowBelowWidth ? WorkspaceWidth.Medium
            : WorkspaceWidth.Narrow;
        return (width, clientSize.Height < ShortBelowHeight);
    }
}
