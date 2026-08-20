using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Avalonia.Headless.XUnit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The encrypted-database password prompt is an in-window overlay, not a
/// separate window, so the search box it covers is still present, enabled and
/// focusable underneath it. Two independent defects met there: nothing focused
/// the password field, and the type-to-filter redirect had no modal guard - so
/// the password was typed in cleartext into a TwoWay-bound search filter that
/// feeds RecentSearches, and never reached the password field at all.
/// (round 2, bugs-opus F3)
/// </summary>
public sealed class PasswordPromptInputHeadlessTests
{
    private static TextBox PasswordBox(MainWindowTestHarness harness)
        => harness.Window.GetVisualDescendants().OfType<TextBox>()
            .First(box => box.Name == "PasswordPromptTextBox");

    private static void OpenThePrompt(MainWindowTestHarness harness)
    {
        harness.ViewModel.OpenPasswordPromptForTests();
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            harness.ViewModel.IsPasswordPromptOpen,
            "the prompt did not open, so nothing below is about the prompt");
    }

    [AvaloniaFact]
    public void OpeningThePrompt_FocusesThePasswordField()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);

        OpenThePrompt(harness);

        Assert.True(
            PasswordBox(harness).IsKeyboardFocusWithin,
            "the password field was not focused, so the first keystroke has no home");
    }

    /// <summary>
    /// The guard is tested with focus deliberately moved OFF the password field
    /// but still inside the overlay. With the field focused the redirect already
    /// declines, because the event source is a text input - so a test that let
    /// the focus fix stand would pass with the guard removed and prove nothing
    /// about it.
    /// </summary>
    /// <remarks>
    /// The "Show password" checkbox rather than the clip list. The list used to
    /// serve here, but binding IsMainWorkspaceVisible hid the whole workspace
    /// behind a modal overlay, so focus can no longer land there at all - and
    /// this test's own guard against proving nothing is what caught that. The
    /// checkbox is better anyway: it is a non-text control a real user reaches
    /// by pressing Tab once, which is exactly the state the guard is for.
    /// </remarks>
    [AvaloniaFact]
    public void TypingWithThePromptOpenAndFocusElsewhere_DoesNotReachTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        OpenThePrompt(harness);

        var revealToggle = harness.Window.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(box => box.Content as string == "Show password");
        revealToggle.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(revealToggle.IsKeyboardFocusWithin, "the checkbox did not take focus; the redirect is not under test");
        Assert.False(
            PasswordBox(harness).IsKeyboardFocusWithin,
            "focus is still on the password field, so the redirect would decline for the wrong reason");

        harness.Window.KeyTextInput("h");
        harness.Window.KeyTextInput("u");
        harness.Window.KeyTextInput("n");
        harness.Window.KeyTextInput("t");
        harness.Window.KeyTextInput("e");
        harness.Window.KeyTextInput("r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, harness.ViewModel.SearchText ?? string.Empty);
        Assert.False(
            harness.SearchBox.IsKeyboardFocusWithin,
            "the covered search box took focus from behind the modal overlay");
    }

    /// <summary>
    /// The workspace is not merely covered while a modal overlay is up - it is
    /// hidden, so nothing behind the overlay is hit-testable or in the tab order.
    /// The overlays are siblings that stack on top rather than replacing it, so
    /// without this the menu, search box and clip list stayed reachable.
    /// (round 2, bugs-opus F4)
    /// </summary>
    [AvaloniaFact]
    public void WhileThePromptIsOpen_TheWorkspaceBehindItIsNotReachable()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);

        Assert.True(harness.ViewModel.IsMainWorkspaceVisible);
        Assert.True(harness.SearchBox.IsEffectivelyVisible, "the search box was already unreachable, so the test proves nothing");

        OpenThePrompt(harness);

        Assert.False(harness.ViewModel.IsMainWorkspaceVisible);
        Assert.False(harness.SearchBox.IsEffectivelyVisible, "the search box is still reachable behind the modal overlay");
        Assert.False(harness.ClipList.IsEffectivelyVisible, "the clip list is still reachable behind the modal overlay");
    }

    /// <summary>
    /// The control. Hiding the workspace must be tied to the overlay and not
    /// permanent, or the fix costs the application its entire UI.
    /// </summary>
    [AvaloniaFact]
    public void WithNoOverlayOpen_TheWorkspaceIsReachable()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);

        Assert.True(harness.ViewModel.IsMainWorkspaceVisible);
        Assert.True(harness.SearchBox.IsEffectivelyVisible);
        Assert.True(harness.ClipList.IsEffectivelyVisible);
    }

    /// <summary>
    /// The guard must not outlive the overlay: closing the prompt has to give
    /// type-to-filter back, or the fix trades a leak for a dead feature.
    /// </summary>
    [AvaloniaFact]
    public void TypingWithNoPromptOpen_StillReachesTheSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(5);
        harness.FocusClipList();

        harness.Window.KeyTextInput("h");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("h", harness.ViewModel.SearchText);
    }
}
