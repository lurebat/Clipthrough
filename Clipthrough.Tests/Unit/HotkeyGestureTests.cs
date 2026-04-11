using Avalonia.Input;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void TryParse_NormalizesDisplayTextAndWindowsRegistration()
    {
        var parsed = HotkeyGesture.TryParse("ctrl+shift+r", out var hotkey, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(hotkey);
        Assert.Equal("Ctrl+Shift+R", hotkey!.ToString());
        Assert.True(hotkey.TryGetWindowsRegistration(out var modifiers, out var virtualKey));
        Assert.Equal(0x0002u | 0x0004u, modifiers);
        Assert.Equal(0x52u, virtualKey);
    }

    [Fact]
    public void TryParse_RejectsMultipleNonModifierKeys()
    {
        var parsed = HotkeyGesture.TryParse("Alt+R+F", out var hotkey, out var error);

        Assert.False(parsed);
        Assert.Null(hotkey);
        Assert.Equal("Only one non-modifier key can be used in a hotkey.", error);
    }

    [Fact]
    public void TryParse_SupportsAliasKeys()
    {
        var parsed = HotkeyGesture.TryParse("Alt+Plus", out var hotkey, out _);

        Assert.True(parsed);
        Assert.Equal(new HotkeyGesture(Key.OemPlus, KeyModifiers.Alt), hotkey);
    }
}
