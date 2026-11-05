using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// Crash recovery. While the editor runs it keeps a marker in <c>sessions</c> (in <see cref="UserData.Folder"/>) naming the autosave that holds
/// the open map's unsaved work (cleared when the map is saved or another one is opened), and deletes it when it closes
/// normally. A marker left by an editor that is no longer running means that editor crashed or was killed: at startup
/// the newest such autosave is offered back, unless its project has been saved since. The autosave opens as the map it
/// was taken of, unsaved, so saving puts the work back in its project. Every other crashed editor's autosave is named
/// in the log.
/// </summary>
[ShellPart(Order = -45)]
public sealed partial class CrashRecovery : Node, IShellPart
{
    private EditorContext _ctx = null!;
    private string? _marker;

    public static string MarkerFolder => UserData.PathOf("sessions");

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        Name = "CrashRecovery";
        window.AddChild(this);
        IReadOnlyList<SessionMarker> crashed = SessionMarkers.Crashed(MarkerFolder);
        try
        {
            _marker = SessionMarkers.Create(MarkerFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Log($"Crash recovery is off: {ex.Message}", Severity.Warning);
        }

        Documents.Autosaved += Remember;
        context.SessionReplaced += () => Remember(null);
        context.Saved += () => Remember(null);
        if (crashed.Count > 0)
        {
            // Offer once the window is up and the start map has opened.
            EditorLifetime.Defer(() => Offer(crashed));
        }
    }

    public override void _ExitTree()
    {
        Documents.Autosaved -= Remember;
        if (_marker is not null)
        {
            SessionMarkers.Remove(_marker);
            _marker = null;
        }
    }

    private void Remember(string? autosave)
    {
        if (_marker is not null)
        {
            SessionMarkers.Update(_marker, _ctx.Project.Title, autosave);
        }
    }

    private void Offer(IReadOnlyList<SessionMarker> crashed)
    {
        foreach (SessionMarker m in crashed)
        {
            SessionMarkers.Remove(m.File);
        }

        // Newest start first; an autosave whose project was saved since holds nothing to recover.
        List<(string Copy, AutosaveInfo? Info)> copies = crashed
            .Where(m => m.Autosave is { } a && ProjectStore.IsProject(a))
            .Select(m => (Copy: m.Autosave!, Info: Autosaves.ReadInfo(m.Autosave!)))
            .Where(c => c.Info is null || !Autosaves.IsOlderThanProject(c.Info))
            .ToList();
        if (copies.Count == 0)
        {
            _ctx.Log("The editor closed unexpectedly last time, with no unsaved work in an autosave.", Severity.Warning);
            return;
        }

        foreach ((string other, AutosaveInfo? otherInfo) in copies.Skip(1))
        {
            _ctx.Log($"Another editor that closed unexpectedly left the autosave {other} ({Describe(other, otherInfo)}); File > Show Autosave Folder has it.",
                Severity.Warning);
        }

        (string copy, AutosaveInfo? info) = copies[0];
        string what = Describe(copy, info);
        string when = info is { } t ? t.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        _ctx.Log($"The editor closed unexpectedly last time. Its newest autosave is {copy} ({what}, {when}).", Severity.Warning);
        if (Dialogs.Host is null)
        {
            return;
        }

        Dialogs.Ask(Dialogs.Host, "Recover your work?", $"Open the newest autosave of {what}?",
            $"The editor closed unexpectedly last time. This autosave is from {when}. It opens unsaved; save to put it back in its project.",
            "Open autosave", () =>
            {
                if (Documents.Instance is { } documents)
                {
                    documents.GuardUnsaved(() => documents.OpenAutosave(copy));
                }
            }, cancel: "Not now", icon: "info", modal: false);
    }

    private static string Describe(string copy, AutosaveInfo? info) =>
        info is { Title.Length: > 0 } i ? $"{i.Title} ({i.MapId})" : Path.GetFileName(copy);
}
