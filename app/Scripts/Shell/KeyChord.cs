using System.Globalization;
using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// A key with modifiers, parsed from the text an <see cref="Core.EditorAction"/> carries ("Ctrl+Shift+S", "Delete",
/// "[", "Alt+1"). Keys are matched by their logical keycode, so letters follow the keyboard layout.
/// </summary>
public readonly record struct KeyChord(Key Key, bool Ctrl, bool Shift, bool Alt)
{
    private static readonly Dictionary<string, Key> s_symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["["] = Key.Bracketleft,
        ["]"] = Key.Bracketright,
        ["="] = Key.Equal,
        ["-"] = Key.Minus,
        [","] = Key.Comma,
        ["."] = Key.Period,
        ["/"] = Key.Slash,
        ["\\"] = Key.Backslash,
        [";"] = Key.Semicolon,
        ["'"] = Key.Apostrophe,
        ["`"] = Key.Quoteleft,
        ["Del"] = Key.Delete,
        ["Esc"] = Key.Escape,
        ["Enter"] = Key.Enter,
        ["Return"] = Key.Enter,
        ["PgUp"] = Key.Pageup,
        ["PgDn"] = Key.Pagedown,
    };

    /// <summary>Parses shortcut text, or returns null when it names no key.</summary>
    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                default:
                    return null;
            }
        }

        string keyName = parts[^1];
        Key key = s_symbols.TryGetValue(keyName, out Key k) ? k : OS.FindKeycodeFromString(keyName);
        return key == Key.None ? null : new KeyChord(key, ctrl, shift, alt);
    }

    public bool Matches(InputEventKey e) =>
        e.Keycode == Key && e.IsCommandOrControlPressed() == Ctrl && e.ShiftPressed == Shift && e.AltPressed == Alt;

    /// <summary>The shortcut as a Godot event (menus show and match it natively).</summary>
    public InputEventKey ToEvent() => new() { Keycode = Key, CtrlPressed = Ctrl, ShiftPressed = Shift, AltPressed = Alt };

    public override string ToString()
    {
        Key key = Key;
        // Short key names, as the UI writes them everywhere ("Esc", "Del"); they parse back the same.
        string name = key switch
        {
            Key.Escape => "Esc",
            Key.Delete => "Del",
            _ => s_symbols.FirstOrDefault(kv => kv.Value == key && kv.Key.Length == 1).Key ?? OS.GetKeycodeString(key),
        };
        return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + name;
    }

    /// <summary>A number key's shortcut text ("1".."9").</summary>
    public static string Digit(int n) => n.ToString(CultureInfo.InvariantCulture);
}
