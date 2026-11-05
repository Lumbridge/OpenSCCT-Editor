using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// A few fast checks the scripting API runs when no Map Check service is loaded (and that the summary always reports):
/// duplicate ids, missing references, unheard events, team starts, actors in solid, floating player starts.
/// </summary>
internal static class BasicChecks
{
    public static List<Finding> Run(MapProject p, string? folder = null)
    {
        var found = new List<Finding>();
        foreach (IGrouping<string, ObjectRef> dup in p.AllRefs().GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            found.Add(Finding.Error("id-duplicate", $"{dup.Count()} objects share the id {dup.Key}", dup.ToArray()));
        }

        var ids = new HashSet<string>(p.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors)
        {
            foreach (string missing in ChangeApplier.References(a.Props).Where(r => !ids.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                found.Add(Finding.Warning("ref-missing", $"{a.Id} references {missing}, which is not in the map", ObjectRef.Actor(a.Id)));
            }
        }

        foreach ((string from, string ev) in EventGraph.Build(p).Unheard)
        {
            found.Add(Finding.Info("event-unheard", $"{from} fires {ev}, which no actor's Tag answers", ObjectRef.Actor(from)));
        }

        var starts = p.Actors.Where(a => MapObjects.ClassMatches(a.Class, "PlayerStart", subclasses: true)).ToList();
        foreach (int team in new[] { 0, 1 })
        {
            if (!starts.Any(s => s.Props.GetInt("TeamNumber") == team))
            {
                found.Add(Finding.Warning("starts-missing", $"no player start for team {team} ({(team == 0 ? "spies" : "mercs")})"));
            }
        }

        var geo = new WorldGeometry(p, folder);
        if (!geo.HasGeometry)
        {
            found.Add(Finding.Warning("no-geometry", "the map has no brushes and no baked world"));
            return found;
        }

        if (BakedContent.Collision(p).WorldSolid && geo.BrushCount > 0)
        {
            // Solid just above and just below too, so actors standing on a floor or hanging on a ceiling pass.
            foreach (Actor a in p.Actors.Where(a => a.Shape is null && geo.IsSolid(a.Location)
                         && geo.IsSolid(a.Location + new Vec3(0, 0, 1)) && geo.IsSolid(a.Location - new Vec3(0, 0, 1))))
            {
                found.Add(Finding.Warning("actor-in-solid", $"{a.Id} ({a.ShortClass}) is inside solid geometry", ObjectRef.Actor(a.Id)).At(a.Location));
            }
        }

        foreach (Actor s in starts)
        {
            double height = MapObjects.CollisionSize(s).Height;
            RayHit? floor = geo.Raycast(s.Location, new Vec3(0, 0, -1), 4096);
            if (floor is null || floor.Distance - height > 64)
            {
                string how = floor is null ? "has no floor below" : $"floats {ApiJson.Inv(floor.Distance - height)} above the floor";
                found.Add(Finding.Warning("start-floating", $"{s.Id} {how}", ObjectRef.Actor(s.Id)).At(s.Location));
            }
        }

        return found;
    }
}
