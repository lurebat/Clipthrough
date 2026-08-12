using System;
using System.Collections.Generic;
using Clipthrough.Localization;

namespace Clipthrough.Models;

/// <summary>
/// Gestures the main window handles itself, before the user-configurable filter
/// hotkeys get a chance to run.
///
/// <c>MainWindow.OnKeyDown</c> dispatches its built-in handlers first and only
/// falls through to <c>MainWindowViewModel.TryHandleShortcut</c> if none of them
/// claimed the key. A configured hotkey that matches one of these is therefore
/// dead wherever the built-in applies - which for the clip-list shortcuts is the
/// window's normal focus state. Settings validation rejects such an assignment
/// instead of letting the user configure a shortcut that silently does nothing.
///
/// Gestures are written in <see cref="HotkeyGesture"/> parse syntax and must be
/// spelled in that type's canonical form, since that is the form settings
/// validation compares against.
/// </summary>
internal static class BuiltInShortcuts
{
    public static IReadOnlyList<(string Gesture, Func<string> Description)> All { get; } = BuildAll();

    private static IReadOnlyList<(string, Func<string>)> BuildAll()
    {
        var shortcuts = new List<(string, Func<string>)>
        {
            ("Ctrl+A", static () => AppText.BuiltInShortcutSelectAllClips),
            ("Ctrl+C", static () => AppText.BuiltInShortcutCopySelected),
            ("Ctrl+D", static () => AppText.BuiltInShortcutCopySelected),
            ("Ctrl+Shift+C", static () => AppText.BuiltInShortcutCopySelectedAsPlainText),
            ("Ctrl+V", static () => AppText.BuiltInShortcutPasteSelected),
            ("Ctrl+Shift+V", static () => AppText.BuiltInShortcutPasteSelected),
            // "Return", not "Enter": Avalonia's Key.Enter is an alias of
            // Key.Return, so the canonical text a parsed gesture renders to is
            // "Return". Users can still type either - both parse to this key.
            ("Return", static () => AppText.BuiltInShortcutPasteSelected),
            ("Delete", static () => AppText.BuiltInShortcutDeleteSelected),
            ("Space", static () => AppText.BuiltInShortcutToggleClipChecked),
            ("Escape", static () => AppText.BuiltInShortcutClearFilter),
            ("Ctrl+Comma", static () => AppText.BuiltInShortcutOpenSettings),
            ("Up", static () => AppText.BuiltInShortcutMoveThroughClips),
            ("Down", static () => AppText.BuiltInShortcutMoveThroughClips),
        };

        for (var digit = 1; digit <= 9; digit++)
        {
            shortcuts.Add(($"Ctrl+{digit}", static () => AppText.BuiltInShortcutCopyClipByPosition));
            shortcuts.Add(($"Alt+{digit}", static () => AppText.BuiltInShortcutSelectClipByPosition));
        }

        return shortcuts;
    }

    /// <summary>
    /// Returns the description of the built-in that <paramref name="gesture"/>
    /// collides with, or null when it is free. <paramref name="gesture"/> must
    /// already be normalised (<see cref="HotkeyGesture.ToString"/>), which is
    /// what settings validation compares with everywhere else; the table above
    /// is held in that same canonical form by
    /// <c>BuiltInShortcutsTests.EveryReservedGestureIsAlreadyInCanonicalForm</c>.
    /// </summary>
    public static string? DescribeCollision(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        foreach (var (reserved, description) in All)
        {
            if (string.Equals(reserved, gesture, StringComparison.OrdinalIgnoreCase))
            {
                return description();
            }
        }

        return null;
    }
}
