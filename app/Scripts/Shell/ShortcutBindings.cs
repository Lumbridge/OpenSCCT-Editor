using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// The user's own shortcut bindings over the defaults the actions carry (<see cref="EditorAction.Shortcut"/>). Saved
/// per user in the <c>[shortcuts]</c> section of settings.cfg as <c>action id = "Ctrl+Shift+K"</c>; an empty value
/// means the user took the action's shortcut away. Menus, tooltips, the command palette and the shortcut router all
/// read <see cref="Of"/>, so a new binding shows and works at once.
/// </summary>
public static class ShortcutBindings
{
    private const string Section = "shortcuts";

    /// <summary>Raised when a binding changes.</summary>
    public static event Action? Changed;

    /// <summary>The shortcut that runs the action now: the user's binding when there is one, else its default.</summary>
    public static string? Of(EditorAction action)
    {
        if (!AppSettings.Has(Section, action.Id))
        {
            return action.Shortcut;
        }

        string user = AppSettings.GetString(Section, action.Id, "");
        return user.Length == 0 ? null : user;
    }

    /// <summary>The shortcut as the UI writes it (short key names: "Esc", "Del"), or null when there is none.</summary>
    public static string? Text(EditorAction action) =>
        Of(action) is { } s ? KeyChord.Parse(s)?.ToString() ?? s : null;

    /// <summary>True when the user changed this action's shortcut.</summary>
    public static bool IsCustom(EditorAction action) => AppSettings.Has(Section, action.Id);

    /// <summary>Binds a shortcut to the action (null takes its shortcut away). Binding the default again drops the
    /// user's entry.</summary>
    public static void Bind(EditorAction action, string? shortcut)
    {
        string text = shortcut ?? "";
        if (string.Equals(text, action.Shortcut ?? "", StringComparison.OrdinalIgnoreCase))
        {
            AppSettings.Erase(Section, action.Id);
        }
        else
        {
            AppSettings.Set(Section, action.Id, text);
        }

        Changed?.Invoke();
    }

    /// <summary>Puts the action's default shortcut back.</summary>
    public static void Reset(EditorAction action)
    {
        AppSettings.Erase(Section, action.Id);
        Changed?.Invoke();
    }

    /// <summary>Puts every default shortcut back.</summary>
    public static void ResetAll()
    {
        AppSettings.EraseSection(Section);
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces every binding with <paramref name="preset"/>: its keys, and the defaults for the rest, except defaults
    /// on a chord the preset gives to another action, which are taken away.
    /// </summary>
    public static void ApplyPreset(IEnumerable<EditorAction> actions, ShortcutPreset preset)
    {
        AppSettings.EraseSection(Section);
        HashSet<KeyChord> taken = preset.Keys.Values.Select(KeyChord.Parse).OfType<KeyChord>().ToHashSet();
        foreach (EditorAction action in actions)
        {
            if (preset.Keys.TryGetValue(action.Id, out string? keys))
            {
                if (!string.Equals(keys, action.Shortcut, StringComparison.OrdinalIgnoreCase))
                {
                    AppSettings.Set(Section, action.Id, keys);
                }
            }
            else if (KeyChord.Parse(action.Shortcut) is { } chord && taken.Contains(chord))
            {
                AppSettings.Set(Section, action.Id, "");
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The other actions a chord would also run (a clash: the first by id wins).</summary>
    public static IReadOnlyList<EditorAction> Clashes(IEnumerable<EditorAction> actions, EditorAction action, KeyChord chord) =>
        actions.Where(a => !ReferenceEquals(a, action) && KeyChord.Parse(Of(a)) == chord)
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
}
