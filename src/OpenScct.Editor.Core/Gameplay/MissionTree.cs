using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>An objective of a mission: its SObjective, the triggers that complete it, and triggers it names that are
/// not in the map.</summary>
public sealed record ObjectiveNode(string Id, string Name, IReadOnlyList<string> Triggers, IReadOnlyList<string> Missing);

/// <summary>A mission (SMission): its game mode, how many objectives win it, its objectives and own triggers.</summary>
public sealed record MissionNode(string Id, int GameMode, string ModeName, int MinimumObjectives, bool Chained, string Description,
    IReadOnlyList<ObjectiveNode> Objectives, IReadOnlyList<string> Triggers)
{
    /// <summary>What completes the mission: its objectives, or its own triggers (disk hunt) when it has none.</summary>
    public int Completers => Objectives.Count > 0 ? Objectives.Count : Triggers.Count;
}

/// <summary>
/// A player-count rule or win targets on an actor (SVMap 1.2, <see cref="Actor.Match"/>), or an RE+ rules actor the
/// project still carries (<see cref="Native"/> false: OpenSCCT does not read it; converting the map again lifts it).
/// </summary>
public sealed record MatchSizeRule(string Id, string Class, string Detail, bool Native);

/// <summary>
/// The missions, objectives and triggers tree as OpenSCCT derives it (GameplayDoc.ComputeMissionTree): each SMission's
/// <c>Objectives</c>, each SObjective's <c>Triggers</c>, and the mission's own <c>Triggers</c> (SVMap 1.1). Also lists
/// objectives no mission uses and the actors with player-count rules.
/// </summary>
public sealed class MissionTree
{
    /// <summary>EGameMode, used when the class catalog has no enum data.</summary>
    public static readonly IReadOnlyList<string> GameModes =
        ["GM_Undefined", "GM_Extraction", "GM_Sabotage", "GM_Neutralization", "GM_DiskHunt", "GM_Multi", "GM_DeathMatch", "GM_MercDeathMatch"];

    /// <summary>RE+'s classes holding player-count rules, matched by short name.</summary>
    public static readonly IReadOnlyList<string> MatchSizeClasses = ["SMatchSizeRules", "SPlayerCountRules", "SObjectivePlayers"];

    public required IReadOnlyList<MissionNode> Missions { get; init; }

    /// <summary>SObjectives no mission lists.</summary>
    public required IReadOnlyList<string> Unlisted { get; init; }

    public required IReadOnlyList<MatchSizeRule> MatchSize { get; init; }

    public static MissionTree Build(MapProject p, IClassCatalog? classes = null)
    {
        IReadOnlyList<string> modes = classes?.EnumValues("Core.Object.EGameMode") ?? GameModes;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missions = new List<MissionNode>();
        foreach (Actor m in p.Actors.Where(MatchPlan.IsMission))
        {
            var objectives = new List<ObjectiveNode>();
            foreach (string oid in Refs(m.Props["Objectives"]))
            {
                listed.Add(oid);
                Actor? o = p.FindActor(oid);
                List<string> triggers = o is null ? [] : Refs(o.Props["Triggers"]);
                objectives.Add(new ObjectiveNode(oid, o?.Props.GetString("ObjectiveName") ?? "",
                    triggers, triggers.Where(t => p.FindActor(t) is null).ToList()));
            }

            // Unset values take the class defaults, as the compiled map and the game do.
            int mode = (int)ClassDefaults.Number(m, "GameMode", 0);
            missions.Add(new MissionNode(m.Id, mode, mode >= 0 && mode < modes.Count ? modes[mode].Replace("GM_", "") : $"mode {mode}",
                (int)ClassDefaults.Number(m, "MinimumObjectives", 0), m.Props.GetBool("bChained"), m.Props.GetString("Description") ?? "",
                objectives, Refs(m.Props["Triggers"])));
        }

        List<string> unlisted = p.Actors.Where(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a) && !listed.Contains(a.Id)).Select(a => a.Id).ToList();
        List<MatchSizeRule> rules = p.Actors.Where(a => a.Match is { IsEmpty: false })
            .Select(a => new MatchSizeRule(a.Id, a.Class, MatchPlan.Describe(a.Match!), Native: true))
            .Concat(p.Actors.Where(a => MatchSizeClasses.Contains(a.ShortClass, StringComparer.OrdinalIgnoreCase))
                .Select(a => new MatchSizeRule(a.Id, a.Class, "RE+ rules actor: not read by OpenSCCT", Native: false)))
            .ToList();
        return new MissionTree { Missions = missions, Unlisted = unlisted, MatchSize = rules };
    }

    /// <summary>The actor ids in an array of references (or a single one).</summary>
    public static List<string> Refs(SvJsonNode? node) => node switch
    {
        SvJsonArray a => a.Select(PropValues.ActorId).OfType<string>().ToList(),
        SvJsonObject o when PropValues.ActorId(o) is { } id => [id],
        _ => [],
    };
}
