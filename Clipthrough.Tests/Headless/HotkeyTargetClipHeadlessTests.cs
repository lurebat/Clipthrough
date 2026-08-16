using Avalonia.Headless.XUnit;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A global hotkey can fire while the popup is hidden. Hiding deliberately
/// leaves <see cref="MainWindowViewModel.SelectedClip"/> in place, so anything
/// reading the selection directly would transform a clip the user last touched
/// hours ago instead of the one they just copied.
/// </summary>
public sealed class HotkeyTargetClipHeadlessTests
{
    [AvaloniaFact]
    public void WhileVisible_TheSelectionIsTheHotkeyTarget()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.ViewModel.SetMainWindowVisible(true);

        var chosen = harness.ViewModel.Clips[2];
        harness.ViewModel.SelectedClip = chosen;

        Assert.Same(chosen.Clip, harness.ViewModel.HotkeyTargetClip);
    }

    /// <summary>
    /// The regression: the selection survives hiding, but it must stop being a
    /// hotkey target the moment the list leaves the screen.
    /// </summary>
    [AvaloniaFact]
    public void WhileHidden_TheStaleSelectionIsNotAHotkeyTarget()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.ViewModel.SetMainWindowVisible(true);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[2];

        harness.ViewModel.SetMainWindowVisible(false);

        // The selection is still there - that is the trap the gate exists for.
        Assert.NotNull(harness.ViewModel.SelectedClip);
        Assert.Null(harness.ViewModel.HotkeyTargetClip);
    }
}
