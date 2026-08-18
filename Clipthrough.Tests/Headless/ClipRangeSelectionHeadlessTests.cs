using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.ViewModels;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Ctrl-click and shift-click over the clip list.
///
/// This is anchor-based range selection with inclusive bounds, a direction that
/// can go either way, a preserve flag, and two fallbacks when the anchor has
/// gone - and none of it had a test. It is the kind of code that is wrong by one
/// row for months, because a range that is nearly right looks like the user
/// mis-clicked.
/// </summary>
public sealed class ClipRangeSelectionHeadlessTests
{
    private static MainWindowTestHarness ListOfSix()
    {
        var harness = MainWindowTestHarness.Create();
        harness.SeedClips(6);
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    private static string CheckedPattern(MainWindowViewModel vm)
        => string.Concat(vm.Clips.Select(c => c.IsChecked ? "X" : "."));

    [AvaloniaFact]
    public void ShiftClickChecksTheWholeRangeInclusiveOfBothEnds()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.ToggleClipCheckedSelection(vm.Clips[1]);
        vm.ExtendClipCheckedSelection(vm.Clips[3], preserveExistingSelection: false);

        Assert.Equal(".XXX..", CheckedPattern(vm));
    }

    /// <summary>
    /// Dragging a range upwards has to select the same rows as dragging it down.
    /// </summary>
    [AvaloniaFact]
    public void TheRangeIsTheSameWhicheverEndIsClickedFirst()
    {
        using var harnessDown = ListOfSix();
        var down = harnessDown.ViewModel;
        down.ToggleClipCheckedSelection(down.Clips[1]);
        down.ExtendClipCheckedSelection(down.Clips[4], preserveExistingSelection: false);

        using var harnessUp = ListOfSix();
        var up = harnessUp.ViewModel;
        up.ToggleClipCheckedSelection(up.Clips[4]);
        up.ExtendClipCheckedSelection(up.Clips[1], preserveExistingSelection: false);

        Assert.Equal(".XXXX.", CheckedPattern(down));
        Assert.Equal(CheckedPattern(down), CheckedPattern(up));
    }

    [AvaloniaFact]
    public void ExtendingWithoutPreservingClearsWhatWasCheckedBefore()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.ToggleClipCheckedSelection(vm.Clips[5]);
        vm.ToggleClipCheckedSelection(vm.Clips[0]);
        vm.ExtendClipCheckedSelection(vm.Clips[1], preserveExistingSelection: false);

        // The 5 that was checked first is gone; only the new range survives.
        Assert.Equal("XX....", CheckedPattern(vm));
    }

    [AvaloniaFact]
    public void ExtendingWithPreservingKeepsWhatWasCheckedBefore()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.ToggleClipCheckedSelection(vm.Clips[5]);
        vm.ToggleClipCheckedSelection(vm.Clips[0]);
        vm.ExtendClipCheckedSelection(vm.Clips[1], preserveExistingSelection: true);

        Assert.Equal("XX...X", CheckedPattern(vm));
    }

    /// <summary>
    /// The anchor is where the last ctrl-click landed, not where the previous
    /// shift-click ended. This is what lets a range shrink: without it every
    /// range pivots on its own far end, so the selection can only ever grow and
    /// there is no way back to a smaller one. It was the bug this test found.
    /// </summary>
    [AvaloniaFact]
    public void ASecondShiftClickExtendsFromTheAnchorRatherThanFromTheLastTarget()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.ToggleClipCheckedSelection(vm.Clips[2]);
        vm.ExtendClipCheckedSelection(vm.Clips[4], preserveExistingSelection: false);
        Assert.Equal("..XXX.", CheckedPattern(vm));

        vm.ExtendClipCheckedSelection(vm.Clips[0], preserveExistingSelection: false);

        Assert.Equal("XXX...", CheckedPattern(vm));
    }

    /// <summary>
    /// With no anchor at all, the selected clip stands in for one - otherwise a
    /// shift-click with nothing ctrl-clicked first would select a single row and
    /// look broken.
    /// </summary>
    [AvaloniaFact]
    public void WithNoAnchorTheSelectedClipActsAsOne()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.SelectedClip = vm.Clips[4];
        Dispatcher.UIThread.RunJobs();

        vm.ExtendClipCheckedSelection(vm.Clips[2], preserveExistingSelection: false);

        Assert.Equal("..XXX.", CheckedPattern(vm));
    }

    [AvaloniaFact]
    public void AClipThatIsNotInTheListIsIgnoredRatherThanCheckingEverything()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;
        var stranger = new ClipItemViewModel(new Clipthrough.Models.ClipEntry { Id = 9999, Content = "elsewhere" });

        vm.ToggleClipCheckedSelection(vm.Clips[1]);
        vm.ExtendClipCheckedSelection(stranger, preserveExistingSelection: false);

        // Unchanged: only the ctrl-clicked row.
        Assert.Equal(".X....", CheckedPattern(vm));
    }

    [AvaloniaFact]
    public void CtrlClickingTheSameRowTwiceUnchecksIt()
    {
        using var harness = ListOfSix();
        var vm = harness.ViewModel;

        vm.ToggleClipCheckedSelection(vm.Clips[3]);
        Assert.Equal("...X..", CheckedPattern(vm));

        vm.ToggleClipCheckedSelection(vm.Clips[3]);
        Assert.Equal("......", CheckedPattern(vm));
    }
}
