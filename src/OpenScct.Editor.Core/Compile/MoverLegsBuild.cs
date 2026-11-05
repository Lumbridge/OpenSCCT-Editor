using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using Actor = OpenScct.Editor.Core.Model.Actor;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// SVMap 1.2's optional <c>sim/movers.json</c>: the movers' own time per leg and easing per key
/// (<see cref="Actor.Legs"/>), which OpenSCCT's server and clients read at load.
/// </summary>
public static class MoverLegsBuild
{
    public static void Write(CompileContext ctx)
    {
        // A baked source's own file came along with it; the project's movers replace it.
        ctx.Files.Remove(MoversDoc.RelativePath);
        var movers = new List<(string, MoverLegs)>();
        foreach (Actor a in ctx.Project.Actors)
        {
            if (a.Legs is not { } legs)
            {
                continue;
            }

            if (!MatchPlan.IsMover(a) || !ctx.GameplayIds.Contains(a.Id))
            {
                ctx.Warn("mover.legs-dropped", $"{a.Id} has its own leg times or easing but is not a mover in the map; they are left out", ObjectRef.Actor(a.Id));
                continue;
            }

            movers.Add((a.Id, legs));
        }

        if (movers.Count > 0)
        {
            ctx.Files[MoversDoc.RelativePath] = CanonicalJson.ToFileBytes(MoversDoc.ToJson(movers));
            ctx.Count("movers.legs", movers.Count);
        }
    }
}
