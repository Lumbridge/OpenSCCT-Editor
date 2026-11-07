using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static readonly string[] s_playKeys = ["side", "mode", "bots", "rules", "players", "difficulty", "smoke"];

    private static readonly (string Key, Func<MovementLimits, double> Get, Action<MovementLimits, double> Set, double Min, double Max)[] s_movement =
    [
        ("stepHeight", m => m.StepHeight, (m, v) => m.StepHeight = v, 1, 512),
        ("walkableSlope", m => m.WalkableSlopeDegrees, (m, v) => m.WalkableSlopeDegrees = v, 1, 89),
        ("standHeight", m => m.StandHeight, (m, v) => m.StandHeight = v, 16, 1024),
        ("spyCrouchHeight", m => m.SpyCrouchHeight, (m, v) => m.SpyCrouchHeight = v, 16, 1024),
        ("mercCrouchHeight", m => m.MercCrouchHeight, (m, v) => m.MercCrouchHeight = v, 16, 1024),
        ("radius", m => m.Radius, (m, v) => m.Radius = v, 1, 512),
        ("spyRunSpeed", m => m.SpyRunSpeed, (m, v) => m.SpyRunSpeed = v, 1, 10000),
        ("mercRunSpeed", m => m.MercRunSpeed, (m, v) => m.MercRunSpeed = v, 1, 10000),
    ];

    /// <summary>workspace: the editor-only settings saved with the map: <c>grid</c>, <c>rotationGrid</c>,
    /// <c>camera</c> {at, rot|rotDeg|look}, <c>play</c> {side, mode, bots, rules, players, difficulty},
    /// <c>movement</c> {stepHeight, walkableSlope, standHeight, spyCrouchHeight, mercCrouchHeight, radius, spyRunSpeed,
    /// mercRunSpeed}, <c>storeys</c> (the map's own storeys, [{name, bottom, top}]; [] goes back to detected ones) and
    /// <c>shown</c> (the storey the views show: a name, a number from 1 for the lowest, or "all").</summary>
    private static void WorkspaceOp(ChangeContext ctx, SvJsonObject op)
    {
        var edits = new List<Action<Workspace>>();
        if (op["grid"] is { } grid)
        {
            double g = ApiJson.RequireNumber(grid, "grid");
            if (g is < 1 or > 4096)
            {
                throw new ApiException("grid is 1..4096", "grid");
            }

            edits.Add(w => w.GridSize = g);
        }

        if (op["rotationGrid"] is { } rg)
        {
            int r = (int)ApiJson.RequireNumber(rg, "rotationGrid");
            if (r is < 1 or > 32768)
            {
                throw new ApiException("rotationGrid is 1..32768 rotator units", "rotationGrid");
            }

            edits.Add(w => w.RotationGrid = r);
        }

        if (op["camera"] is { } cam)
        {
            SvJsonObject c = Members(cam, "camera");
            Known(c, ["at", "rot", "rotDeg", "look"], "camera");
            Vec3? at = c["at"] is { } a ? ctx.Point(a, "camera.at") : null;
            Rot? rot = ViewRotation(ctx, c, at ?? ctx.Project.Workspace.CameraLocation, "camera");
            edits.Add(w =>
            {
                w.CameraLocation = at ?? w.CameraLocation;
                w.CameraRotation = rot ?? w.CameraRotation;
            });
        }

        if (op["play"] is { } play)
        {
            SvJsonObject o = Members(play, "play");
            Known(o, s_playKeys, "play");
            string? side = ApiJson.GetString(o, "side");
            if (side is not null and not ("spy" or "merc"))
            {
                throw new ApiException("play.side is spy or merc", "play.side");
            }

            string? rules = ApiJson.GetString(o, "rules");
            if (rules is not null and not ("playlevel" or "map"))
            {
                throw new ApiException("play.rules is playlevel (the editor's Play Level rules) or map (the map's own settings)", "play.rules");
            }

            string? mode = ApiJson.GetString(o, "mode");
            if (mode is not null && !ctx.Project.MapSettings.ContainsKey(mode))
            {
                ctx.Warn("play.mode", $"the map has no settings for {mode} (it has {string.Join(", ", ctx.Project.MapSettings.Keys)})");
            }

            string? difficulty = ApiJson.GetString(o, "difficulty")?.ToLowerInvariant();
            if (difficulty is not null && !s_difficulties.Contains(difficulty))
            {
                throw new ApiException($"play.difficulty is {string.Join(", ", s_difficulties)}", "play.difficulty");
            }

            string? smoke = ApiJson.GetString(o, "smoke")?.ToLowerInvariant();
            if (smoke is not null && !PlayOptions.SmokeModes.Contains(smoke))
                throw new ApiException($"play.smoke is {string.Join(", ", PlayOptions.SmokeModes)}", "play.smoke");

            int? bots = o["bots"] is { } b ? (int)ApiJson.RequireNumber(b, "play.bots") : null;
            int? players = o["players"] is { } pl ? (int)ApiJson.RequireNumber(pl, "play.players") : null;
            if (bots is < 0 or > 15 || players is < 0 or > MatchRuleSet.MostPlayers)
            {
                throw new ApiException($"play.bots is 0..15 and play.players 0..{MatchRuleSet.MostPlayers} (0 counts the bots and you)",
                    bots is < 0 or > 15 ? "play.bots" : "play.players");
            }

            edits.Add(w =>
            {
                w.Play.Side = side ?? w.Play.Side;
                w.Play.Mode = mode ?? w.Play.Mode;
                w.Play.Rules = rules ?? w.Play.Rules;
                w.Play.Bots = bots ?? w.Play.Bots;
                w.Play.Players = players ?? w.Play.Players;
                w.Play.Difficulty = difficulty ?? w.Play.Difficulty;
                w.Play.Smoke = smoke ?? w.Play.Smoke;
            });
        }

        if (op["movement"] is { } movement)
        {
            SvJsonObject o = Members(movement, "movement");
            Known(o, s_movement.Select(m => m.Key).ToArray(), "movement");
            foreach (var m in s_movement)
            {
                if (o[m.Key] is { } node)
                {
                    double v = ApiJson.RequireNumber(node, $"movement.{m.Key}");
                    if (v < m.Min || v > m.Max)
                    {
                        throw new ApiException($"movement.{m.Key} is {m.Min}..{m.Max}", $"movement.{m.Key}");
                    }

                    edits.Add(w => m.Set(w.Movement, v));
                }
            }
        }

        if (op["storeys"] is { } storeys)
        {
            List<StoreyRange> list = Storeys(storeys);
            edits.Add(w => w.Storeys = list.Select(s => s.Clone()).ToList());
        }

        if (op["shown"] is { } shown)
        {
            string spec = shown is SvJsonNumber ? ((int)ApiJson.RequireNumber(shown, "shown")).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : ApiJson.GetString(op, "shown") ?? throw new ApiException("shown is a storey's name or number, or \"all\"", "shown");
            // The storeys the map has once this op's own storey list (if any) is in place.
            var after = new MapProject { Brushes = ctx.Project.Brushes, Baked = ctx.Project.Baked, Workspace = ctx.Project.Workspace.Clone() };
            edits.ForEach(e => e(after.Workspace));
            IReadOnlyList<Geometry.Storey> list = Geometry.Storeys.Of(after, ctx.Folder);
            int index = FindStorey(list, spec, "shown");
            double? level = index < 0 ? null : list[index].Level;
            edits.Add(w => w.ShownStorey = level);
        }

        if (edits.Count == 0)
        {
            throw new ApiException("give grid, rotationGrid, camera, play, movement, storeys or shown", "grid");
        }

        ctx.SnapshotProject();
        ctx.Run(WorkspaceEdits.Edit("Workspace", w => edits.ForEach(e => e(w))), touchesBrushes: false);
    }

    /// <summary>The bot difficulty names OpenSCCT takes (<c>--difficulty=</c>, its AiSkill presets).</summary>
    private static readonly string[] s_difficulties = Enum.GetNames<OpenScct.Game.AI.AiSkill>().Select(n => n.ToLowerInvariant()).ToArray();

    /// <summary>The bot difficulty names, easiest first.</summary>
    public static IReadOnlyList<string> Difficulties => s_difficulties;

    /// <summary>A storey list: [{name, bottom, top}], names unique, each top above its bottom.</summary>
    private static List<StoreyRange> Storeys(SvJsonNode node)
    {
        if (node is not SvJsonArray items)
        {
            throw new ApiException("storeys is a list of {name, bottom, top} ([] to use the detected storeys)", "storeys");
        }

        var list = new List<StoreyRange>();
        for (int i = 0; i < items.Count; i++)
        {
            string field = $"storeys[{i}]";
            SvJsonObject o = Members(items[i], field);
            Known(o, ["name", "bottom", "top"], field);
            string name = ApiJson.GetString(o, "name") ?? $"Floor {i + 1}";
            double bottom = ApiJson.RequireNumber(o["bottom"] ?? throw new ApiException("bottom is the storey's lowest height", $"{field}.bottom"), $"{field}.bottom");
            double top = ApiJson.RequireNumber(o["top"] ?? throw new ApiException("top is the height the storey ends at", $"{field}.top"), $"{field}.top");
            if (top <= bottom)
            {
                throw new ApiException("top must be above bottom", $"{field}.top");
            }

            if (list.Any(s => LayerEdits.Same(s.Name, name)))
            {
                throw new ApiException($"two storeys are named {name}", $"{field}.name");
            }

            list.Add(new StoreyRange { Name = name, Bottom = bottom, Top = top });
        }

        return list;
    }

    /// <summary>bookmark: stores a camera view by <c>name</c>: <c>at</c> (where the camera stands), <c>rot</c>/
    /// <c>rotDeg</c> or <c>look</c> (a point to look at), <c>note</c>; or <c>rename</c>s or <c>delete</c>s it.</summary>
    private static void BookmarkOp(ChangeContext ctx, SvJsonObject op)
    {
        string name = ApiJson.GetString(op, "name") ?? throw new ApiException("name is the bookmark's name", "name");
        Bookmark? existing = ctx.Project.Workspace.Bookmarks.Find(b => LayerEdits.Same(b.Name, name));
        ctx.SnapshotProject();
        if (ApiJson.GetBool(op, "delete", false))
        {
            if (existing is null)
            {
                throw new ApiException($"no bookmark {name}", "name");
            }

            ctx.Run(WorkspaceEdits.Bookmarks($"Remove bookmark {name}", list => list.RemoveAll(b => LayerEdits.Same(b.Name, name))), touchesBrushes: false);
            return;
        }

        string? rename = ApiJson.GetString(op, "rename");
        if (rename is not null && (existing is null || rename.Length == 0
            || ctx.Project.Workspace.Bookmarks.Any(b => !ReferenceEquals(b, existing) && LayerEdits.Same(b.Name, rename))))
        {
            throw new ApiException(existing is null ? $"no bookmark {name} to rename" : $"bookmark {rename} exists", existing is null ? "name" : "rename");
        }

        Vec3? at = op["at"] is { } a ? ctx.Point(a, "at") : null;
        if (existing is null && at is null)
        {
            throw new ApiException("a new bookmark needs at (where the camera stands)", "at");
        }

        Vec3 loc = at ?? existing!.Location;
        Rot rot = ViewRotation(ctx, op, loc, "") ?? existing?.Rotation ?? Rot.Zero;
        string? note = op["note"] is { } n ? (n is SvJsonString s ? s.Value : null) : existing?.Note;
        ctx.Run(WorkspaceEdits.Bookmarks($"Bookmark {name}", list =>
        {
            int i = list.FindIndex(b => LayerEdits.Same(b.Name, name));
            var bm = new Bookmark { Name = rename ?? (i >= 0 ? list[i].Name : name), Location = loc, Rotation = rot, Note = note };
            if (i >= 0)
            {
                list[i] = bm;
            }
            else
            {
                list.Add(bm);
            }
        }), touchesBrushes: false);
    }

    /// <summary>
    /// reference: a reference image over a 2D view, picked by <c>name</c> (or by <c>path</c> when one image uses that
    /// file): <c>path</c> (the file; a new image needs one), <c>view</c> (top, front, side), <c>origin</c>,
    /// <c>unitsPerPixel</c>, <c>opacity</c> (0..1), <c>rename</c>, <c>calibrate</c> {from, to, length} (two marks on
    /// the image as drawn and their real distance: scales the image about <c>from</c>); or <c>delete</c>.
    /// </summary>
    private static void ReferenceOp(ChangeContext ctx, SvJsonObject op)
    {
        List<ReferenceImage> images = ctx.Project.Workspace.References;
        string? name = ApiJson.GetString(op, "name");
        string? path = ApiJson.GetString(op, "path");
        if (name is null && path is null)
        {
            throw new ApiException("name the image (name), or give its file (path)", "name");
        }

        ReferenceImage? existing;
        if (name is not null)
        {
            existing = images.Find(r => LayerEdits.Same(r.Name, name));
        }
        else
        {
            List<ReferenceImage> users = images.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (users.Count > 1)
            {
                throw new ApiException($"{users.Count} images use {path} ({string.Join(", ", users.Select(r => r.Name))}): give name", "name");
            }

            existing = users.FirstOrDefault();
        }

        ctx.SnapshotProject();
        if (ApiJson.GetBool(op, "delete", false))
        {
            if (existing is null)
            {
                throw new ApiException($"no reference image {name ?? path}", name is null ? "path" : "name");
            }

            string gone = existing.Name;
            ctx.Run(WorkspaceEdits.References($"Remove reference {gone}", list => list.RemoveAll(r => LayerEdits.Same(r.Name, gone))), touchesBrushes: false);
            return;
        }

        if (existing is null && path is null)
        {
            throw new ApiException($"no reference image {name}: give path to add it", "path");
        }

        string? view = ApiJson.GetString(op, "view");
        if (view is not null and not ("top" or "front" or "side"))
        {
            throw new ApiException("view is top, front or side", "view");
        }

        Vec3? origin = op["origin"] is { } o ? ctx.Point(o, "origin") : null;
        double? scale = op["unitsPerPixel"] is { } u ? ApiJson.RequireNumber(u, "unitsPerPixel") : null;
        double? opacity = op["opacity"] is { } op2 ? ApiJson.RequireNumber(op2, "opacity") : null;
        if (scale is <= 0 || opacity is < 0 or > 1)
        {
            throw new ApiException("unitsPerPixel is above 0 and opacity 0..1", scale is <= 0 ? "unitsPerPixel" : "opacity");
        }

        string key = existing?.Name ?? name ?? ReferenceImage.NewName(images, path!);
        string? rename = ApiJson.GetString(op, "rename");
        if (rename is not null && (rename.Trim().Length == 0 || images.Any(r => r != existing && LayerEdits.Same(r.Name, rename))))
        {
            throw new ApiException(rename.Trim().Length == 0 ? "rename needs a name" : $"another image is already called {rename}", "rename");
        }

        // Calibration works on the image as the rest of this op leaves it (its view and size).
        var after = existing?.Clone() ?? new ReferenceImage { Path = path! };
        after.View = view ?? after.View;
        after.Origin = origin ?? after.Origin;
        after.UnitsPerPixel = scale ?? after.UnitsPerPixel;
        if (op["calibrate"] is { } cal)
        {
            SvJsonObject c = Members(cal, "calibrate");
            Known(c, ["from", "to", "length"], "calibrate");
            Vec3 from = ctx.Point(c["from"] ?? throw new ApiException("from is a mark on the image", "calibrate.from"), "calibrate.from");
            Vec3 to = ctx.Point(c["to"] ?? throw new ApiException("to is a second mark on the image", "calibrate.to"), "calibrate.to");
            double length = ApiJson.RequireNumber(c["length"] ?? throw new ApiException("length is the real distance between the marks", "calibrate.length"), "calibrate.length");
            try
            {
                (after.Origin, after.UnitsPerPixel) = after.Calibrate(from, to, length);
            }
            catch (ArgumentException e)
            {
                throw new ApiException(e.Message, "calibrate");
            }
        }

        ctx.Run(WorkspaceEdits.References($"Reference {key}", list =>
        {
            ReferenceImage r = list.Find(x => LayerEdits.Same(x.Name, key)) ?? Add(list, new ReferenceImage { Name = key });
            r.Name = rename?.Trim() ?? r.Name;
            r.Path = path ?? r.Path;
            r.View = after.View;
            r.Origin = after.Origin;
            r.UnitsPerPixel = after.UnitsPerPixel;
            r.Opacity = opacity ?? r.Opacity;
        }), touchesBrushes: false);

        static ReferenceImage Add(List<ReferenceImage> list, ReferenceImage r)
        {
            list.Add(r);
            return r;
        }
    }

    /// <summary>A view rotation from <c>rot</c> (units), <c>rotDeg</c> or <c>look</c> (a point), or null.</summary>
    private static Rot? ViewRotation(ChangeContext ctx, SvJsonObject o, Vec3 from, string prefix)
    {
        string P(string k) => prefix.Length == 0 ? k : $"{prefix}.{k}";
        if (o["look"] is { } look)
        {
            return RotatorMath.Tidy(RotatorMath.LookAt(from, ctx.Point(look, P("look"))));
        }

        if (o["rot"] is { } r)
        {
            return ApiJson.TryRot(r, out Rot rot) ? RotatorMath.Tidy(rot) : throw new ApiException("expected [pitch,yaw,roll]", P("rot"));
        }

        if (o["rotDeg"] is { } rd)
        {
            return ApiJson.TryRot(rd, out Rot rot, Rot.UnitsPerTurn / 360.0) ? RotatorMath.Tidy(rot) : throw new ApiException("expected [pitch,yaw,roll] in degrees", P("rotDeg"));
        }

        return null;
    }

    /// <summary>Rejects members of a sub-object that are not in <paramref name="keys"/>.</summary>
    private static void Known(SvJsonObject o, string[] keys, string field)
    {
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (Array.IndexOf(keys, kv.Key) < 0)
            {
                throw new ApiException($"{field} has no field '{kv.Key}' ({string.Join(", ", keys)})", $"{field}.{kv.Key}");
            }
        }
    }
}
