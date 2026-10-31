namespace Clipthrough.Models;

/// <summary>What the first start after an upgrade is doing, for the loading overlay.</summary>
public enum LibraryUpgradeStage
{
    CheckingIntegrity,
    UpdatingStructure,
    RebuildingSearchIndex,
    MovingClipData,
    Finishing,
}

/// <param name="Stage">The stage now running.</param>
/// <param name="Fraction">How far through the stage, 0 to 1, or null when it cannot tell.</param>
public sealed record LibraryUpgradeProgress(LibraryUpgradeStage Stage, double? Fraction = null);
