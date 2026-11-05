using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// Which game mode each gameplay actor belongs to, for the "show one game mode's objects" view filter. At match start
/// the game resets only the missions whose <c>GameMode</c> is the match's mode (spec/objectives/missions.md rule 4), so
/// a mission belongs to its mode together with everything it holds: its objectives, nested zone missions (GM_Undefined,
/// reset by their parent) and the triggers of all of them. SDisk dog tags only exist in Disk Hunt. Every other actor is
/// shared by all modes.
/// </summary>
public sealed class GameModes
{
    public const int DiskHunt = 4;

    private readonly Dictionary<string, HashSet<int>> _modes;

    private GameModes(Dictionary<string, HashSet<int>> modes) => _modes = modes;

    /// <summary>The modes the map has missions for (by mode number, in order), with their names.</summary>
    public IReadOnlyList<(int Mode, string Name)> Present { get; private init; } = [];

    public static GameModes Build(MapProject p, MissionTree tree)
    {
        var modes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        void Claim(string id, int mode)
        {
            if (!modes.TryGetValue(id, out HashSet<int>? set))
            {
                modes[id] = set = [];
            }

            set.Add(mode);
        }

        // Walks a mission or objective and everything under it; the visited set stops loops in broken maps.
        void Walk(string id, int mode, HashSet<string> seen)
        {
            if (!seen.Add(id) || p.FindActor(id) is not { } a)
            {
                return;
            }

            Claim(id, mode);
            foreach (string child in MissionTree.Refs(a.Props["Objectives"]))
            {
                Walk(child, mode, seen);
            }

            foreach (string trigger in MissionTree.Refs(a.Props["Triggers"]))
            {
                Claim(trigger, mode);
            }
        }

        foreach (MissionNode m in tree.Missions.Where(m => m.GameMode != 0))
        {
            Walk(m.Id, m.GameMode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        foreach (Actor disk in p.Actors.Where(a => a.ShortClass == "SDisk"))
        {
            Claim(disk.Id, DiskHunt);
        }

        List<(int, string)> present = tree.Missions.Where(m => m.GameMode != 0)
            .Select(m => (m.GameMode, m.ModeName)).Distinct().OrderBy(m => m.GameMode).ToList();
        return new GameModes(modes) { Present = present };
    }

    /// <summary>The modes an actor belongs to; empty for actors every mode shares.</summary>
    public IReadOnlyCollection<int> Of(string id) => _modes.TryGetValue(id, out HashSet<int>? set) ? set : [];

    /// <summary>True when the actor is shared by all modes or belongs to <paramref name="mode"/>.</summary>
    public bool InMode(string id, int mode) => !_modes.TryGetValue(id, out HashSet<int>? set) || set.Contains(mode);

    /// <summary>How many actors belong to a mode (shared ones not counted).</summary>
    public int Count(int mode) => _modes.Values.Count(s => s.Contains(mode));
}
