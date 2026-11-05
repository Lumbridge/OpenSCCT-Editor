using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// A whole map in a short overview: identity, counts, bounds, actors by class, team starts, objectives, GEs,
/// layers, game modes and the top problems. The first thing a script reads.
/// </summary>
public static class MapSummary
{
    public static SvJsonObject Build(EditorSession session, SvJsonObject? q = null)
    {
        MapProject p = session.Project;
        int top = q is null ? 20 : (int)ApiJson.Number(q, "classes", 20);
        var o = new SvJsonObject();
        o.Add("map", ApiJson.Str(p.Id));
        o.Add("title", ApiJson.Str(p.Title));
        o.Add("source", ApiJson.Str(p.Source.Kind));
        if (session.Folder is not null)
        {
            o.Add("dirty", ApiJson.Bool(session.IsDirty));
        }

        int adds = p.Brushes.Count(b => b.Oper == CsgOper.Add);
        o.Add("counts", ApiJson.Obj(
            ("brushes", SvJsonNode.Integer(p.Brushes.Count)), ("add", SvJsonNode.Integer(adds)), ("subtract", SvJsonNode.Integer(p.Brushes.Count - adds)),
            ("actors", SvJsonNode.Integer(p.Actors.Count)), ("ges", SvJsonNode.Integer(p.Ges.Count))));
        if (p.Baked is not null)
        {
            o.Add("baked", ApiJson.Str(p.Baked.SourceMap));
        }

        if (Bounds(p) is (Vec3 min, Vec3 max))
        {
            o.Add("bounds", ApiJson.Arr([ApiJson.Vec(min), ApiJson.Vec(max)]));
        }

        var classes = p.Actors.GroupBy(a => a.ShortClass, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var byClass = new SvJsonObject();
        foreach (IGrouping<string, Actor> g in classes.Take(top))
        {
            byClass.Add(g.Key, SvJsonNode.Integer(g.Count()));
        }

        if (classes.Count > top)
        {
            byClass.Add("(other classes)", SvJsonNode.Integer(classes.Skip(top).Sum(g => g.Count())));
        }

        o.Add("classes", byClass);
        o.Add("starts", Starts(p));
        if (Objectives(p) is { Count: > 0 } objectives)
        {
            o.Add("objectives", objectives);
        }

        if (p.Ges.Count > 0)
        {
            var ges = new SvJsonObject();
            foreach (IGrouping<string, Ge> g in p.Ges.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                ges.Add(g.Key, SvJsonNode.Integer(g.Count()));
            }

            o.Add("ges", ges);
        }

        var layers = new SvJsonObject();
        foreach (Layer l in p.Layers)
        {
            int n = p.AllRefs().Count(r => ObjectFilter.LayerIs(p.MetaOf(r)?.Layer ?? "", l.Name));
            layers.TryAdd(l.Name, ApiJson.Str($"{n}{(l.Visible ? "" : " hidden")}{(l.Locked ? " locked" : "")}"));
        }

        o.Add("layers", layers);
        if (p.Prefabs.Count > 0)
        {
            // Instances by prefab (query prefabs for each one's place and parameters).
            var prefabs = new SvJsonObject();
            foreach (IGrouping<string, PrefabInstance> g in p.Prefabs.GroupBy(i => i.Prefab, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                prefabs.Add(g.Key, SvJsonNode.Integer(g.Count()));
            }

            o.Add("prefabs", prefabs);
        }

        o.Add("modes", ApiJson.Strings(p.MapSettings.Keys));
        o.Add("lighting", Project.ProjectStore.LightingToJson(p.Lighting));
        o.Add("problems", Problems(p, session.Folder));
        return o;
    }

    private static (Vec3, Vec3)? Bounds(MapProject p)
    {
        if (p.Brushes.Count == 0 && p.Actors.Count == 0)
        {
            return null;
        }

        IEnumerable<(Vec3 Min, Vec3 Max)> boxes = p.Brushes.Count > 0
            ? p.Brushes.Select(MapObjects.BrushBounds)
            : p.Actors.Select(a => (a.Location, a.Location));
        return boxes.Aggregate((a, b) => (Vec3.Min(a.Min, b.Min), Vec3.Max(a.Max, b.Max)));
    }

    /// <summary>Starts per team: "spy"/"merc" → [[id, [x,y,z]], ...] (only counts past 8).</summary>
    private static SvJsonObject Starts(MapProject p)
    {
        var o = new SvJsonObject();
        foreach (IGrouping<int, Actor> team in p.Actors.Where(a => MapObjects.ClassMatches(a.Class, "PlayerStart", subclasses: true))
                     .GroupBy(a => a.Props.GetInt("TeamNumber")).OrderBy(g => g.Key))
        {
            string name = team.Key switch { 0 => "spy", 1 => "merc", _ => $"team{team.Key}" };
            List<Actor> list = team.ToList();
            o.Add(name, list.Count > 8
                ? SvJsonNode.Integer(list.Count)
                : ApiJson.Arr(list.Select(a => (SvJsonNode)ApiJson.Arr([ApiJson.Str(a.Id), ApiJson.Vec(a.Location)]))));
        }

        return o;
    }

    /// <summary>Objectives and objective triggers: [id, class, [x,y,z]] (at most 16).</summary>
    private static SvJsonArray Objectives(MapProject p)
    {
        bool IsObjective(Actor a) => EditorServices.Classes is { } cat && cat.Find(a.Class) is not null
            ? cat.IsA(a.Class, "SBase.SObjective") || cat.IsA(a.Class, "SBase.SObjectiveTrigger")
            : a.ShortClass.Contains("Objective", StringComparison.OrdinalIgnoreCase);
        return ApiJson.Arr(p.Actors.Where(IsObjective).Take(16)
            .Select(a => (SvJsonNode)ApiJson.Arr([ApiJson.Str(a.Id), ApiJson.Str(a.ShortClass), ApiJson.Vec(a.Location)])));
    }

    private static SvJsonObject Problems(MapProject p, string? folder)
    {
        IReadOnlyList<Finding> findings = MapQuery.Findings(p, folder, null, out string source);
        return ApiJson.Obj(
            ("checker", ApiJson.Str(source)),
            ("errors", SvJsonNode.Integer(findings.Count(f => f.Severity == Severity.Error))),
            ("warnings", SvJsonNode.Integer(findings.Count(f => f.Severity == Severity.Warning))),
            ("top", ApiJson.Strings(findings.Where(f => f.Severity != Severity.Info).Take(5).Select(f => f.ToString()))));
    }
}
