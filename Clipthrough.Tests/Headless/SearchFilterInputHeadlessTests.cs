using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Regression coverage for type-to-filter on non-Latin keyboard layouts.
///
/// The redirect used to run on KeyDown and synthesise a character arithmetically
/// from <c>Avalonia.Input.Key</c>. That enum is the layout-independent virtual
/// key code, so the physical A key reports <c>Key.A</c> on Hebrew, Russian and
/// Arabic layouts too — the first character of every filter came out Latin.
/// Worse, the handler marked the KeyDown handled, which suppressed the platform
/// TextInput event that would otherwise have corrected it. Focus then moved to
/// the search box, so every *subsequent* character arrived natively and was
/// correct: the reported "first letter in English, the rest in the language".
///
/// These tests drive TextInput directly, which is what the platform delivers
/// after applying the active layout, dead keys, AltGr and IME composition.
/// </summary>
public sealed class SearchFilterInputHeadlessTests
{
    [AvaloniaTheory]
    [InlineData("ש")]      // Hebrew
    [InlineData("ф")]      // Russian
    [InlineData("ش")]      // Arabic
    [InlineData("a")]      // Latin must keep working
    [InlineData("é")]      // dead-key composition
    public void TypeToFilter_UsesPlatformTextForAnyLayout(string typed)
    {
        using var harness = MainWindowTestHarness.Create();

        harness.FocusClipList();
        harness.Window.KeyTextInput(typed);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(typed, harness.ViewModel.SearchText);
    }

    /// <summary>
    /// The exact reported symptom: the first character was Latin while the rest
    /// of the word came through correctly.
    /// </summary>
    [AvaloniaFact]
    public void TypeToFilter_FirstCharacterMatchesTheRestOfTheWord()
    {
        using var harness = MainWindowTestHarness.Create();

        harness.FocusClipList();
        foreach (var character in "שלום")
        {
            harness.Window.KeyTextInput(character.ToString());
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("שלום", harness.ViewModel.SearchText);
    }

    [AvaloniaFact]
    public void TypeToFilter_MovesFocusToSearchBox()
    {
        using var harness = MainWindowTestHarness.Create();

        harness.FocusClipList();
        harness.Window.KeyTextInput("ש");
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.SearchBox.IsKeyboardFocusWithin);
    }

    [AvaloniaTheory]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\u001b")]  // Escape
    [InlineData("\u0003")]  // Ctrl+C
    public void TypeToFilter_IgnoresControlCharacters(string typed)
    {
        using var harness = MainWindowTestHarness.Create();

        harness.FocusClipList();
        harness.Window.KeyTextInput(typed);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, harness.ViewModel.SearchText ?? string.Empty);
    }

    /// <summary>
    /// Typing inside the search box must append normally rather than be
    /// re-redirected and duplicated.
    /// </summary>
    [AvaloniaFact]
    public void TypeToFilter_DoesNotRedirectWhenSearchBoxAlreadyFocused()
    {
        using var harness = MainWindowTestHarness.Create();

        harness.SearchBox.Focus();
        Dispatcher.UIThread.RunJobs();
        harness.Window.KeyTextInput("ש");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ש", harness.SearchBox.Text);
    }

    /// <summary>
    /// The KeyDown half of the chain. Avalonia's headless backend does not
    /// synthesise TextInput from <c>KeyPress</c> (verified: pressing a key with
    /// key symbol "a" into a focused TextBox leaves it empty), so the two halves
    /// are asserted separately — <c>KeyTextInput</c> above covers "the platform's
    /// character reaches the filter", and this covers "the platform still gets to
    /// produce that character". On Windows a handled WM_KEYDOWN suppresses the
    /// following WM_CHAR, so if anything here started marking printable keys
    /// handled, type-to-filter would go silent.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.A, PhysicalKey.A, "a")]
    [InlineData(Key.Z, PhysicalKey.Z, "z")]
    [InlineData(Key.D5, PhysicalKey.Digit5, "5")]
    [InlineData(Key.Space, PhysicalKey.Space, " ")]
    public void PrintableKeyDown_IsLeftUnhandledSoPlatformEmitsTextInput(Key key, PhysicalKey physicalKey, string keySymbol)
    {
        using var harness = MainWindowTestHarness.Create();

        bool? handledAtWindow = null;
        harness.Window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) => handledAtWindow = e.Handled,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        harness.FocusClipList();
        harness.Window.KeyPress(key, RawInputModifiers.None, physicalKey, keySymbol);
        Dispatcher.UIThread.RunJobs();

        Assert.False(handledAtWindow);
    }
}
