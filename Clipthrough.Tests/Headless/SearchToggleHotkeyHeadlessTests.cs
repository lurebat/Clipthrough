using System;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Clipthrough.Models;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The in-window search toggles (regex, favourites, fuzzy, and the rest) are
/// bound to hotkeys the user types into settings as free text, and the whole
/// path from that string to the toggle had no tests.
///
/// It is the kind of code that fails quietly. An unparseable string, a modifier
/// compared the wrong way, or a layout that reports no key at all each produce
/// the same symptom - the hotkey does nothing - and nothing logs, so the user
/// concludes the feature is broken rather than that their gesture is.
/// </summary>
public sealed class SearchToggleHotkeyHeadlessTests
{
    private static MainWindowTestHarness CreateHarness(string regexHotkey, bool enabled = true)
        => MainWindowTestHarness.Create(settings => settings with
        {
            EnableToggleRegexHotkey = enabled,
            ToggleRegexHotkey = regexHotkey,
        });

    private static KeyEventArgs KeyPress(Key key, KeyModifiers modifiers, PhysicalKey physicalKey = PhysicalKey.None)
        => new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            PhysicalKey = physicalKey,
        };

    [AvaloniaFact]
    public void AConfiguredHotkey_TogglesItsSearchOption()
    {
        using var harness = CreateHarness("Ctrl+R");
        Assert.False(harness.ViewModel.UseRegexSearch);

        Assert.True(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.True(harness.ViewModel.UseRegexSearch);

        // A toggle, so the same gesture has to come back again.
        Assert.True(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    [AvaloniaFact]
    public void ADisabledHotkey_DoesNothing()
    {
        using var harness = CreateHarness("Ctrl+R", enabled: false);

        Assert.False(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// Extra modifiers must not match. Comparing with a flag test rather than
    /// equality would let Ctrl+Shift+R fire the Ctrl+R toggle, which then
    /// swallows whatever Ctrl+Shift+R was meant to do.
    /// </summary>
    [AvaloniaFact]
    public void AGestureWithAnExtraModifier_DoesNotMatch()
    {
        using var harness = CreateHarness("Ctrl+R");

        Assert.False(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    [AvaloniaFact]
    public void AGestureWithTheWrongModifier_DoesNotMatch()
    {
        using var harness = CreateHarness("Ctrl+R");

        Assert.False(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Alt)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// The setting is free text, so it will eventually hold something that is
    /// not a gesture. That has to be inert rather than throw: this runs inside
    /// the window's key handler, so an exception here takes out every other
    /// shortcut on the same keystroke.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Ctrl+")]
    [InlineData("NotAKey")]
    [InlineData("Ctrl+Shift+")]
    public void AnUnparseableHotkey_IsInertRatherThanFatal(string hotkey)
    {
        using var harness = CreateHarness(hotkey);

        Assert.False(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// Clearing a toggle hotkey restores its default rather than removing it.
    /// </summary>
    /// <remarks>
    /// Found by writing a test that assumed the opposite: an empty string was
    /// offered to the "unparseable gestures are inert" case, and the hotkey kept
    /// working, because <see cref="AppSettings.Normalize"/> maps a blank toggle
    /// hotkey back to its default. Only the optional hotkeys (incremental paste
    /// and friends) are allowed to normalize to empty.
    ///
    /// Worth pinning precisely because it is surprising. A user who clears the
    /// box to switch a shortcut off gets it back, and the only thing that
    /// actually disables it is the checkbox beside it. If that ever changes,
    /// blank hotkeys start reaching KeyGesture.Parse, which is the path this
    /// class's other tests cover.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearingAToggleHotkey_RestoresItsDefaultRatherThanDisablingIt(string cleared)
    {
        using var harness = CreateHarness(cleared);

        Assert.Equal("Ctrl+R", harness.Settings.Current.ToggleRegexHotkey);
        Assert.True(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.True(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// A keyboard layout that reports no <see cref="Key"/> still has to work.
    /// </summary>
    /// <remarks>
    /// This is the branch that exists for non-Latin layouts, where the layout
    /// can leave Key as None while PhysicalKey still identifies the position.
    /// Asaf reported layout-dependent input trouble at the start of this work,
    /// and this fallback is the only place the code accounts for it - untested
    /// until now, which is the worst combination for a path that only a user on
    /// another layout can reach.
    /// </remarks>
    [AvaloniaFact]
    public void AKeyThatOnlyReportsItsPhysicalPosition_StillMatches()
    {
        using var harness = CreateHarness("Ctrl+R");

        Assert.True(harness.ViewModel.TryHandleShortcut(
            KeyPress(Key.None, KeyModifiers.Control, PhysicalKey.R)));
        Assert.True(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// And the fallback must not fire for a different physical key, or every
    /// unmapped keystroke on such a layout would toggle something.
    ///
    /// Q rather than the first letter that came to mind: Ctrl+T is the fuzzy
    /// toggle by default, so the obvious choice made this pass through a real
    /// hotkey firing rather than through the fallback declining.
    /// </summary>
    [AvaloniaFact]
    public void TheFallbackDoesNotMatchADifferentPhysicalKey()
    {
        using var harness = CreateHarness("Ctrl+R");

        Assert.False(harness.ViewModel.TryHandleShortcut(
            KeyPress(Key.None, KeyModifiers.Control, PhysicalKey.Q)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }

    /// <summary>
    /// While a modal is up the keystroke belongs to the modal. Settings is the
    /// case that matters: the hotkey capture box is in there, so without this a
    /// user typing a new gesture would trip the old one as they typed it.
    /// </summary>
    [AvaloniaFact]
    public void AModalSwallowsTheShortcut()
    {
        using var harness = CreateHarness("Ctrl+R");
        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Assert.True(harness.ViewModel.IsSettingsOpen, "settings did not open, so the guard was never exercised");

        Assert.False(harness.ViewModel.TryHandleShortcut(KeyPress(Key.R, KeyModifiers.Control)));
        Assert.False(harness.ViewModel.UseRegexSearch);
    }
}
