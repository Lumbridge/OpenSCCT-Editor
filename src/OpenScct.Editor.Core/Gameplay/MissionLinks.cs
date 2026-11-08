using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Named mission relationships, edited as one undo step without changing event wiring.</summary>
public static class MissionLinks
{
    public static bool IsObjective(Actor a) => ClassDefaults.IsA(a, "SBase.SObjective");
    public static bool IsTrigger(Actor a) => ClassDefaults.IsA(a, "SBase.SObjectiveTrigger");

    public static string Name(Actor a) => a.Props.GetString("ObjectiveName") is { Length: > 0 } objective ? objective
        : a.Props.GetString("TriggerName") is { Length: > 0 } trigger ? trigger : a.Id;

    public static string Label(Actor a) => Name(a) == a.Id ? a.Id : $"{Name(a)} ({a.Id})";

    public static bool Contains(Actor parent, string property, string child) =>
        MissionTree.Refs(parent.Props[property]).Contains(child, StringComparer.OrdinalIgnoreCase);

    /// <summary>A reason a relationship cannot be added, or null. Nested missions cannot form cycles.</summary>
    public static string? Refusal(MapProject p, Actor parent, Actor child)
    {
        if (!IsObjective(parent)) return "Choose a mission or objective first.";
        if (Same(parent.Id, child.Id)) return "A mission cannot contain itself.";
        if (IsTrigger(child)) return null;
        if (!MatchPlan.IsMission(parent) || !IsObjective(child))
            return "Missions contain objectives; objectives are completed by computers, bomb targets or disks.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Reaches(string id) => Same(id, parent.Id) || (seen.Add(id) && p.FindActor(id) is { } a
            && MissionTree.Refs(a.Props["Objectives"]).Any(Reaches));
        return Reaches(child.Id) ? "This would create a loop between missions." : null;
    }

    public static ReplaceObjectsCommand? Add(MapProject p, string parentId, IEnumerable<string> children)
    {
        Actor parent = Require(p, parentId);
        List<Actor> actors = children.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Require(p, id)).ToList();
        foreach (Actor child in actors)
            if (Refusal(p, parent, child) is { } reason) throw new ArgumentException(reason);
        actors.RemoveAll(a => Contains(parent, Property(a), a.Id));
        if (actors.Count == 0) return null;
        Actor after = parent.Clone();
        foreach (IGrouping<string, Actor> group in actors.GroupBy(Property))
        {
            List<SvJsonNode> items = Items(after.Props[group.Key]);
            items.AddRange(group.Select(a => PropValues.ActorRef(a.Id)));
            after.Props.Set(group.Key, PropValues.Array(items));
        }
        return new ReplaceObjectsCommand($"Link {actors.Count} item(s) to {Name(parent)}").Actor(parent, after);
    }

    /// <summary>Also permits removing a missing reference from an imported map.</summary>
    public static ReplaceObjectsCommand? Remove(MapProject p, string parentId, string property, string childId)
    {
        Actor parent = Require(p, parentId);
        ValidateProperty(parent, property);
        if (!Contains(parent, property, childId)) return null;
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(parent.Id)], $"Unlink {childId} from {Name(parent)}", actor: a =>
            a.Props.Set(property, PropValues.Array(Items(a.Props[property]).Where(n => !Same(PropValues.ActorId(n), childId)))));
    }

    public static ReplaceObjectsCommand? Move(MapProject p, string parentId, string childId, int direction)
    {
        Actor parent = Require(p, parentId);
        ValidateProperty(parent, "Objectives");
        List<SvJsonNode> items = Items(parent.Props["Objectives"]);
        List<int> slots = Enumerable.Range(0, items.Count).Where(i => PropValues.ActorId(items[i]) is not null).ToList();
        int index = slots.FindIndex(i => Same(PropValues.ActorId(items[i]), childId));
        int to = index + Math.Sign(direction);
        if (index < 0 || to < 0 || to >= slots.Count || to == index) return null;
        (items[slots[index]], items[slots[to]]) = (items[slots[to]], items[slots[index]]);
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(parent.Id)], $"Reorder {Name(parent)} objectives", actor: a =>
            a.Props.Set("Objectives", PropValues.Array(items)));
    }

    /// <summary>A new named objective, already in its mission; undo removes both the actor and relationship.</summary>
    public static IEditCommand CreateObjective(MapProject p, string missionId, Actor objective)
    {
        Actor mission = Require(p, missionId);
        ValidateProperty(mission, "Objectives");
        if (p.Find(objective.Id) is not null || !IsObjective(objective) || MatchPlan.IsMission(objective))
            throw new ArgumentException("Choose a new objective with a unique name.");
        return new CompositeCommand($"Create objective in {Name(mission)}", [
            new AddObjectsCommand("Create objective", actors: [objective]),
            ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(mission.Id)], "Link objective", actor: a =>
                a.Props.Set("Objectives", PropValues.Array(Items(a.Props["Objectives"]).Append(PropValues.ActorRef(objective.Id)))))
        ]);
    }

    private static string Property(Actor child) => IsTrigger(child) ? "Triggers" : "Objectives";
    private static Actor Require(MapProject p, string id) => p.FindActor(id) ?? throw new ArgumentException($"{id} is no longer in the map.");
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static List<SvJsonNode> Items(SvJsonNode? node) => node is SvJsonArray array ? [.. array]
        : PropValues.ActorId(node) is not null ? [node!] : [];
    private static void ValidateProperty(Actor parent, string property)
    {
        if (!(property == "Objectives" && MatchPlan.IsMission(parent)) && !(property == "Triggers" && IsObjective(parent)))
            throw new ArgumentException("Choose a mission's objectives or an objective's devices.");
    }
}
