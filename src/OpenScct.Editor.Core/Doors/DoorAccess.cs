using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Doors;

public static class DoorAccess
{
    public static List<Actor> Targets(MapProject p, Actor panel) => panel.Props.GetString("Event") is { Length: > 0 } tag
        ? p.Actors.Where(a => a.Door is not null && string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)).ToList() : [];
    public static bool IsHackPanel(MapProject p, Actor a) => a.ShortClass == "SInterrupteur" && a.Props.GetInt("iHackingTime") > 0 && Targets(p, a).Count > 0;
    public static ReplaceObjectsCommand HackTime(MapProject p, string id, int seconds)
    {
        if (seconds is < 1 or > 600) throw new ArgumentException("Hack time must be 1–600 seconds");
        if (p.FindActor(id) is not { } panel || !IsHackPanel(p, panel)) throw new ArgumentException("Select a linked door hack panel");
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], "Set door hack time", actor: a => a.Props.Set("iHackingTime", SvJsonNode.Integer(seconds)));
    }
}
