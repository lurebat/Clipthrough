using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The main window dispatches its own keyboard handlers before falling through
/// to the user-configurable filter hotkeys, so a filter hotkey that matches a
/// built-in never fires where that built-in applies. These tests hold the line
/// on both halves of that: the shipped defaults must not collide, and a
/// collision must be detectable so settings validation can refuse it.
/// </summary>
public sealed class BuiltInShortcutsTests
{
    [Theory]
    [InlineData("Ctrl+D")]
    [InlineData("Ctrl+A")]
    [InlineData("Ctrl+Shift+C")]
    [InlineData("Ctrl+1")]
    [InlineData("Alt+9")]
    [InlineData("Delete")]
    public void DescribeCollision_ReportsGesturesTheWindowHandlesItself(string gesture)
    {
        Assert.False(string.IsNullOrWhiteSpace(BuiltInShortcuts.DescribeCollision(Normalize(gesture))));
    }

    [Theory]
    [InlineData("Ctrl+B")]
    [InlineData("Ctrl+R")]
    [InlineData("Ctrl+Alt+D")]
    [InlineData("Alt+V")]
    public void DescribeCollision_LeavesFreeGesturesAlone(string gesture)
    {
        Assert.Null(BuiltInShortcuts.DescribeCollision(Normalize(gesture)));
    }

    /// <summary>
    /// Validation compares canonicalised gesture text, so a reserved entry
    /// spelled with an alias ("Return" for Enter, "Esc" for Escape) would parse
    /// fine yet never match anything - a silent hole rather than a build break.
    /// </summary>
    [Fact]
    public void EveryReservedGestureIsAlreadyInCanonicalForm()
    {
        foreach (var (gesture, _) in BuiltInShortcuts.All)
        {
            Assert.True(
                HotkeyGesture.TryParse(gesture, out var parsed, out var error) && parsed is not null,
                $"Reserved gesture '{gesture}' does not parse ({error}), so it can never match anything.");
            Assert.Equal(parsed!.ToString(), gesture);
            Assert.NotNull(BuiltInShortcuts.DescribeCollision(parsed.ToString()));
        }
    }

    [Fact]
    public void DescribeCollision_IgnoresBlankGestures()
    {
        Assert.Null(BuiltInShortcuts.DescribeCollision(null));
        Assert.Null(BuiltInShortcuts.DescribeCollision("   "));
    }

    [Fact]
    public void EveryReservedGestureHasADescription()
    {
        foreach (var (gesture, description) in BuiltInShortcuts.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(description()),
                $"Reserved gesture '{gesture}' has no description, so the error message would name no action.");
        }
    }

    /// <summary>
    /// The structural guard. Ctrl+D shipped as the favorites default and was
    /// dead in the clip list - the window's normal focus state - for every
    /// install. Rather than pin that one gesture, assert the whole class: no
    /// shipped filter default may collide with a built-in.
    /// </summary>
    [Fact]
    public void NoShippedFilterHotkeyDefaultCollidesWithABuiltIn()
    {
        var collisions = new List<string>();
        foreach (var (name, value) in FilterHotkeyDefaults())
        {
            if (BuiltInShortcuts.DescribeCollision(Normalize(value)) is { } builtIn)
            {
                collisions.Add($"{name} = {value} (window uses it to {builtIn})");
            }
        }

        Assert.True(collisions.Count == 0, "Filter hotkey defaults that the window swallows first: " + string.Join("; ", collisions));
    }

    /// <summary>
    /// Changing the default only helps fresh installs. Anyone who already ran
    /// the app has Ctrl+D persisted, so the normalizer has to move it as well -
    /// otherwise the fix reaches nobody who is already affected.
    /// </summary>
    [Fact]
    public void Normalize_MovesTheStoredCtrlDFavoritesHotkeyOffTheBuiltIn()
    {
        var upgraded = (AppSettings.Default with { ToggleFavoritesHotkey = "Ctrl+D" }).Normalize();

        Assert.Equal(AppSettings.Default.ToggleFavoritesHotkey, upgraded.ToggleFavoritesHotkey);
        Assert.Null(BuiltInShortcuts.DescribeCollision(Normalize(upgraded.ToggleFavoritesHotkey)));
    }

    [Fact]
    public void Normalize_LeavesAFavoritesHotkeyTheUserActuallyChoseAlone()
    {
        var settings = (AppSettings.Default with { ToggleFavoritesHotkey = "Ctrl+Alt+F" }).Normalize();

        Assert.Equal("Ctrl+Alt+F", settings.ToggleFavoritesHotkey);
    }

    private static string Normalize(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out var parsed, out var error) && parsed is not null, error);
        return parsed!.ToString();
    }

    // Reflected rather than listed so a filter hotkey added later is covered
    // without anyone remembering to extend this test.
    private static IEnumerable<(string Name, string Value)> FilterHotkeyDefaults()
        => typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.PropertyType == typeof(string)
                && p.Name.StartsWith("Toggle", StringComparison.Ordinal)
                && p.Name.EndsWith("Hotkey", StringComparison.Ordinal)
                && p.Name != nameof(AppSettings.ToggleWindowHotkey))
            .Select(p => (p.Name, (string)p.GetValue(AppSettings.Default)!))
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Item2));
}
