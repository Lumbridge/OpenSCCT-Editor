using System.Diagnostics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>
/// <c>ose ge</c> (ledge detection, flush, corner join, GE IDs) and <c>ose graph</c> (the event graph and the mission
/// tree). Edits are one undo step each and are saved unless <c>--dry-run</c> is given.
/// </summary>
internal static class GameplayCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("ge", "ge detect <project> [--apply] [--compare] [--all] [--min-drop 40] [--min-length 32] [--headroom 0] [--step 8] " +
            "[--box x,y,z,x,y,z] [--meshes] [--limit 200] [--json]\n" +
            "ge draw <project> --kind LADDER|PIPE|POLE|ZL|HOH|FENCE|LG --from x,y,z --to x,y,z [--facing x,y,z] " +
            "[--name id] [--zone-only] [--width 48] [--radius 4] [--spacing 24] [--material asset]\n" +
            "ge flush <project> [--ids a,b] [--reach 32] | ge join <project> [--ids a,b] [--dist 24] | " +
            "ge probe <project> [--ids a,b] | ge mask <project> --ids a,b --id 4 | ge platform <project> --ids a,b --platform common|ps2|xbox | " +
            "ge activation <project> <0-255>   (edits: [--dry-run] [--json])",
            "GE authoring. detect finds grabbable ledges in the geometry (brushes and baked collision) and lists the ones no " +
            "LG covers yet (--all lists every one, --meshes also looks at a baked map's props, --compare scores them against the map's LGs, --apply adds them as one " +
            "undo step). probe explains, per LG, whether its edge is grabbable and why not. flush moves LGs onto their edges, join closes corner gaps, mask sets GE ID bits, platform the GE platform and activation " +
            "sets LevelInfo.GEActivationMask. draw creates a GE with matching generated geometry (LG uses existing geometry); --zone-only omits its appearance.", GeAsync),
        new("graph", "graph <project> [--id X] [--depth 1] [--dangling] [--rings] [--missions] [--limit 300] [--json]",
            "The event graph with SVMap 1.1 rules (fire, accept and reference links), events nothing receives, event " +
            "rings, and the missions / objectives / triggers tree.", GraphAsync),
    ];

    // ---- ose ge ------------------------------------------------------------------------------------------------

    private static Task<int> GeAsync(CliArgs a)
    {
        EnsureServices();
        string op = a.Positional(0)?.ToLowerInvariant() ?? throw new ArgumentException("give an op: draw, detect, probe, flush, join, mask, platform or activation");
        string folder = a.Positional(1) ?? throw new ArgumentException("give the project folder");
        EditorSession session = EditorSession.Open(folder);
        return Task.FromResult(op switch
        {
            "draw" => Draw(session, a),
            "detect" => Detect(session, a),
            "probe" => Probe(session, a),
            "flush" => Flush(session, a),
            "join" => Join(session, a),
            "mask" => Mask(session, a),
            "platform" => Platform(session, a),
            "activation" => Activation(session, a),
            _ => throw new ArgumentException($"unknown ge op '{op}' (draw, detect, probe, flush, join, mask, platform, activation)"),
        });
    }

    private static int Draw(EditorSession session, CliArgs a)
    {
        string[] allowed = ["kind", "from", "to", "facing", "name", "zone-only", "width", "radius", "spacing", "material", "dry-run", "json", "pretty"];
        if (a.OptionKeys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            throw new ArgumentException($"unknown ge draw option --{unknown}");
        }

        string kind = a.Get("kind")?.ToUpperInvariant() ?? throw new ArgumentException("give --kind LADDER, PIPE, POLE, ZL, HOH, FENCE or LG");
        Vec3 Point(string key)
        {
            string value = a.Get(key) ?? throw new ArgumentException($"give --{key} x,y,z");
            string[] parts = value.Split(',');
            if (parts.Length != 3)
            {
                throw new ArgumentException($"--{key} is x,y,z");
            }

            return new Vec3(Parsing.Number(parts[0], key), Parsing.Number(parts[1], key), Parsing.Number(parts[2], key));
        }

        GeAppearance? appearance = !a.Has("zone-only") && GeGeometry.Supports(kind) ? GeGeometry.Defaults(kind) : null;
        if (appearance is not null)
        {
            appearance.Width = Number(a, "width", appearance.Width);
            appearance.Radius = Number(a, "radius", appearance.Radius);
            appearance.Spacing = Number(a, "spacing", appearance.Spacing);
            appearance.Material = a.Get("material") ?? appearance.Material;
        }
        else if (new[] { "width", "radius", "spacing", "material" }.Any(a.Has))
        {
            throw new ArgumentException("appearance settings need a supported kind without --zone-only");
        }

        Ge ge = GeDrawing.Create(kind, Point("from"), Point("to"), a.Has("facing") ? Point("facing") : null, appearance);
        ge.Id = a.Get("name") ?? GeShapes.IdSource(session.Project)(GeShapes.Stem(ge.Kind));
        if (!OpenScct.Editor.Core.Compile.GameplayBuild.IsIdentifier(ge.Id) || session.Project.Find(ge.Id) is not null)
        {
            throw new ArgumentException("--name must be a unique identifier (letters, digits and _, at most 63 characters)");
        }

        session.Execute(new AddObjectsCommand($"Draw {ge.Id}", ges: [ge]));
        if (!a.Has("dry-run"))
        {
            session.Save();
        }

        var result = new SvJsonObject();
        result.Add("ge", ModelJson.GeToJson(ge));
        result.Add("saved", SvJsonNode.Bool(!a.Has("dry-run")));
        result.Add("warnings", Array(GeShapes.Problems(ge.Kind, ge.Points).Select(p => p.Message)));
        if (a.Has("json"))
        {
            PrintJson(result, a);
        }
        else
        {
            Console.WriteLine($"Drew {ge.Id} ({ge.Kind}){(ge.Appearance is null ? "" : " with geometry")}" + (a.Has("dry-run") ? " (not saved)" : " (saved)"));
            foreach (GeProblem problem in GeShapes.Problems(ge.Kind, ge.Points))
            {
                Console.WriteLine("  " + problem.Message);
            }
        }

        return 0;
    }

    private static LedgeOptions Options(CliArgs a)
    {
        var d = new LedgeOptions();
        return new LedgeOptions
        {
            MinDrop = Number(a, "min-drop", d.MinDrop),
            MinLength = Number(a, "min-length", d.MinLength),
            ClimbHeadroom = Number(a, "headroom", d.ClimbHeadroom),
            Step = Math.Max(2, Number(a, "step", d.Step)),
            JoinDistance = Number(a, "dist", d.JoinDistance),
            Region = a.Get("box") is { } box ? Box(box) : null,
            IncludeMeshes = a.Has("meshes"),
        };
    }

    private static int Detect(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        var watch = Stopwatch.StartNew();
        TraversalWorld world = TraversalWorld.Build(p, session.Folder);
        if (!world.HasGeometry)
        {
            throw new InvalidOperationException("the map has no geometry to detect ledges in (no brushes, no baked collision)");
        }

        LedgeOptions o = Options(a);
        LedgeResult result = LedgeDetector.Detect(world, o);
        List<Ge> ledges = p.Ges.Where(g => g.Kind == GeKinds.LedgeGrab && g.Points.Count >= 2).ToList();
        List<Lip> fresh = result.Lips.Where(l => !GeEdits.Covered(l, ledges)).ToList();
        List<Lip> shown = a.Has("all") ? result.Lips.ToList() : fresh;
        long ms = watch.ElapsedMilliseconds;

        string? applied = null;
        List<string> added = [];
        if (a.Has("apply") && GeEdits.AddLedges(p, fresh) is { } add)
        {
            session.Execute(add);
            added = add.Added.Select(r => r.Id).ToList();
            applied = add.Name;
            if (!a.Has("dry-run"))
            {
                session.Save();
            }
        }

        var o2 = new SvJsonObject();
        o2.Add("map", SvJsonNode.String(p.Id));
        o2.Add("surfaces", SvJsonNode.Integer(world.Surfaces.Count));
        o2.Add("edges", SvJsonNode.Integer(result.Edges));
        o2.Add("probes", SvJsonNode.Integer(result.Probes));
        o2.Add("ms", SvJsonNode.Integer(ms));
        o2.Add("lips", SvJsonNode.Integer(result.Lips.Count));
        o2.Add("new", SvJsonNode.Integer(fresh.Count));
        var refused = new SvJsonObject();
        foreach ((LipProblem k, int v) in result.Refused.OrderBy(kv => kv.Key))
        {
            refused.Add(k.ToString(), SvJsonNode.Integer(v));
        }

        o2.Add("refusedProbes", refused);
        if (a.Has("compare"))
        {
            o2.Add("compare", Compare(result.Lips, ledges, Number(a, "tolerance", 12)));
        }

        int limit = a.GetInt("limit", 200);
        o2.Add("proposals", new SvJsonArray(shown.Take(limit).Select(LipJson)));
        if (applied is not null)
        {
            o2.Add("applied", SvJsonNode.String(applied));
            o2.Add("added", Array(added));
            o2.Add("saved", SvJsonNode.Bool(!a.Has("dry-run")));
        }

        if (a.Has("json"))
        {
            PrintJson(o2, a);
            return 0;
        }

        Console.WriteLine($"{p.Id}: {result.Lips.Count} ledges found ({fresh.Count} not covered by an LG) from {result.Edges} edges, " +
            $"{result.Probes} probes in {ms} ms");
        Console.WriteLine("  refused probes: " + string.Join(", ", result.Refused.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        if (a.Has("compare"))
        {
            Console.WriteLine("  compare: " + CanonicalJson.ToCanonicalString(o2["compare"]!));
        }

        foreach (Lip l in shown.Take(limit))
        {
            Console.WriteLine($"  LG {V(l.A)} -> {V(l.B)}  {l.Length:0} long, drop {l.Drop:0}{(l.Joined ? ", joined" : "")}");
        }

        if (shown.Count > limit)
        {
            Console.WriteLine($"  ... {shown.Count - limit} more (--limit)");
        }

        if (applied is not null)
        {
            Console.WriteLine($"{applied}: {string.Join(", ", added.Take(12))}{(added.Count > 12 ? " ..." : "")}" + (a.Has("dry-run") ? " (not saved)" : " (saved)"));
        }

        return 0;
    }

    /// <summary>Per LG: the edge next to it and the probe result at its middle on that edge.</summary>
    private static int Probe(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        TraversalWorld world = TraversalWorld.Build(p, session.Folder);
        LedgeOptions o = Options(a);
        var rows = new SvJsonArray();
        foreach (Ge g in Ids(a, p).Select(p.FindGe).OfType<Ge>().Where(g => g.Kind == GeKinds.LedgeGrab && g.Points.Count >= 2).Take(a.GetInt("limit", 200)))
        {
            var row = new SvJsonObject();
            row.Add("id", SvJsonNode.String(g.Id));
            if (LedgeProbe.Fit(world, g.Points[0], g.Points[1], o) is not { } fit)
            {
                row.Add("result", SvJsonNode.String("no edge within 32"));
            }
            else
            {
                Vec3 mid = ((g.Points[0] + g.Points[1]) / 2 + fit.Offset) with { Z = fit.TopZ };
                (LipProblem problem, double drop) = LedgeProbe.Evaluate(world, mid, fit.HangSide, o);
                row.Add("result", SvJsonNode.String(LedgeProbe.Describe(problem, drop)));
                row.Add("across", Num(Math.Round(fit.Across, 2)));
                row.Add("dz", Num(Math.Round(fit.TopZ - (g.Points[0].Z + g.Points[1].Z) / 2, 2)));
                row.Add("drop", Num(Math.Round(drop)));
            }

            rows.Add(row);
        }

        if (a.Has("json"))
        {
            PrintJson(rows, a);
        }
        else
        {
            foreach (SvJsonObject r in rows.Cast<SvJsonObject>())
            {
                Console.WriteLine($"  {((SvJsonString)r["id"]!).Value,-10} {((SvJsonString)r["result"]!).Value}" +
                    (r["across"] is { } x ? $"  (off edge {CanonicalJson.ToCanonicalString(x)}, drop {CanonicalJson.ToCanonicalString(r["drop"]!)})" : ""));
            }
        }

        return 0;
    }

    /// <summary>How well the found ledges match the map's own LGs: LGs a ledge covers by at least half, and the
    /// found length that lies on an LG.</summary>
    private static SvJsonObject Compare(IReadOnlyList<Lip> lips, List<Ge> ledges, double tolerance)
    {
        int matched = 0;
        var missed = new List<string>();
        foreach (Ge g in ledges)
        {
            double len = (g.Points[1] - g.Points[0]).Length;
            if (lips.Any(l => GeEdits.Overlap(l, g, tolerance) >= len * 0.5))
            {
                matched++;
            }
            else
            {
                missed.Add(g.Id);
            }
        }

        int used = lips.Count(l => ledges.Any(g => GeEdits.Overlap(l, g, tolerance) > 0));
        var o = new SvJsonObject();
        o.Add("existing", SvJsonNode.Integer(ledges.Count));
        o.Add("existingMatched", SvJsonNode.Integer(matched));
        o.Add("existingMissed", Array(missed.Take(40)));
        o.Add("foundOnAnLg", SvJsonNode.Integer(used));
        o.Add("foundElsewhere", SvJsonNode.Integer(lips.Count - used));
        return o;
    }

    private static int Flush(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        TraversalWorld world = TraversalWorld.Build(p, session.Folder);
        var notFound = new List<string>();
        ReplaceObjectsCommand cmd = GeEdits.Flush(p, world, Ids(a, p), Options(a), notFound, Number(a, "reach", 32));
        return Finish(session, a, cmd, "flushed", extra: o => o.Add("noEdge", Array(notFound)));
    }

    private static int Join(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        ReplaceObjectsCommand cmd = GeEdits.JoinCorners(p, Ids(a, p), Number(a, "dist", GeEdits.CornerReach));
        return Finish(session, a, cmd, "joined");
    }

    private static int Mask(EditorSession session, CliArgs a)
    {
        int mask = a.GetInt("id", -1);
        if (mask != 0 && (mask is < 1 or > 128 || (mask & (mask - 1)) != 0))
        {
            throw new ArgumentException("--id must be 0 or one of 1, 2, 4 ... 128");
        }

        List<string> ids = a.Get("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            ?? throw new ArgumentException("give --ids");
        return Finish(session, a, GeEdits.SetMask(session.Project, ids, mask), "masked");
    }

    private static int Platform(EditorSession session, CliArgs a)
    {
        string name = a.Get("platform")?.ToLowerInvariant() ?? throw new ArgumentException("give --platform common, ps2 or xbox");
        int platform = GeEdits.Platforms.ToList().IndexOf(name) is >= 0 and int i ? i : throw new ArgumentException("--platform is common, ps2 or xbox");
        List<string> ids = a.Get("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            ?? throw new ArgumentException("give --ids");
        return Finish(session, a, GeEdits.SetPlatform(session.Project, ids, platform), "changed");
    }

    private static int Activation(EditorSession session, CliArgs a)
    {
        int mask = int.TryParse(a.Positional(2), out int m) && m is >= 0 and <= 255 ? m : throw new ArgumentException("give the mask, 0-255");
        session.Execute(GeEdits.SetActivationMask(mask));
        if (!a.Has("dry-run"))
        {
            session.Save();
        }

        Console.WriteLine($"GEActivationMask = {mask}" + (a.Has("dry-run") ? " (not saved)" : " (saved)"));
        return 0;
    }

    private static int Finish(EditorSession session, CliArgs a, ReplaceObjectsCommand cmd, string verb, Action<SvJsonObject>? extra = null)
    {
        List<string> changed = cmd.IsEmpty ? [] : ChangedGes(session.Project, cmd);
        if (!cmd.IsEmpty)
        {
            session.Execute(cmd);
            if (!a.Has("dry-run"))
            {
                session.Save();
            }
        }

        var o = new SvJsonObject();
        o.Add(verb, Array(changed));
        o.Add("saved", SvJsonNode.Bool(!cmd.IsEmpty && !a.Has("dry-run")));
        extra?.Invoke(o);
        if (a.Has("json"))
        {
            PrintJson(o, a);
        }
        else
        {
            Console.WriteLine($"{cmd.Name}: {changed.Count} GEs {verb}" + (changed.Count > 0 ? $" ({string.Join(", ", changed.Take(12))}{(changed.Count > 12 ? " ..." : "")})" : "") +
                (cmd.IsEmpty || a.Has("dry-run") ? " (not saved)" : " (saved)"));
            if (o["noEdge"] is SvJsonArray none && none.Count > 0)
            {
                Console.WriteLine($"  no edge near: {string.Join(", ", none.Take(20).Select(n => ((SvJsonString)n).Value))}");
            }
        }

        return 0;
    }

    /// <summary>The GEs a replace command changes (compared by points, ID and platform before and after).</summary>
    private static List<string> ChangedGes(MapProject p, ReplaceObjectsCommand cmd)
    {
        MapProject probe = new() { Ges = p.Ges.Select(g => g.Clone()).ToList() };
        cmd.Do(probe);
        return probe.Ges.Where(g => p.FindGe(g.Id) is { } before && (before.Mask != g.Mask || before.Platform != g.Platform || !before.Points.SequenceEqual(g.Points)))
            .Select(g => g.Id).ToList();
    }

    // ---- ose graph ---------------------------------------------------------------------------------------------

    private static Task<int> GraphAsync(CliArgs a)
    {
        EnsureServices();
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        MapProject p = ProjectStore.Load(folder);
        GameplayGraph graph = GameplayGraph.Build(p);
        IEnumerable<GameplayLink> links = graph.Links;
        IEnumerable<DanglingEvent> dangling = graph.Dangling;
        if (a.Get("id") is { } id)
        {
            HashSet<string> reach = graph.Around(id, a.GetInt("depth", 1));
            links = links.Where(l => reach.Contains(l.From) && reach.Contains(l.To));
            dangling = dangling.Where(d => reach.Contains(d.From));
        }

        bool only = a.Has("dangling") || a.Has("rings") || a.Has("missions");
        int limit = a.GetInt("limit", 300);
        List<GameplayLink> linkList = links.ToList();
        List<DanglingEvent> danglingList = dangling.ToList();
        List<List<string>> rings = graph.Rings();
        MissionTree missions = MissionTree.Build(p, Core.Services.EditorServices.Classes);

        var o = new SvJsonObject();
        o.Add("map", SvJsonNode.String(p.Id));
        o.Add("count", SvJsonNode.Integer(linkList.Count));
        if (!only)
        {
            o.Add("cols", Array(["from", "via", "to", "kind", "event"]));
            o.Add("rows", new SvJsonArray(linkList.Take(limit).Select(l => (SvJsonNode)Array([l.From, l.Via, l.To, l.Kind.ToString().ToLowerInvariant(), l.Event ?? ""]))));
        }

        if (!only || a.Has("dangling"))
        {
            o.Add("dangling", new SvJsonArray(danglingList.Take(limit).Select(d => (SvJsonNode)Array([d.From, d.Via, d.Event]))));
        }

        if (!only || a.Has("rings"))
        {
            o.Add("rings", new SvJsonArray(rings.Select(r => (SvJsonNode)Array(r))));
        }

        if (!only || a.Has("missions"))
        {
            o.Add("missions", MissionsJson(missions));
        }

        if (a.Has("json"))
        {
            PrintJson(o, a);
            return Task.FromResult(0);
        }

        if (!only)
        {
            Console.WriteLine($"{p.Id}: {linkList.Count} links ({linkList.Count(l => l.Kind == LinkKind.Fire)} fire, " +
                $"{linkList.Count(l => l.Kind == LinkKind.Accept)} accept, {linkList.Count(l => l.Kind == LinkKind.Ref)} ref)");
            foreach (GameplayLink l in linkList.Take(limit))
            {
                Console.WriteLine($"  {l.From} -{l.Via}{(l.Event is null ? "" : "=" + l.Event)}-> {l.To}  ({l.Kind.ToString().ToLowerInvariant()})");
            }
        }

        if (!only || a.Has("dangling"))
        {
            Console.WriteLine($"Events nothing receives: {danglingList.Count}");
            foreach (DanglingEvent d in danglingList.Take(limit))
            {
                Console.WriteLine($"  {d.From}.{d.Via} = {d.Event}");
            }
        }

        if (!only || a.Has("rings"))
        {
            Console.WriteLine($"Event rings: {rings.Count}");
            foreach (List<string> r in rings)
            {
                Console.WriteLine("  " + string.Join(" -> ", r));
            }
        }

        if (!only || a.Has("missions"))
        {
            PrintMissions(missions);
        }

        return Task.FromResult(0);
    }

    private static SvJsonArray MissionsJson(MissionTree t) => new(t.Missions.Select(m =>
    {
        var o = new SvJsonObject();
        o.Add("mission", SvJsonNode.String(m.Id));
        o.Add("mode", SvJsonNode.String(m.ModeName));
        o.Add("minimumObjectives", SvJsonNode.Integer(m.MinimumObjectives));
        o.Add("objectives", new SvJsonArray(m.Objectives.Select(ob =>
        {
            var x = new SvJsonObject();
            x.Add("objective", SvJsonNode.String(ob.Id));
            x.Add("name", SvJsonNode.String(ob.Name));
            x.Add("triggers", Array(ob.Triggers));
            return (SvJsonNode)x;
        })));
        o.Add("triggers", Array(m.Triggers));
        return (SvJsonNode)o;
    }));

    private static void PrintMissions(MissionTree t)
    {
        Console.WriteLine($"Missions: {t.Missions.Count}");
        foreach (MissionNode m in t.Missions)
        {
            Console.WriteLine($"  {m.Id} [{m.ModeName}] wins with {m.MinimumObjectives} of {m.Completers}{(m.Chained ? ", chained" : "")}");
            foreach (ObjectiveNode o in m.Objectives)
            {
                Console.WriteLine($"    {o.Id} \"{o.Name}\" <- {string.Join(", ", o.Triggers)}{(o.Missing.Count > 0 ? $" (missing {string.Join(", ", o.Missing)})" : "")}");
            }

            if (m.Triggers.Count > 0)
            {
                Console.WriteLine($"    triggers: {string.Join(", ", m.Triggers.Take(8))}{(m.Triggers.Count > 8 ? $" +{m.Triggers.Count - 8}" : "")}");
            }
        }

        if (t.Unlisted.Count > 0)
        {
            Console.WriteLine($"  objectives in no mission: {string.Join(", ", t.Unlisted)}");
        }

        Console.WriteLine(t.MatchSize.Count == 0 ? "Player-count rules: none in this map" : "Player-count rules:");
        foreach (MatchSizeRule r in t.MatchSize)
        {
            Console.WriteLine($"  {r.Id}: {r.Detail}");
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static SvJsonNode LipJson(Lip l)
    {
        var o = new SvJsonObject();
        o.Add("points", new SvJsonArray([ModelJson.Vec(GeEdits.Round(l.A)), ModelJson.Vec(GeEdits.Round(l.B))]));
        o.Add("length", Num(Math.Round(l.Length, 1)));
        o.Add("drop", Num(Math.Round(l.Drop)));
        o.Add("hangSide", ModelJson.Vec(GeEdits.Round(l.Out)));
        if (l.Joined)
        {
            o.Add("joined", SvJsonNode.True);
        }

        return o;
    }

    private static IEnumerable<string> Ids(CliArgs a, MapProject p) =>
        a.Get("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? p.Ges.Where(g => g.Kind == GeKinds.LedgeGrab).Select(g => g.Id);

    private static double Number(CliArgs a, string key, double fallback) =>
        a.Get(key) is { } s ? Parsing.Number(s, "--" + key) : fallback;

    private static (Vec3 Min, Vec3 Max) Box(string text)
    {
        double[] v = text.Split(',').Select(s => Parsing.Number(s.Trim(), "--box")).ToArray();
        if (v.Length != 6)
        {
            throw new ArgumentException("--box needs six numbers: minX,minY,minZ,maxX,maxY,maxZ");
        }

        return (new Vec3(v[0], v[1], v[2]), new Vec3(v[3], v[4], v[5]));
    }

    private static string V(Vec3 v) => $"({Inv(v.X)}, {Inv(v.Y)}, {Inv(v.Z)})";
}
