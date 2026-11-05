using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static string[] MatchKeys => ["id", "ids", "where", "players", "outside", "inside", "win", "clear"];

    private static string[] OutfitKeys => ["team", "body", "head", "model", "goggles", "cosmetics", "clear"];

    /// <summary>
    /// match: player-count settings on actors (SVMap 1.2 <c>sim/match.json</c>). <c>players</c> [min, max] (0 for no
    /// limit, <c>"any"</c> or null for every count), <c>outside</c>/<c>inside</c> <c>"hidden"</c>, <c>"normal"</c> or
    /// <c>{"key": n}</c> (null for the default), <c>win</c> a mission's targets for 1v1 to 12v12 (0 keeps its own
    /// number, null clears them). Only the members given change; <c>clear</c> removes everything.
    /// </summary>
    private static void MatchOp(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        foreach (ObjectRef t in targets.Where(t => t.Kind != ObjectKind.Actor))
        {
            throw new ApiException($"{t.Id} is not an actor; only actors have player ranges", "id");
        }

        bool clear = ApiJson.GetBool(op, "clear", false);
        PlayerRange? players = !op.ContainsKey("players") ? null
            : op["players"] is SvJsonString { Value: "any" } || ApiJson.IsNull(op["players"]) ? PlayerRange.Any
            : Read("players", () => MatchJson.ReadRange(op["players"]!));
        (bool Given, Hold? Value) outside = HoldMember(op, "outside");
        (bool Given, Hold? Value) inside = HoldMember(op, "inside");
        if (outside.Value is { Kind: HoldKind.Normal })
        {
            throw new ApiException("outside its range an actor cannot work as normal; give \"hidden\" or a mover key", "outside");
        }

        int[]? win = op["win"] is { } w && !ApiJson.IsNull(w) ? Read("win", () => MatchJson.ReadWin(w)) : null;
        bool winGiven = op.ContainsKey("win");
        if (!clear && players is null && !outside.Given && !inside.Given && !winGiven)
        {
            throw new ApiException("give players, outside, inside, win or clear", "players");
        }

        foreach (ObjectRef t in targets)
        {
            Actor a = ctx.Project.FindActor(t.Id)!;
            bool mover = MatchPlan.IsMover(a);
            if (!mover && (outside.Value is { Kind: HoldKind.Key } || inside.Value is { Kind: HoldKind.Key }))
            {
                throw new ApiException($"{a.Id} is not a mover; key states are for movers", outside.Value is { Kind: HoldKind.Key } ? "outside" : "inside");
            }

            if (winGiven && win is not null && !MatchPlan.IsMission(a))
            {
                ctx.Warn("win", $"{a.Id} is not a mission; its win targets are left out of the map");
            }

            if ((players is { IsAny: false } || outside.Given || inside.Given) && MatchPlan.IsRenderOnly(a))
            {
                ctx.Warn("players", $"{a.Id} ({a.Class}) is drawn and lit into the map: a player range on it is a build error");
            }
        }

        ctx.Edit(targets, clear ? "Clear player range" : "Player range", actor: a =>
        {
            if (clear)
            {
                a.Match = null;
                return;
            }

            ActorMatch m = a.Match ?? new ActorMatch(PlayerRange.Any);
            m = m with
            {
                Players = players ?? m.Players,
                Outside = outside.Given ? outside.Value : m.Outside,
                Inside = inside.Given ? inside.Value : m.Inside,
                Win = winGiven ? win : m.Win,
            };
            a.Match = m.OrNull();
        });
    }

    /// <summary>
    /// outfits: what a team wears on this map (SVMap 1.2 <c>render/outfits.json</c>). <c>team</c> spy or merc;
    /// <c>body</c>/<c>head</c> a PNG texture, <c>model</c> <c>{"glb": path}</c> or <c>{"stock": "SPerso.ATT_01"}</c>,
    /// <c>goggles</c> <c>{"bone", "offset"}</c> for the model, <c>cosmetics</c> map, player or none; null removes a
    /// member, <c>clear</c> the team's outfit. Files outside the map are copied into <c>assets/outfits/</c> when the
    /// change commits.
    /// </summary>
    private static void OutfitsOp(ChangeContext ctx, SvJsonObject op)
    {
        bool spy = ApiJson.GetString(op, "team")?.ToLowerInvariant() switch
        {
            "spy" or "spies" => true,
            "merc" or "mercs" => false,
            _ => throw new ApiException("team is spy or merc", "team"),
        };
        ctx.SnapshotProject();
        TeamOutfit? team = ctx.Project.Outfits.For(spy) ?? new TeamOutfit(null, null, null);
        if (ApiJson.GetBool(op, "clear", false))
        {
            team = null;
        }

        if (team is not null)
        {
            if (op.ContainsKey("body"))
            {
                team = team with { Body = Slot(ctx, op["body"], "body") };
            }

            if (op.ContainsKey("head"))
            {
                team = team with { Head = Slot(ctx, op["head"], "head") };
            }

            if (op.ContainsKey("model"))
            {
                team = team with { Model = Model(ctx, op["model"], team.Model) };
            }

            if (op["goggles"] is { } goggles && !ApiJson.IsNull(goggles))
            {
                if (team.Model is not { } model)
                {
                    throw new ApiException("goggles place the goggle glow on an outfit model; give model first", "goggles");
                }

                team = team with { Model = Goggles(goggles, model) };
            }

            if (ApiJson.GetString(op, "cosmetics") is { } policy)
            {
                team = OutfitsDoc.TryParsePolicy(policy, out OutfitCosmetics cosmetics)
                    ? team with { Cosmetics = cosmetics }
                    : throw new ApiException("cosmetics is map, player or none", "cosmetics");
            }
        }

        team = team is { IsEmpty: false } ? team : null;
        MapOutfits outfits = spy ? ctx.Project.Outfits with { Spy = team } : ctx.Project.Outfits with { Merc = team };
        foreach (OutfitProblem problem in ProjectOutfits.Problems(outfits, ctx.Folder, Services.EditorServices.ContentRoot).Where(p => p.Code != "outfit.file"))
        {
            ctx.Warn("model", problem.Message);
        }

        ctx.Run(new ProjectSettingsCommand($"{ProjectOutfits.TeamName(spy)} outfit", p => p.Outfits = outfits), touchesBrushes: false);
    }

    private static OutfitSlot? Slot(ChangeContext ctx, SvJsonNode? node, string field)
    {
        if (ApiJson.IsNull(node))
        {
            return null;
        }

        string path = node is SvJsonString s ? s.Value : throw new ApiException("a PNG texture path (or null to remove it)", field);
        return new OutfitSlot(AdoptFile(ctx, path, ".png", field));
    }

    private static OutfitModel? Model(ChangeContext ctx, SvJsonNode? node, OutfitModel? old)
    {
        if (ApiJson.IsNull(node))
        {
            return null;
        }

        string bone = old?.GoggleBone ?? OutfitsDoc.DefaultGoggleBone;
        FVector offset = old?.GoggleOffset ?? default;
        return node switch
        {
            SvJsonObject o when ApiJson.GetString(o, "glb") is { } glb => new OutfitModel(AdoptFile(ctx, glb, ".glb", "model.glb"), null, bone, offset),
            SvJsonObject o when ApiJson.GetString(o, "stock") is { } stock => ProjectOutfits.SharedGlb(stock, Services.EditorServices.ContentRoot) is null
                ? throw new ApiException($"{stock} is not a shared skeletal mesh (Package.Mesh under shared/chars)", "model.stock")
                : new OutfitModel(null, stock, bone, offset),
            _ => throw new ApiException("model is {\"glb\": path} or {\"stock\": \"Package.Mesh\"} (or null to remove it)", "model"),
        };
    }

    private static OutfitModel Goggles(SvJsonNode node, OutfitModel model)
    {
        if (node is not SvJsonObject g)
        {
            throw new ApiException("goggles is {\"bone\": name, \"offset\": [x, y, z]}", "goggles");
        }

        string bone = ApiJson.GetString(g, "bone") ?? model.GoggleBone;
        FVector offset = model.GoggleOffset;
        if (g["offset"] is { } o)
        {
            Vec3 v = ApiJson.RequireVec(o, "goggles.offset");
            offset = new FVector((float)v.X, (float)v.Y, (float)v.Z);
        }

        return model with { GoggleBone = bone, GoggleOffset = offset };
    }

    /// <summary>The stored path of an outfit file, queueing its copy into the map folder.</summary>
    private static string AdoptFile(ChangeContext ctx, string path, string extension, string field)
    {
        string full = Path.IsPathRooted(path) || ctx.Folder is null ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(ctx.Folder, path));
        if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new ApiException($"{path} is not a {extension} file", field);
        }

        (string stored, List<(string From, string To)> copies) = ProjectOutfits.Adopt(full, ctx.Folder, ctx.PendingCopies);
        ctx.PendingCopies.AddRange(copies);
        return stored;
    }

    private static (bool Given, Hold? Value) HoldMember(SvJsonObject op, string field) =>
        !op.ContainsKey(field) ? (false, null)
        : ApiJson.IsNull(op[field]) || op[field] is SvJsonString { Value: "default" } ? (true, null)
        : (true, Read(field, () => MatchJson.ReadHold(op[field]!, field)));

    private static T Read<T>(string field, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (FormatException ex)
        {
            throw new ApiException(ex.Message, field);
        }
    }
}
