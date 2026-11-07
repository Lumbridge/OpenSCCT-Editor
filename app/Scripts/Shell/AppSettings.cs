using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor's own preferences (not part of any map): recent files, autosave, the viewport layout, colours,
/// UI scale and shortcut bindings (<see cref="Preferences"/>, <see cref="ShortcutBindings"/>). Stored per user in
/// <c>settings.cfg</c> in <see cref="UserData.Folder"/> and written on every change, so a crash loses nothing.
/// </summary>
public static class AppSettings
{
    public static bool Exists => Godot.FileAccess.FileExists(FilePath);
    public const int MaxRecent = 10;
    private static string FilePath => UserData.PathOf("settings.cfg");
    private static ConfigFile? s_config;

    private static ConfigFile Config
    {
        get
        {
            if (s_config is null)
            {
                s_config = new ConfigFile();
                if (Godot.FileAccess.FileExists(FilePath))
                {
                    s_config.Load(FilePath);
                }
            }

            return s_config;
        }
    }

    /// <summary>Recently opened projects, newest first (absolute folder paths).</summary>
    public static IReadOnlyList<string> Recent => GetStrings("files", "recent");

    /// <summary>Minutes between autosaves of a changed map; 0 turns autosave off.</summary>
    public static int AutosaveMinutes
    {
        get => GetInt("files", "autosave_minutes", 5);
        set => Set("files", "autosave_minutes", Math.Clamp(value, 0, 120));
    }

    /// <summary>How many autosave slots rotate per map.</summary>
    public static int AutosaveSlots
    {
        get => GetInt("files", "autosave_slots", 10);
        set => Set("files", "autosave_slots", Math.Clamp(value, 1, 50));
    }

    public static void AddRecent(string folder)
    {
        string full = Path.GetFullPath(folder);
        var list = Recent.Where(r => !string.Equals(r, full, StringComparison.OrdinalIgnoreCase)).Prepend(full).Take(MaxRecent).ToArray();
        Set("files", "recent", list);
    }

    public static void RemoveRecent(string folder) =>
        Set("files", "recent", Recent.Where(r => !string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)).ToArray());

    public static void ClearRecent() => Set("files", "recent", Array.Empty<string>());

    public static bool Has(string section, string key) => Config.HasSectionKey(section, key);

    /// <summary>The keys set in a section (empty when there is none).</summary>
    public static string[] Keys(string section) => Config.HasSection(section) ? Config.GetSectionKeys(section) : Array.Empty<string>();

    public static double GetDouble(string section, string key, double fallback) =>
        Config.HasSectionKey(section, key) ? Config.GetValue(section, key).AsDouble() : fallback;

    /// <summary>Removes a key, so its default applies again.</summary>
    public static void Erase(string section, string key)
    {
        if (Config.HasSectionKey(section, key))
        {
            Config.EraseSectionKey(section, key);
            Config.Save(FilePath);
        }
    }

    /// <summary>Removes a whole section (every key in it goes back to its default).</summary>
    public static void EraseSection(string section)
    {
        if (Config.HasSection(section))
        {
            Config.EraseSection(section);
            Config.Save(FilePath);
        }
    }

    public static bool GetBool(string section, string key, bool fallback) =>
        Config.HasSectionKey(section, key) ? Config.GetValue(section, key).AsBool() : fallback;

    public static int GetInt(string section, string key, int fallback) =>
        Config.HasSectionKey(section, key) ? Config.GetValue(section, key).AsInt32() : fallback;

    public static string GetString(string section, string key, string fallback) =>
        Config.HasSectionKey(section, key) ? Config.GetValue(section, key).AsString() : fallback;

    public static string[] GetStrings(string section, string key) =>
        Config.HasSectionKey(section, key) ? Config.GetValue(section, key).AsStringArray() : Array.Empty<string>();

    public static void Set(string section, string key, Variant value)
    {
        Config.SetValue(section, key, value);
        Config.Save(FilePath);
    }
}
