using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// SVMap 1.2's optional files: <c>sim/match.json</c> (the actors' player-count rules and the missions' win targets)
/// and <c>render/outfits.json</c> with the outfit textures and models it names.
/// </summary>
public static class MatchBuild
{
    public static void Write(CompileContext ctx)
    {
        // A baked source's own outfits came along with its render files; the project's outfits replace them.
        ctx.Files.Remove(OutfitsDoc.RelativePath);
        if (ctx.Baked is { } baked)
        {
            foreach (string file in ProjectOutfits.SourceFiles(baked.Folder))
            {
                ctx.Files.Remove(file);
            }
        }

        WriteMatch(ctx);
        WriteOutfits(ctx);
    }

    private static void WriteMatch(CompileContext ctx)
    {
        MatchRuleSet all = MatchPlan.RuleSet(ctx.Project);
        if (all.IsEmpty)
        {
            return;
        }

        var rules = new List<MatchRule>();
        foreach (MatchRule rule in all.Rules)
        {
            if (ctx.Project.FindActor(rule.Actor) is { } conveyor && ConveyorDesign.Is(conveyor))
            {
                // Svm.Conveyor's belt is world collision and world.glb instances with no actor id: a range cannot take it away.
                ctx.Error("match.conveyor", $"{rule.Actor} is a conveyor with a player range, but its belt draws, collides and carries at every player count; " +
                    "gate the trigger or mover that switches it instead", ObjectRef.Actor(rule.Actor));
            }
            else if (ctx.GameplayIds.Contains(rule.Actor))
            {
                rules.Add(rule);
            }
            else if (ctx.Render.Any(a => string.Equals(a.Id, rule.Actor, StringComparison.OrdinalIgnoreCase)))
            {
                // Lights and static meshes are baked into the world's lighting and shadows once for every player count.
                ctx.Error("match.render-only", $"{rule.Actor} has a player range but is drawn and lit into the map; gate a mover or another gameplay actor instead",
                    ObjectRef.Actor(rule.Actor));
            }
            else
            {
                ctx.Warn("match.dropped", $"{rule.Actor} has a player range but is not in the map; the range is left out", ObjectRef.Actor(rule.Actor));
            }
        }

        var win = new List<WinTargets>();
        foreach (WinTargets w in all.Win)
        {
            if (ctx.Project.FindActor(w.Mission) is { } m && MatchPlan.IsMission(m) && ctx.GameplayIds.Contains(m.Id))
            {
                win.Add(w);
            }
            else
            {
                ctx.Warn("match.win", $"{w.Mission} has win targets but is not a mission in the map; they are left out", ObjectRef.Actor(w.Mission));
            }
        }

        // As Map Check: a top mission that can be left with nothing to play cannot be won at those counts.
        foreach (string problem in MatchLayout.Problems(MatchPlan.Missions(ctx.Project), all))
        {
            ctx.Error("match.problems", problem);
        }

        var set = new MatchRuleSet(rules, win);
        if (!set.IsEmpty)
        {
            ctx.Files[MatchDoc.RelativePath] = CanonicalJson.ToFileBytes(MatchDoc.ToJson(set));
            ctx.Count("match.rules", rules.Count);
        }
    }

    private static void WriteOutfits(CompileContext ctx)
    {
        MapOutfits outfits = ctx.Project.Outfits;
        if (outfits.IsEmpty)
        {
            return;
        }

        foreach (OutfitProblem p in ProjectOutfits.Problems(outfits, ctx.Options.ProjectFolder, ctx.ContentRoot))
        {
            if (p.Error)
            {
                ctx.Error(p.Code, p.Message);
            }
            else
            {
                ctx.Warn(p.Code, p.Message);
            }
        }

        foreach ((string path, byte[] data) in ProjectOutfits.Build(outfits, ctx.MapId, ctx.Options.ProjectFolder))
        {
            ctx.Files[path] = data;
        }
    }
}
