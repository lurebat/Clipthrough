using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Input;

namespace Clipthrough.Models;

public sealed record HotkeyGesture(Key Key, KeyModifiers Modifiers)
{
    private static readonly IReadOnlyDictionary<string, Key> KeyAliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
    {
        ["PLUS"] = Key.OemPlus,
        ["MINUS"] = Key.OemMinus,
        ["COMMA"] = Key.OemComma,
        ["PERIOD"] = Key.OemPeriod,
        ["DOT"] = Key.OemPeriod,
        ["SLASH"] = Key.Oem2,
        ["QUESTION"] = Key.Oem2,
        ["SEMICOLON"] = Key.Oem1,
        ["QUOTE"] = Key.Oem7,
        ["APOSTROPHE"] = Key.Oem7,
        ["OPENBRACKET"] = Key.Oem4,
        ["CLOSEBRACKET"] = Key.Oem6,
        ["BACKSLASH"] = Key.Oem5,
        ["SPACE"] = Key.Space,
        ["TAB"] = Key.Tab,
        ["ENTER"] = Key.Enter,
        ["RETURN"] = Key.Enter,
        ["ESC"] = Key.Escape,
        ["ESCAPE"] = Key.Escape,
        ["DELETE"] = Key.Delete,
        ["BACKSPACE"] = Key.Back,
        ["UP"] = Key.Up,
        ["DOWN"] = Key.Down,
        ["LEFT"] = Key.Left,
        ["RIGHT"] = Key.Right,
        ["HOME"] = Key.Home,
        ["END"] = Key.End,
        ["PAGEUP"] = Key.PageUp,
        ["PAGEDOWN"] = Key.PageDown,
        ["INSERT"] = Key.Insert,
    };

    public bool Matches(KeyEventArgs e)
    {
        var normalizedKey = NormalizeKey(e.Key, e.PhysicalKey);
        if (normalizedKey != Key)
        {
            return false;
        }

        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        return relevantModifiers == Modifiers;
    }

