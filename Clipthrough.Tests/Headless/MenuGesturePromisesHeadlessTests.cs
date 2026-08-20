using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A menu must not advertise a shortcut the application answers by doing
/// something else.
/// </summary>
/// <remarks>
/// "Toggle favorite" and "Toggle pin" carried InputGesture="F" and "P". Neither
/// was ever handled: TryHandleClipListShortcuts claims only Ctrl+A, Ctrl+Shift+C,
/// Ctrl+D, Delete and Space. And they could not be added, because an unmodified
/// letter with the clip list focused is type-to-filter - pressing F puts "f" in
/// the search box, which is the opposite of harmless.
///
/// The rule is therefore structural rather than about those two items: no menu
/// entry may advertise a bare single-character gesture, because every one of
/// them is already spoken for. (round 2, bugs-opus F7)
/// </remarks>
public sealed class MenuGesturePromisesHeadlessTests
{
    [AvaloniaFact]
    public void NoMenuItem_AdvertisesABareSingleLetterShortcut()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);

        var offenders = harness.Window.GetLogicalDescendants()
            .OfType<MenuItem>()
            .Where(item => item.InputGesture is { } gesture
                && gesture.KeyModifiers == Avalonia.Input.KeyModifiers.None
                && gesture.Key.ToString().Length == 1)
            .Select(item => $"{item.Header} -> {item.InputGesture}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These menu items promise a bare letter shortcut, which type-to-filter claims first: "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// The control: gestures that do exist must still be advertised, or the rule
    /// above is satisfied by a menu that documents nothing.
    /// </summary>
    [AvaloniaFact]
    public void RealShortcuts_AreStillAdvertised()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);

        var gestures = harness.Window.GetLogicalDescendants()
            .OfType<MenuItem>()
            .Where(item => item.InputGesture is not null)
            .Select(item => item.InputGesture!.ToString())
            .ToArray();

        Assert.Contains(gestures, g => g.Contains("Ctrl+D", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gestures, g => g.Contains("Ctrl+Shift+C", System.StringComparison.OrdinalIgnoreCase));
    }
}
