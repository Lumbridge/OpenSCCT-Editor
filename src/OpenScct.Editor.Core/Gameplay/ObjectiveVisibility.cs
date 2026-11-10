using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Visibility of inactive devices in authored OpenSCCT maps, separate from objective availability.</summary>
public static class ObjectiveVisibility
{
    public const string Property = "HideWhileInactive";

    public static bool Hidden(MapProject project, Actor target)
    {
        if (target.Props[Property] is { Kind: SvJsonKind.True or SvJsonKind.False } own)
            return own.Kind == SvJsonKind.True;
        if (!MissionLinks.IsTrigger(target)) return false;
        List<Actor> owners = project.Actors.Where(a => MissionLinks.IsObjective(a) && MissionLinks.Contains(a, "Triggers", target.Id)).ToList();
        // A shared device stays visible if any of its objectives wants it shown. A device override is definitive.
        return owners.Count > 0 && owners.All(a => a.Props.GetBool(Property));
    }
}