    public bool TryGetWindowsRegistration(out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers |= 0x0001;
        }

        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            modifiers |= 0x0002;
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= 0x0004;
        }

        if (Modifiers.HasFlag(KeyModifiers.Meta))
        {
            modifiers |= 0x0008;
        }

        if (!TryGetVirtualKey(Key, out virtualKey))
        {
            modifiers = 0;
            return false;
        }

        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        parts.Add(ToDisplayKey(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? value, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A hotkey is required.";
            return false;
        }

        var modifiers = KeyModifiers.None;
        Key? key = null;

        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= KeyModifiers.Control;
                    continue;
                case "SHIFT":
                    modifiers |= KeyModifiers.Shift;
                    continue;
                case "ALT":
                    modifiers |= KeyModifiers.Alt;
                    continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                    modifiers |= KeyModifiers.Meta;
                    continue;
            }

            if (key is not null)
            {
                error = "Only one non-modifier key can be used in a hotkey.";
                return false;
            }

            if (!TryParseKey(rawPart, out var parsedKey))
            {
                error = string.Format(CultureInfo.InvariantCulture, "'{0}' is not a supported hotkey key.", rawPart);
                return false;
            }

            key = parsedKey;
        }

        if (key is null)
        {
            error = "A hotkey must include a key, such as Alt+R.";
            return false;
        }

        gesture = new HotkeyGesture(key.Value, modifiers);
        return true;
    }

    private static bool TryParseKey(string value, out Key key)
    {
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' && Enum.TryParse(character.ToString(), ignoreCase: true, out key))
            {
                return true;
            }

            if (character is >= '0' and <= '9' && Enum.TryParse($"D{character}", ignoreCase: true, out key))
            {
                return true;
            }
        }

        if (KeyAliases.TryGetValue(value.Replace(" ", string.Empty, StringComparison.Ordinal), out key))
        {
            return true;
        }

        return Enum.TryParse(value.Replace(" ", string.Empty, StringComparison.Ordinal), ignoreCase: true, out key);
    }

    private static Key NormalizeKey(Key key, PhysicalKey physicalKey)
        => key != Key.None ? key : PhysicalToLogicalKey(physicalKey);

    private static Key PhysicalToLogicalKey(PhysicalKey physicalKey) => physicalKey switch
    {
        PhysicalKey.A => Key.A,
        PhysicalKey.B => Key.B,
        PhysicalKey.C => Key.C,
        PhysicalKey.D => Key.D,
        PhysicalKey.E => Key.E,
        PhysicalKey.F => Key.F,
        PhysicalKey.G => Key.G,
        PhysicalKey.H => Key.H,
        PhysicalKey.I => Key.I,
        PhysicalKey.J => Key.J,
        PhysicalKey.K => Key.K,
        PhysicalKey.L => Key.L,
        PhysicalKey.M => Key.M,
        PhysicalKey.N => Key.N,
        PhysicalKey.O => Key.O,
        PhysicalKey.P => Key.P,
        PhysicalKey.Q => Key.Q,
        PhysicalKey.R => Key.R,
        PhysicalKey.S => Key.S,
        PhysicalKey.T => Key.T,
        PhysicalKey.U => Key.U,
        PhysicalKey.V => Key.V,
        PhysicalKey.W => Key.W,
        PhysicalKey.X => Key.X,
        PhysicalKey.Y => Key.Y,
        PhysicalKey.Z => Key.Z,
        PhysicalKey.Digit0 => Key.D0,
        PhysicalKey.Digit1 => Key.D1,
        PhysicalKey.Digit2 => Key.D2,
        PhysicalKey.Digit3 => Key.D3,
        PhysicalKey.Digit4 => Key.D4,
        PhysicalKey.Digit5 => Key.D5,
        PhysicalKey.Digit6 => Key.D6,
        PhysicalKey.Digit7 => Key.D7,
        PhysicalKey.Digit8 => Key.D8,
        PhysicalKey.Digit9 => Key.D9,
        _ => Key.None,
    };

    private static string ToDisplayKey(Key key)
    {
        var name = key switch
        {
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.Oem1 => "Semicolon",
            Key.Oem2 => "Slash",
            Key.Oem4 => "OpenBracket",
            Key.Oem5 => "Backslash",
            Key.Oem6 => "CloseBracket",
            Key.Oem7 => "Quote",
            _ => key.ToString(),
        };

        return name.StartsWith('D') && name.Length == 2 && char.IsDigit(name[1])
            ? name[1].ToString()
            : name;
    }

    private static bool TryGetVirtualKey(Key key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            Key.A => 0x41,
            Key.B => 0x42,
            Key.C => 0x43,
            Key.D => 0x44,
            Key.E => 0x45,
            Key.F => 0x46,
            Key.G => 0x47,
            Key.H => 0x48,
            Key.I => 0x49,
            Key.J => 0x4A,
            Key.K => 0x4B,
            Key.L => 0x4C,
            Key.M => 0x4D,
            Key.N => 0x4E,
            Key.O => 0x4F,
            Key.P => 0x50,
            Key.Q => 0x51,
            Key.R => 0x52,
            Key.S => 0x53,
            Key.T => 0x54,
            Key.U => 0x55,
            Key.V => 0x56,
            Key.W => 0x57,
            Key.X => 0x58,
            Key.Y => 0x59,
            Key.Z => 0x5A,
            Key.D0 => 0x30,
            Key.D1 => 0x31,
            Key.D2 => 0x32,
            Key.D3 => 0x33,
            Key.D4 => 0x34,
            Key.D5 => 0x35,
            Key.D6 => 0x36,
            Key.D7 => 0x37,
            Key.D8 => 0x38,
            Key.D9 => 0x39,
            Key.F1 => 0x70,
            Key.F2 => 0x71,
            Key.F3 => 0x72,
            Key.F4 => 0x73,
            Key.F5 => 0x74,
            Key.F6 => 0x75,
            Key.F7 => 0x76,
            Key.F8 => 0x77,
            Key.F9 => 0x78,
            Key.F10 => 0x79,
            Key.F11 => 0x7A,
            Key.F12 => 0x7B,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Space => 0x20,
            Key.Escape => 0x1B,
            Key.Delete => 0x2E,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.OemPlus => 0xBB,
            Key.OemMinus => 0xBD,
            Key.OemComma => 0xBC,
            Key.OemPeriod => 0xBE,
            Key.Oem1 => 0xBA,
            Key.Oem2 => 0xBF,
            Key.Oem4 => 0xDB,
            Key.Oem5 => 0xDC,
            Key.Oem6 => 0xDD,
            Key.Oem7 => 0xDE,
            _ => 0,
        };

        return virtualKey != 0;
    }
}


