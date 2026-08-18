using System;

using Clipthrough.ViewModels;

using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// Settings validation, exercised without touching storage.
///
/// Every rule here is a refusal the user can hit, and until this was split out
/// of <c>SaveSettingsAsync</c> none of them could be reached without also
/// running a database move and a credential rewrite. That is why they were
/// almost entirely uncovered.
/// </summary>
public sealed class SettingsDraftValidatorTests
{
    private static SettingsViewModel ValidDraft() => new()
    {
        MaxClipSizeKilobytes = "64",
        EnableNormalClipLifetime = false,
        EnableSensitiveClipLifetime = false,
        EnableMaxLibrarySize = false,
        EnableMaxEntryCount = false,
    };

    private static (bool Ok, ValidatedSettingsDraft? Result, string? Error) Validate(SettingsViewModel draft)
    {
        var ok = SettingsDraftValidator.TryValidate(draft, out var result, out var error);
        return (ok, result, error);
    }

    [Fact]
    public void ADraftWithNothingWrongWithItPasses()
    {
        var (ok, result, error) = Validate(ValidDraft());

        Assert.True(ok, error);
        Assert.NotNull(result);
        Assert.Null(error);
    }

    [Fact]
    public void AnEnabledHotkeyThatWillNotParseIsRefused()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = true;
        draft.ToggleRegexHotkey = "this is not a gesture";

        var (ok, result, error) = Validate(draft);

        Assert.False(ok);
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// The same text on a hotkey that is switched off is not an error, because
    /// nothing will ever try to register it. Pairs with the test above: together
    /// they show the refusal is about the hotkey being live, not about the text.
    /// </summary>
    [Fact]
    public void TheSameUnparseableTextIsIgnoredWhileTheHotkeyIsDisabled()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = false;
        draft.ToggleRegexHotkey = "this is not a gesture";

        var (ok, _, error) = Validate(draft);

        Assert.True(ok, error);
    }

    [Fact]
    public void TwoEnabledHotkeysCannotClaimTheSameGesture()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = true;
        draft.ToggleRegexHotkey = "Ctrl+Shift+G";
        draft.EnableToggleFavoritesHotkey = true;
        draft.ToggleFavoritesHotkey = "Ctrl+Shift+G";

        var (ok, _, error) = Validate(draft);

        Assert.False(ok);
        Assert.Contains("Ctrl+Shift+G", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Anti-vacuity for the duplicate rule: it must be about the collision, not
    /// about having two hotkeys enabled at once.
    /// </summary>
    [Fact]
    public void TwoEnabledHotkeysWithDifferentGesturesAreFine()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = true;
        draft.ToggleRegexHotkey = "Ctrl+Shift+G";
        draft.EnableToggleFavoritesHotkey = true;
        draft.ToggleFavoritesHotkey = "Ctrl+Shift+H";

        var (ok, _, error) = Validate(draft);

        Assert.True(ok, error);
    }

    /// <summary>
    /// A disabled hotkey holding the same gesture as an enabled one does not
    /// collide, because it never fires. Without this the duplicate check could
    /// be looking at the raw list rather than the live one.
    /// </summary>
    [Fact]
    public void ADisabledHotkeyDoesNotCollideWithAnEnabledOne()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = true;
        draft.ToggleRegexHotkey = "Ctrl+Shift+G";
        draft.EnableToggleFavoritesHotkey = false;
        draft.ToggleFavoritesHotkey = "Ctrl+Shift+G";

        var (ok, _, error) = Validate(draft);

        Assert.True(ok, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("999999999")]
    public void AClipSizeOutsideItsBoundsIsRefused(string kilobytes)
    {
        var draft = ValidDraft();
        draft.MaxClipSizeKilobytes = kilobytes;

        var (ok, _, _) = Validate(draft);

        Assert.False(ok);
    }

    [Fact]
    public void AnEnabledLimitWithAnUnparseableValueIsRefused()
    {
        var draft = ValidDraft();
        draft.EnableMaxEntryCount = true;
        draft.MaxEntryCount = "lots";

        var (ok, _, _) = Validate(draft);

        Assert.False(ok);
    }

    /// <summary>
    /// And the same rubbish is accepted while the limit is off, which is what
    /// makes the check about the limit being live rather than about the text.
    /// </summary>
    [Fact]
    public void TheSameUnparseableLimitIsIgnoredWhileTheLimitIsDisabled()
    {
        var draft = ValidDraft();
        draft.EnableMaxEntryCount = false;
        draft.MaxEntryCount = "lots";

        var (ok, _, error) = Validate(draft);

        Assert.True(ok, error);
    }

    /// <summary>
    /// Validation normalizes as well as accepts, and the caller writes what it
    /// returns. A gesture typed in a different case or spacing has to come back
    /// in canonical form or the saved settings disagree with what was checked.
    /// </summary>
    [Fact]
    public void AnAcceptedHotkeyComesBackNormalized()
    {
        var draft = ValidDraft();
        draft.EnableToggleRegexHotkey = true;
        draft.ToggleRegexHotkey = "  ctrl+shift+g  ";

        var (ok, result, error) = Validate(draft);

        Assert.True(ok, error);
        var normalized = result!.LocalHotkeys["ToggleRegexHotkey"];
        Assert.Equal("Ctrl+Shift+G", normalized);
        Assert.NotEqual(draft.ToggleRegexHotkey, normalized);
    }

    [Fact]
    public void ValidationRejectsANullDraftRatherThanTreatingItAsEmpty()
        => Assert.Throws<ArgumentNullException>(
            () => SettingsDraftValidator.TryValidate(null!, out _, out _));
}
