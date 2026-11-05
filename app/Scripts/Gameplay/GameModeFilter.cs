using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The "show one game mode's objects" view filter: with a mode chosen, the actors that belong only to other modes
/// (their missions, objectives, triggers and Disk Hunt disks, see <see cref="GameModes"/>) are not drawn or picked;
/// everything every mode shares stays. View/Game Mode chooses it; it resets when another map is opened.
/// </summary>
public static class GameModeFilter
{
    private static HashSet<EditorMeta>? s_hidden;

    /// <summary>The mode shown alone (EGameMode), or 0 for every mode's objects.</summary>
    public static int Mode { get; private set; }

    /// <summary>How many actors the filter hides now.</summary>
    public static int HiddenCount
    {
        get
        {
            if (Mode == 0)
            {
                return 0;
            }

            s_hidden ??= Build(EditorContext.Instance.Project);
            return s_hidden.Count;
        }
    }

    /// <summary>True when the filter hides an object of the open map.</summary>
    public static bool Hides(MapProject p, EditorMeta meta)
    {
        if (Mode == 0 || !ReferenceEquals(p, EditorContext.Instance.Project))
        {
            return false;
        }

        s_hidden ??= Build(p);
        return s_hidden.Contains(meta);
    }

    public static void Show(int mode)
    {
        Mode = mode;
        Refresh();
    }

    /// <summary>Forgets what is hidden (the map changed) and redraws.</summary>
    public static void Refresh()
    {
        s_hidden = null;
        if (ViewportLayout.Instance is { } views)
        {
            views.Scene.ApplyVisibility(EditorContext.Instance.Project);
        }

        ViewOverlays.Invalidate();
    }

    /// <summary>A game mode as people read it ("DeathMatch" and "Deathmatch" are both "Deathmatch", "DiskHunt" is
    /// "Disk hunt"); the stored name stays in tooltips.</summary>
    public static string Display(string mode) => mode.Replace("GM_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "deathmatch" => "Deathmatch",
        "mercdeathmatch" => "Merc deathmatch",
        "diskhunt" => "Disk hunt",
        "story" => "Story",
        _ => PanelUi.Words(mode),
    };

    public static string Name(int mode) =>
        mode > 0 && mode < MissionTree.GameModes.Count ? MissionTree.GameModes[mode].Replace("GM_", "") : "every mode";

    // Edits replace the objects they change, so the metas are compared by reference and the set is rebuilt after any edit.
    private static HashSet<EditorMeta> Build(MapProject p)
    {
        GameModes modes = GameModes.Build(p, GameplayState.Instance.Missions);
        return new HashSet<EditorMeta>(p.Actors.Where(a => !modes.InMode(a.Id, Mode)).Select(a => a.Editor), ReferenceEqualityComparer.Instance);
    }
}

/// <summary>The View/Game Mode menu: every mode's objects, or one mode's (the modes the map has missions for).</summary>
[ShellPart(Order = -18)]
public sealed class GameModeFilterPart : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        context.ProjectChanged += _ => GameModeFilter.Refresh();
        context.SessionReplaced += () => GameModeFilter.Show(0);
        context.AddAction(new EditorAction("view.mode.all", "Every Mode's Objects", "View/Game Mode", () =>
        {
            GameModeFilter.Show(0);
            context.SetStatus("Showing every game mode's objects");
        })
        {
            Order = 0,
            IsChecked = () => GameModeFilter.Mode == 0,
        });
        for (int mode = 1; mode < MissionTree.GameModes.Count; mode++)
        {
            int m = mode;
            string name = GameModeFilter.Name(m);
            context.AddAction(new EditorAction("view.mode." + name.ToLowerInvariant(), name == "Multi" ? "Story (Multi) Only" : name + " Only", "View/Game Mode", () =>
            {
                GameModeFilter.Show(m);
                GameModes modes = GameModes.Build(context.Project, GameplayState.Instance.Missions);
                context.SetStatus($"Showing {name}'s objects: {modes.Count(m)} of its own; {GameModeFilter.HiddenCount} of other modes hidden");
            })
            {
                Order = 10 + m,
                IsChecked = () => GameModeFilter.Mode == m,
                IsEnabled = () => GameplayState.Instance.Missions.Missions.Any(x => x.GameMode == m),
                Description = "Hides the missions, objectives and triggers of the other game modes (and Disk Hunt disks outside Disk Hunt)",
            });
        }
    }
}
