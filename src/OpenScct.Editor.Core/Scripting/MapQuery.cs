using OpenScct.Content.Json;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Read-only queries with chosen fields and compact output: <c>{"q":"find","class":"SCamera","fields":["id","loc"]}</c>.
/// Kinds: find, count, get, summary, schema, classes, check, edges, raycast, pointInfo, history, workspace, storeys,
/// backups, prefabs, assets, asset, match, outfits, recoveredLights (an .sdc import's light recovery), the gameplay workbenches' mover, magic, camnets, propsets, tips, zone
/// and modes, and the analyses of the compiled map: stealth, reach, route, nav. A batch (<c>{"queries":[...]}</c> or an
/// array) answers several in one round trip.
/// </summary>
public static partial class MapQuery
{
    private delegate SvJsonNode Handler(EditorSession session, ChangeContext ctx, SvJsonObject q);

    private static readonly Dictionary<string, Handler> s_queries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["find"] = Find,
        ["count"] = Count,
        ["get"] = Get,
        ["summary"] = (s, _, q) => MapSummary.Build(s, q),
        ["schema"] = (_, _, q) => ClassQueries.Schema(q),
        ["classes"] = (s, _, q) => ClassQueries.Classes(s.Project, q),
        ["check"] = Check,
        ["edges"] = (s, _, q) => EventGraph.Query(s.Project, q),
        ["raycast"] = Raycast,
        ["trace"] = Raycast,
        ["pointInfo"] = PointInfo,
        ["history"] = History,
        ["identity"] = (s, _, _) => IdentityOf(s.Project),
        ["importReport"] = (s, _, _) => Interop.ImportReport.Build(s.Project, null).ToJson(),
        ["workspace"] = Workspace,
        ["storeys"] = (s, _, _) => StoreysJson(s.Project, s.Folder),
        ["backups"] = (s, _, _) => ProjectBackups.ToJson(s.Folder),
        ["prefabs"] = Prefabs,
        ["assets"] = (s, _, q) => Assets.AssetQueries.List(s, q),
        ["asset"] = (s, _, q) => Assets.AssetQueries.Detail(s, q),
        ["mover"] = Mover,
        ["magic"] = Magic,
        ["camnets"] = (s, _, _) => CamNets(s.Project),
        ["propsets"] = PropSets,
        ["tips"] = (s, _, _) => Tips(s.Project),
        ["zone"] = Zone,
        ["modes"] = Modes,
        ["stealth"] = (s, ctx, q) => AnalysisJson.Stealth(Analysis(s), q, ctx.Point),
        ["reach"] = (s, _, q) => AnalysisJson.Reach(Analysis(s), q),
        ["route"] = (s, ctx, q) => AnalysisJson.Route(Analysis(s), q, ctx.Point),
        ["nav"] = (s, ctx, q) => AnalysisJson.Nav(Analysis(s), q, ctx.Point),
        ["match"] = (s, _, q) => MatchQueries.Match(s.Project, q),
        ["outfits"] = (s, _, _) => MatchQueries.Outfits(s.Project, s.Folder),
        ["recoveredLights"] = (s, _, _) => Interop.Sdc.Lights.LostLightRecovery.Describe(s.Project),
    };

    public static IEnumerable<string> Names => s_queries.Keys;

    private static readonly HashSet<string> s_analyses = new(StringComparer.OrdinalIgnoreCase) { "stealth", "reach", "route", "nav" };

    /// <summary>
    /// True when the query (or any query of a batch) needs the compiled map: an analysis, or a deep check. Compiling
    /// and building the bots' graphs can take minutes, so a live editor answers these off its main thread.
    /// </summary>
    public static bool NeedsCompiledMap(SvJsonNode? query) => query switch
    {
        SvJsonArray batch => batch.Any(NeedsCompiledMap),
        SvJsonObject o when o["queries"] is SvJsonArray list => list.Any(NeedsCompiledMap),
        SvJsonObject o => ApiJson.GetString(o, "q") is { } kind && (s_analyses.Contains(kind) ||
            kind.Equals("check", StringComparison.OrdinalIgnoreCase) && ApiJson.GetBool(o, "deep", false)),
        _ => false,
    };

    /// <summary>The session's compiled analysis map (compiled on first use and after each edit).</summary>
    private static AnalysisMap Analysis(EditorSession s)
    {
        try
        {
            return AnalysisCache.Of(s).Get();
        }
        catch (InvalidOperationException e)
        {
            throw new ApiException(e.Message);
        }
    }

    /// <summary>Runs a query or a batch. Errors come back as <c>{"error":{...}}</c> in place of the result.</summary>
    public static SvJsonNode Run(EditorSession session, SvJsonNode query)
    {
        if (query is SvJsonArray batch)
        {
            return new SvJsonArray(batch.Select(q => RunOne(session, q)));
        }

        if (query is SvJsonObject o && o["queries"] is SvJsonArray list)
        {
            return new SvJsonArray(list.Select(q => RunOne(session, q)));
        }

        return RunOne(session, query);
    }

    private static SvJsonNode RunOne(EditorSession session, SvJsonNode node)
    {
        try
        {
            if (node is not SvJsonObject q)
            {
                throw new ApiException("a query is an object such as {\"q\":\"find\",\"class\":\"PlayerStart\"}", "q");
            }

            string kind = ApiJson.GetString(q, "q") ?? throw new ApiException($"missing \"q\" ({string.Join(", ", s_queries.Keys)})", "q");
            Handler h = s_queries.GetValueOrDefault(kind) ?? throw new ApiException($"unknown query '{kind}' ({string.Join(", ", s_queries.Keys)})", "q");
            var ctx = new ChangeContext(session.Project, session.Folder, "query", "query");
            return h(session, ctx, q);
        }
        catch (ApiException ex)
        {
            return ApiJson.Obj(("error", ex.ToJson()));
        }
        catch (Exception ex)
        {
            // One failing query of a batch must not lose the others' answers.
            return ApiJson.Obj(("error", ApiException.Internal(ex).ToJson()));
        }
    }

    // ---- find / get --------------------------------------------------------------------------------------------

    private static readonly string[] s_defaultFindFields = ["id", "class", "loc"];

    private static SvJsonNode Find(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        (List<ObjectRef> refs, int total) = Select(s.Project, ctx, q);
        string[] fields = ApiJson.StringList(q["fields"], "fields")?.ToArray() ?? s_defaultFindFields;
        SvJsonObject result = Project(s.Project, refs, fields, ApiJson.GetBool(q, "rows", false));
        result.Add("count", SvJsonNode.Integer(total));
        if (total > refs.Count + (int)ApiJson.Number(q, "offset", 0))
        {
            result.Add("more", SvJsonNode.True);
        }

        return result;
    }

    private static SvJsonNode Count(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        List<ObjectRef> all = ObjectFilter.Parse(q, ctx.Point, field: "q", query: true).Select(s.Project).ToList();
        if (ApiJson.GetString(q, "by") is not { } by)
        {
            return ApiJson.Obj(("count", SvJsonNode.Integer(all.Count)));
        }

        var groups = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (ObjectRef r in all)
        {
            string key = by switch
            {
                "class" => MapObjects.Short(MapObjects.ClassOf(s.Project, r)),
                "kind" => MapObjects.KindName(r.Kind),
                "layer" => s.Project.MetaOf(r)?.Layer is { Length: > 0 } l ? l : "Default",
                "folder" => s.Project.MetaOf(r)?.Folder ?? "",
                "author" => s.Project.MetaOf(r)?.Author ?? "",
                _ => throw new ApiException("by is class, kind, layer, folder or author", "by"),
            };
            groups[key] = groups.GetValueOrDefault(key) + 1;
        }

        var o = new SvJsonObject();
        foreach (KeyValuePair<string, int> kv in groups)
        {
            o.Add(kv.Key, SvJsonNode.Integer(kv.Value));
        }

        return ApiJson.Obj(("count", SvJsonNode.Integer(all.Count)), ("by", o));
    }

    private static SvJsonNode Get(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        List<string> ids = ApiJson.StringList(q["ids"] ?? q["id"], "ids") ?? throw new ApiException("give ids", "ids");
        var refs = ids.Select((id, i) => ctx.Resolve(id, $"ids[{i}]")).ToList();
        string[] fields = ApiJson.StringList(q["fields"], "fields")?.ToArray() ?? ["*"];
        return Project(s.Project, refs, fields, ApiJson.GetBool(q, "rows", false));
    }

    private static (List<ObjectRef> Page, int Total) Select(MapProject p, ChangeContext ctx, SvJsonObject q)
    {
        IEnumerable<ObjectRef> found = ObjectFilter.Parse(q, ctx.Point, field: "q", query: true).Select(p);
        if (q["near"] is { } near)
        {
            Vec3 c = ctx.Point(near, "near");
            found = found.OrderBy(r => (MapObjects.Origin(p, r) - c).LengthSquared);
        }
        else if (ApiJson.GetString(q, "sort") is { } sort)
        {
            found = found.OrderBy(r => Fields.SortKey(p, r, sort), StringComparer.Ordinal);
        }

        List<ObjectRef> all = found.ToList();
        int offset = (int)ApiJson.Number(q, "offset", 0);
        int limit = (int)ApiJson.Number(q, "limit", 100);
        return (all.Skip(offset).Take(limit).ToList(), all.Count);
    }

    /// <summary>Objects as <c>{"items":[{field: value}]}</c>, or with rows as <c>{"cols":[...],"rows":[[...]]}</c>.</summary>
    public static SvJsonObject Project(MapProject p, IReadOnlyList<ObjectRef> refs, string[] fields, bool rows)
    {
        if (rows)
        {
            string[] cols = fields.Contains("*") ? Fields.Common : fields;
            var table = refs.Select(r => (SvJsonNode)new SvJsonArray(cols.Select(f => Fields.Get(p, r, f) ?? SvJsonNode.Null)));
            return ApiJson.Obj(("cols", ApiJson.Strings(cols)), ("rows", ApiJson.Arr(table)));
        }

        var items = refs.Select(r =>
        {
            var o = new SvJsonObject();
            foreach (string f in fields.Contains("*") ? Fields.All(p, r) : fields)
            {
                if (Fields.Get(p, r, f) is { } v)
                {
                    o.TryAdd(f, v);
                }
            }

            return (SvJsonNode)o;
        });
        return ApiJson.Obj(("items", ApiJson.Arr(items)));
    }

    // ---- check -------------------------------------------------------------------------------------------------

    private static SvJsonNode Check(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        AnalysisMap? compiled = ApiJson.GetBool(q, "deep", false) ? Analysis(s) : null;
        IReadOnlyList<Finding> findings = Findings(s.Project, s.Folder, q, out string source, compiled);
        int limit = (int)ApiJson.Number(q, "limit", 100);
        return ApiJson.Obj(
            ("checker", ApiJson.Str(source)),
            ("deep", compiled is null ? null : SvJsonNode.True),
            ("errors", SvJsonNode.Integer(findings.Count(f => f.Severity == Severity.Error))),
            ("warnings", SvJsonNode.Integer(findings.Count(f => f.Severity == Severity.Warning))),
            ("cols", ApiJson.Strings(["sev", "code", "message", "ids", "at", "fix", "shapes"])),
            ("rows", ApiJson.Arr(findings.Take(limit).Select(f => FindingRow(f, withFix: true)))),
            ("more", findings.Count > limit ? SvJsonNode.True : null));
    }

    /// <summary>Map Check findings (the registered checker; the script's basic checks when none is loaded).</summary>
    public static IReadOnlyList<Finding> Findings(MapProject p, string? folder, SvJsonObject? q, out string source, AnalysisMap? compiled = null)
    {
        if (EditorServices.Checker is { } checker)
        {
            string target = ApiJson.GetString(q, "target") ?? "openscct";
            var options = new CheckOptions
            {
                Target = target.Equals("original", StringComparison.OrdinalIgnoreCase) ? TargetGame.Original : TargetGame.OpenScct,
                Only = q is null ? null : ApiJson.StringList(q["only"], "only"),
                Compiled = compiled,
                ProjectFolder = folder,
            };
            source = "map check";
            return checker.Check(p, options).OrderBy(f => f.Severity).ToList();
        }

        source = "basic";
        return BasicChecks.Run(p, folder);
    }

    public static SvJsonNode FindingRow(Finding f) => FindingRow(f, withFix: false);

    /// <summary>A finding as a row; <paramref name="withFix"/> adds its fix ({label, changes}, a change document to apply) or
    /// null, and its shapes or null.</summary>
    public static SvJsonNode FindingRow(Finding f, bool withFix)
    {
        SvJsonArray row = ApiJson.Arr([
            ApiJson.Str(f.Severity.ToString().ToLowerInvariant()), ApiJson.Str(f.Code), ApiJson.Str(f.Message),
            ApiJson.Strings(f.Objects.Select(o => o.Id)), f.Location is Vec3 v ? ApiJson.Vec(v) : SvJsonNode.Null,
        ]);
        if (withFix)
        {
            row.Add(f.Fix is { } fix ? ApiJson.Obj(("label", ApiJson.Str(fix.Label)), ("changes", fix.Changes)) : SvJsonNode.Null);
            row.Add(ShapesJson(f) ?? SvJsonNode.Null);
        }

        return row;
    }

    /// <summary>A finding's shapes as <c>[{kind: path|outline, points: [[x,y,z]...]}]</c> (whole units), or null when it has none.</summary>
    public static SvJsonArray? ShapesJson(Finding f)
    {
        if (f.Shapes.Count == 0)
        {
            return null;
        }

        static SvJsonNode Point(Vec3 p) => ApiJson.Vec(new Vec3(Math.Round(p.X), Math.Round(p.Y), Math.Round(p.Z)));
        return ApiJson.Arr(f.Shapes.Select(s => (SvJsonNode)ApiJson.Obj(
            ("kind", ApiJson.Str(s.Kind.ToString().ToLowerInvariant())),
            ("points", ApiJson.Arr(s.Points.Select(Point))))));
    }

    // ---- geometry ----------------------------------------------------------------------------------------------

    private static SvJsonNode Raycast(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        Vec3 from = ctx.Point(q["from"] ?? throw new ApiException("from is the start point", "from"), "from");
        Vec3 dir;
        double max = ApiJson.Number(q, "max", 65536);
        if (q["to"] is { } toNode)
        {
            Vec3 to = ctx.Point(toNode, "to");
            dir = to - from;
            max = dir.Length;
        }
        else
        {
            dir = q["dir"] is { } d ? ApiJson.RequireVec(d, "dir") : new Vec3(0, 0, -1);
        }

        RayHit? hit = ctx.Geometry().Raycast(from, dir, max);
        return hit is null ? ApiJson.Obj(("hit", SvJsonNode.False)) : HitJson(hit);
    }

    private static SvJsonObject HitJson(RayHit h) => ApiJson.Obj(
        ("hit", SvJsonNode.True), ("at", ApiJson.Vec(h.Point)), ("normal", ApiJson.Vec(h.Normal)), ("dist", ApiJson.Num(h.Distance)),
        ("by", ApiJson.Str(h.Source)), ("face", h.BrushId is null ? null : SvJsonNode.Integer(h.Face)),
        ("startSolid", h.StartSolid ? SvJsonNode.True : null));

    private static SvJsonNode PointInfo(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        Vec3 at = ctx.Point(q["at"] ?? throw new ApiException("at is the point", "at"), "at");
        WorldGeometry geo = ctx.Geometry();
        double radius = ApiJson.Number(q, "radius", 256);
        var walls = new SvJsonObject();
        foreach ((string name, Vec3 d) in new[] { ("+x", Vec3.UnitX), ("-x", -Vec3.UnitX), ("+y", Vec3.UnitY), ("-y", -Vec3.UnitY) })
        {
            if (geo.Raycast(at, d, 8192) is { } h)
            {
                walls.Add(name, ApiJson.Num(h.Distance));
            }
        }

        SvJsonNode Surface(Vec3 d) => geo.Raycast(at, d) is { } h
            ? ApiJson.Obj(("z", ApiJson.Num(h.Point.Z)), ("dist", ApiJson.Num(h.Distance)), ("by", ApiJson.Str(h.Source)))
            : SvJsonNode.Null;

        var near = s.Project.AllRefs()
            .Select(r => (Ref: r, Dist: (MapObjects.Origin(s.Project, r) - at).Length))
            .Where(x => x.Dist <= radius && x.Ref.Kind is not (ObjectKind.Brush or ObjectKind.Stair))
            .OrderBy(x => x.Dist).Take(10)
            .Select(x => (SvJsonNode)ApiJson.Arr([ApiJson.Str(x.Ref.Id), ApiJson.Str(MapObjects.Short(MapObjects.ClassOf(s.Project, x.Ref))), ApiJson.Num(x.Dist)]));
        return ApiJson.Obj(
            ("at", ApiJson.Vec(at)),
            ("solid", geo.HasBaked && geo.BrushCount == 0 ? null : ApiJson.Bool(geo.IsSolid(at))),
            ("brushes", ApiJson.Strings(geo.BrushesAt(at))),
            ("floor", Surface(new Vec3(0, 0, -1))),
            ("ceiling", Surface(Vec3.UnitZ)),
            ("walls", walls),
            ("near", ApiJson.Arr(near)));
    }

    private static SvJsonNode History(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        CommandStack c = s.Commands;
        int limit = (int)ApiJson.Number(q, "limit", 20);
        IReadOnlyList<HistoryEntry> all = c.History();
        var entries = all.Skip(Math.Max(0, all.Count - limit)).Select(e => (SvJsonNode)ApiJson.Arr([
            SvJsonNode.Integer(e.Index), ApiJson.Str(e.Name), ApiJson.Bool(e.IsDone),
        ]));
        return ApiJson.Obj(("position", SvJsonNode.Integer(c.Position)), ("dirty", ApiJson.Bool(c.IsDirty)),
            ("undo", c.UndoName is null ? null : ApiJson.Str(c.UndoName)), ("redo", c.RedoName is null ? null : ApiJson.Str(c.RedoName)),
            ("cols", ApiJson.Strings(["index", "name", "done"])), ("rows", ApiJson.Arr(entries)));
    }

    /// <summary>The map's name and identity as map.json stores it (empty fields left out).</summary>
    public static SvJsonObject IdentityOf(MapProject p)
    {
        var o = new SvJsonObject();
        o.Add("name", SvJsonNode.String(p.Title));
        foreach (KeyValuePair<string, SvJsonNode> kv in IdentityJson.ToJson(p.Identity))
        {
            o.Add(kv.Key, kv.Value);
        }

        return o;
    }

    /// <summary>The editor-only workspace (grid, camera, bookmarks, references, movement limits, play options) and the
    /// layer list.</summary>
    private static SvJsonNode Workspace(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        SvJsonObject o = ProjectStore.WorkspaceToJson(s.Project.Workspace);
        o.Add("layers", new SvJsonArray(s.Project.Layers.Select(ProjectStore.LayerToJson)));
        return o;
    }

    /// <summary>storeys: the map's storeys, lowest first (<c>name</c>, <c>bottom</c>, <c>top</c> (null for the open
    /// top of the highest detected one), <c>floor</c>), whether they were detected or are the map's own, and the one the
    /// views show (<c>shown</c>, null for all).</summary>
    public static SvJsonObject StoreysJson(MapProject p, string? folder)
    {
        IReadOnlyList<Geometry.Storey> storeys = Geometry.Storeys.Of(p, folder);
        int shown = Geometry.Storeys.Shown(storeys, p.Workspace.ShownStorey);
        return ApiJson.Obj(
            ("source", ApiJson.Str(p.Workspace.Storeys.Count > 0 ? "map" : "detected")),
            ("shown", shown >= 0 ? ApiJson.Str(storeys[shown].Name) : SvJsonNode.Null),
            ("storeys", ApiJson.Arr(storeys.Select(st => (SvJsonNode)ApiJson.Obj(
                ("name", ApiJson.Str(st.Name)),
                ("bottom", ApiJson.Num(st.Bottom)),
                ("top", double.IsFinite(st.Top) ? ApiJson.Num(st.Top) : SvJsonNode.Null),
                ("floor", st.Floor is double f ? ApiJson.Num(f) : null))))));
    }

    /// <summary>prefabs: what the map can place (name, title, category, source, parameter defaults, summary) and the
    /// instances in it (stale: the prefab changed since). <c>name</c> gives one prefab in full, with its instances.</summary>
    private static SvJsonNode Prefabs(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        if (ApiJson.GetString(q, "name") is { } name)
        {
            PrefabDefinition def = ctx.FindPrefab(name, "name");
            SvJsonObject full = def.ToJson();
            full.Add("source", ApiJson.Str(def.Source.ToString().ToLowerInvariant()));
            full.Add("hash", ApiJson.Str(def.Hash));
            if (def.Path is { } path)
            {
                full.Add("path", ApiJson.Str(path));
            }

            full.Add("instances", Instances(s.Project, s.Project.Prefabs.Where(i => string.Equals(i.Prefab, def.Name, StringComparison.OrdinalIgnoreCase)), ctx));
            return full;
        }

        (List<PrefabDefinition> all, List<string> problems) = PrefabStore.All(s.Folder);
        var list = all.Select(def => (SvJsonNode)ApiJson.Obj(
            ("name", ApiJson.Str(def.Name)),
            ("title", def.Title is null ? null : ApiJson.Str(def.Title)),
            ("category", def.Category is null ? null : ApiJson.Str(def.Category)),
            ("source", ApiJson.Str(def.Source.ToString().ToLowerInvariant())),
            ("params", def.Params.Count == 0 ? null : ApiJson.Obj(def.Params.Select(p => (p.Name, (SvJsonNode?)p.Default)).ToArray())),
            ("summary", def.Summary is null ? null : ApiJson.Str(def.Summary))));
        return ApiJson.Obj(
            ("prefabs", ApiJson.Arr(list)),
            ("instances", Instances(s.Project, s.Project.Prefabs, ctx)),
            ("library", ApiJson.Str(PrefabStore.LibraryFolder)),
            ("problems", problems.Count == 0 ? null : ApiJson.Strings(problems)));
    }

    private static SvJsonArray Instances(MapProject p, IEnumerable<PrefabInstance> instances, ChangeContext ctx)
    {
        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (EditorMeta m in p.Brushes.Select(b => b.Editor).Concat(p.Actors.Select(a => a.Editor)).Concat(p.Ges.Select(g => g.Editor)))
        {
            if (m.PrefabInstance is { } id)
            {
                parts[id] = parts.GetValueOrDefault(id) + 1;
            }
        }

        return ApiJson.Arr(instances.Select(i =>
        {
            PrefabDefinition? def = ctx.PendingPrefabs.TryGetValue(i.Prefab, out var pending) ? pending.Prefab : TryFind(i.Prefab, ctx.Folder);
            return (SvJsonNode)ApiJson.Obj(
                ("id", ApiJson.Str(i.Id)),
                ("prefab", ApiJson.Str(i.Prefab)),
                ("loc", ApiJson.Vec(i.Location)),
                ("rot", i.Rotation == Rot.Zero ? null : ApiJson.Rotator(i.Rotation)),
                ("params", i.Params.Count == 0 ? null : ApiJson.Obj(i.Params.Select(kv => (kv.Key, (SvJsonNode?)kv.Value)).ToArray())),
                ("parts", SvJsonNode.Integer(parts.GetValueOrDefault(i.Id))),
                ("stale", def is not null && def.Hash != i.Hash ? SvJsonNode.True : null),
                ("missing", def is null ? SvJsonNode.True : null));
        }));

        static PrefabDefinition? TryFind(string name, string? folder)
        {
            try
            {
                return PrefabStore.Find(name, folder);
            }
            catch (ApiException)
            {
                return null;
            }
        }
    }
}
