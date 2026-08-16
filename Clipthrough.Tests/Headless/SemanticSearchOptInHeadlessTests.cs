using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Semantic search is behind two switches: the Settings one that builds the
/// index, and a "Semantic" checkbox on the search bar that decides whether
/// results are actually fused. Enabling the first used to leave the second off,
/// so the user paid for a full embedding pass and got identical results.
/// </summary>
public sealed class SemanticSearchOptInHeadlessTests
{
    [AvaloniaFact]
    public void EnablingSemanticSearch_OptsTheSearchBoxIn()
    {
        using var harness = MainWindowTestHarness.Create();
        Assert.False(harness.ViewModel.UseSemanticClipSearch);

        harness.Settings.SetCurrent(harness.Settings.Current with { EnableSemanticSearch = true });

        Assert.True(harness.ViewModel.UseSemanticClipSearch);
    }

    /// <summary>
    /// Only the off-to-on edge opts in. A later save that leaves the feature on
    /// must not undo a user who deliberately unticked the search-bar checkbox.
    /// </summary>
    [AvaloniaFact]
    public void ASaveThatLeavesSemanticEnabled_DoesNotReTickTheCheckbox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.Settings.SetCurrent(harness.Settings.Current with { EnableSemanticSearch = true });
        Assert.True(harness.ViewModel.UseSemanticClipSearch);

        harness.ViewModel.UseSemanticClipSearch = false;
        harness.Settings.SetCurrent(harness.Settings.Current with { MaxClipSizeBytes = 8192 });

        Assert.False(harness.ViewModel.UseSemanticClipSearch);
    }

    /// <summary>
    /// Starting up with the feature already on is not the user enabling it, so
    /// it must not override the persisted search-bar choice either.
    /// </summary>
    [AvaloniaFact]
    public void StartingWithSemanticAlreadyEnabled_LeavesThePersistedChoiceAlone()
    {
        using var harness = MainWindowTestHarness.Create(s => s with
        {
            EnableSemanticSearch = true,
            LastUseSemanticClipSearch = false,
        });

        Assert.False(harness.ViewModel.UseSemanticClipSearch);

        harness.Settings.SetCurrent(harness.Settings.Current with { MaxClipSizeBytes = 8192 });

        Assert.False(harness.ViewModel.UseSemanticClipSearch);
    }
}
