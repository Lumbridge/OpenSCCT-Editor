using Godot;

namespace OpenScctEditor.Shell.Background;

/// <summary>
/// The one place the editor reads and writes the clipboard: the system clipboard normally, a string of the editor's
/// own in background mode (so a background run never overwrites what the user copied) and headless (no clipboard).
/// </summary>
public static class EditorClipboard
{
    private static string s_local = "";

    /// <summary>True in background mode.</summary>
    public static bool Local { get; set; }

    private static bool UsesSystem => !Local && DisplayServer.HasFeature(DisplayServer.Feature.Clipboard);

    public static string Get() => UsesSystem ? DisplayServer.ClipboardGet() : s_local;

    public static void Set(string text)
    {
        s_local = text;
        if (UsesSystem)
        {
            DisplayServer.ClipboardSet(text);
        }
    }
}
