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
    /// <summary>Editing another stop is editor state. All compiled systems must agree on the round's start pose.</summary>
    public static MapProject AtStart(MapProject project)
    {
        var poses = project.Actors.Where(MoverBase.IsMover).Select(a => (Actor: a, Setup: MoverSetup.Read(a)))
            .Select(m => (m.Actor.Id, Pose: m.Setup.KeyPose(m.Setup.StartKey), Actor: m.Actor))
            .Where(m => m.Actor.Location != m.Pose.Location || m.Actor.Rotation != m.Pose.Rotation).ToList();
        if (poses.Count == 0) return project;
        var copy = Session.ProjectSnapshot.Take(project);
        foreach (var (id, pose, _) in poses)
        {
            Actor a = copy.FindActor(id)!;
            a.Location = pose.Location;
            a.Rotation = pose.Rotation;
            a.Editor.MoverKey = null;
        }
        return copy;
    }

    public static void Write(CompileContext ctx)
    {
        // A baked source's own file came along with it; the project's movers replace it.
        ctx.Files.Remove(MoversDoc.RelativePath);
        ctx.Files.Remove(MoverPathsDoc.RelativePath);
        var paths = new List<(string, MoverPath)>();
        foreach (Actor a in ctx.Project.Actors.Where(a => MoverBase.IsMover(a) && ctx.GameplayIds.Contains(a.Id)))
        {
            double count = Gameplay.ClassDefaults.Number(a, "NumKeys", 2);
            if (count < 1 || count > MoverSetup.MaxKeys || count != Math.Truncate(count))
            {
                ctx.Error("mover.path-size", $"{a.Id}: a mover needs 1 to {MoverSetup.MaxKeys} stops", ObjectRef.Actor(a.Id));
                continue;
            }
            MoverSetup m = MoverSetup.Read(a);
            if (m.Keys.Count > MoverSetup.OriginalMaxKeys)
                paths.Add((a.Id, new MoverPath(m.Keys.Select(k => new MoverPathKey(new((float)k.Offset.X, (float)k.Offset.Y, (float)k.Offset.Z),
                    new(k.Turn.Pitch, k.Turn.Yaw, k.Turn.Roll))).ToArray(), m.StartKey)));
        }
        if (paths.Count > 0)
        {
            ctx.Files[MoverPathsDoc.RelativePath] = CanonicalJson.ToFileBytes(MoverPathsDoc.ToJson(paths));
            ctx.Count("movers.paths", paths.Count);
        }
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
