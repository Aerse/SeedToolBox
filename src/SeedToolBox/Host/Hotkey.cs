using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace SeedToolBox.Host;

/// <summary>A key combination such as Ctrl+Alt+Q, stored as text in settings.</summary>
public readonly struct Hotkey
{
    public ModifierKeys Modifiers { get; }
    public Key Key { get; }

    public Hotkey(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>Function keys may be used alone; other keys need a modifier.</summary>
    public bool IsValid => Key != Key.None && !IsModifierKey(Key) && (Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24);

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Key != Key.None && !IsModifierKey(Key)) parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Oem3 => "`",
        _ => key.ToString(),
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        var key = Key.None;
        foreach (var part in text!.Split('+').Select(p => p.Trim()))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) key = Key.D0 + (part[0] - '0');
                    else if (part == "`") key = Key.Oem3;
                    else if (!Enum.TryParse(part, true, out key)) return false;
                    break;
            }
        }
        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }
}
