using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.AI;
using OpenScct.Game.SBase;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// The analysis queries of the scripting API and <c>ose analyze</c>: <c>stealth</c>, <c>reach</c>, <c>route</c> and
/// <c>nav</c>, each a JSON object in, a compact JSON object out. They share the session's compiled
/// <see cref="AnalysisMap"/>.
/// </summary>
public static class AnalysisJson
{
    /// <summary>Objective approaches: the cells this far across from an objective (see <see cref="Near"/>).</summary>
    public const float ApproachRadius = 512f;

    // ---- options ---------------------------------------------------------------------------------------------

    public static ReachProfile Profile(SvJsonObject q, TeamSide fallback = TeamSide.Spy) => new(
        ApiJson.GetString(q, "side") is { } side ? ReachProfile.ParseSide(side) : fallback,
        ApiJson.GetString(q, "drops") is { } drops ? ReachProfile.ParseDrops(drops) : DropRule.Survivable);

    /// <summary>The GE activation mask: <c>"mask":N</c>, <c>"mask":"all"</c> (every GE on), else the map's own.</summary>
    public static byte Mask(AnalysisMap map, SvJsonObject q)
    {
        if (ApiJson.GetString(q, "mask") is { } text)
        {
            return text.Equals("all", StringComparison.OrdinalIgnoreCase) ? byte.MaxValue
                : throw new ApiException("mask is a number 0..255 or \"all\"", "mask");
        }

        double mask = ApiJson.Number(q, "mask", map.ActivationMask);
        return mask is >= 0 and <= 255 ? (byte)mask : throw new ApiException("mask is a number 0..255 or \"all\"", "mask");
    }

    public static StealthOptions StealthOptions(SvJsonObject q) => new()
    {
        Spacing = Math.Clamp(ApiJson.Number(q, "spacing", 64), 32, 1024),
        Crouched = (ApiJson.GetString(q, "stance") ?? "crouch").ToLowerInvariant() switch
        {
            "crouch" or "crouched" => true,
            "stand" or "standing" => false,
            string s => throw new ApiException($"stance is crouch or stand, not '{s}'", "stance"),
        },
        MercSight = ApiJson.GetBool(q, "mercSight", true),
        Night = ApiJson.GetBool(q, "night", false),
        Lights = new LightState
        {
            Name = ApiJson.GetString(q, "lights") ?? "start",
            Off = ApiJson.StringList(q["off"], "off") ?? [],
            On = ApiJson.StringList(q["on"], "on") ?? [],
        },
    };

    // ---- stealth ---------------------------------------------------------------------------------------------

    public static SvJsonObject Stealth(AnalysisMap map, SvJsonObject q, Func<SvJsonNode, string, Vec3> point)
    {
        StealthOptions options = StealthOptions(q);
        StealthResult r;
        try
        {
            lock (map.Gate)
            {
                r = StealthMap.Build(map, options);
            }
        }
        catch (ArgumentException e)
        {
            throw new ApiException(e.Message, "lights");
        }

        IEnumerable<StealthCell> shown = r.Cells;
        if (q["near"] is { } near)
        {
            Vec3 c = point(near, "near");
            double radius = ApiJson.Number(q, "radius", 512);
            shown = shown.Where(cell => Near(cell, c, radius));
        }

        List<StealthCell> cells = shown.ToList();
        SvJsonObject o = Summary(cells);
        o.Add("state", ApiJson.Str(r.State));
        o.Add("lightsOff", ApiJson.Strings(r.LightsOff));
        o.Add("lightData", ApiJson.Bool(r.HasLightData));
        o.Add("pawnLights", SvJsonNode.Integer(r.PawnLights));
        o.Add("spotLights", SvJsonNode.Integer(r.SpotLights));
        if (r.HasLightData && r.PawnLights == 0 && r.SpotLights == 0)
        {
            o.Add("note", ApiJson.Str("no light lights pawns in game: only lights with bApplyToInGameLighting or bDynamicLight do; the rest only light the world"));
        }
        o.Add("worldOnlyLights", ApiJson.Strings(r.LightRoles.Where(l => l.Kind == LightRoleKind.WorldOnly).Select(l => l.Name)));
        o.Add("pawnOnlyLights", ApiJson.Strings(r.LightRoles.Where(l => l.Kind == LightRoleKind.PawnsOnly).Select(l => l.Name)));
        o.Add("spacing", ApiJson.Num(r.Spacing));
        o.Add("stance", ApiJson.Str(r.Crouched ? "crouch" : "stand"));
        o.Add("vision", ApiJson.Str(r.Night ? "night" : "normal"));
        o.Add("viewpoints", SvJsonNode.Integer(r.Viewpoints));
        o.Add("bandEdges", ApiJson.Arr([ApiJson.Num(StealthMap.HiddenBelow), ApiJson.Num(StealthMap.ExposedFrom)]));
        o.Add("switches", ApiJson.Arr(r.Switches.Select(s => (SvJsonNode)ApiJson.Obj(
            ("id", ApiJson.Str(s.Id)), ("class", ApiJson.Str(s.Class)), ("sends", ApiJson.Str(s.Untrigger ? "untrigger" : "trigger")),
            ("lights", ApiJson.Strings(s.Lights))))));
        o.Add("objectives", ApiJson.Arr(Objectives(map, r.Cells)));
        if (ApiJson.GetBool(q, "cells", false))
        {
            int limit = (int)ApiJson.Number(q, "limit", 5000);
            o.Add("cols", ApiJson.Strings(["x", "y", "z", "stand", "crouch", "band", "merc", "torch", "back", "albedo", "silhouette"]));
            o.Add("rows", ApiJson.Arr(cells.Take(limit).Select(CellRow)));
            if (cells.Count > limit)
            {
                o.Add("more", SvJsonNode.True);
            }
        }

        return o;
    }

    /// <summary>Counts of a set of cells: by band, tracked by the motion tracker, in a torch's reach, dark but tracked, and
    /// not exposed but dark against a bright background.</summary>
    private static SvJsonObject Summary(IReadOnlyCollection<StealthCell> cells) => ApiJson.Obj(
        ("cells", SvJsonNode.Integer(cells.Count)),
        ("hidden", SvJsonNode.Integer(cells.Count(c => c.Band == StealthBand.Hidden))),
        ("partial", SvJsonNode.Integer(cells.Count(c => c.Band == StealthBand.Partial))),
        ("exposed", SvJsonNode.Integer(cells.Count(c => c.Band == StealthBand.Exposed))),
        ("tracked", SvJsonNode.Integer(cells.Count(c => c.Tracked))),
        ("torch", SvJsonNode.Integer(cells.Count(c => c.InTorchReach))),
        ("hiddenButTracked", SvJsonNode.Integer(cells.Count(c => c.Band == StealthBand.Hidden && c.Tracked))),
        ("silhouette", SvJsonNode.Integer(cells.Count(c => c.Silhouette))));

    /// <summary>The cells round each objective trigger (its approaches), counted the same way.</summary>
    private static IEnumerable<SvJsonNode> Objectives(AnalysisMap map, IReadOnlyList<StealthCell> cells)
    {
        foreach (ActorRecordView o in ObjectiveTriggers(map))
        {
            List<StealthCell> near = cells.Where(c => Near(c, o.At, ApproachRadius)).ToList();
            SvJsonObject row = Summary(near);
            row.Add("id", ApiJson.Str(o.Id));
            yield return row;
        }
    }

    private readonly record struct ActorRecordView(string Id, Vec3 At);

    private static IEnumerable<ActorRecordView> ObjectiveTriggers(AnalysisMap map)
    {
        foreach (OpenScct.Content.ActorRecord record in map.Loaded.Gameplay.Actors)
        {
            if (map.Loaded.ActorOf(record) is SObjectiveTrigger t)
            {
                yield return new ActorRecordView(record.Id, AnalysisMap.V(t.Location));
            }
        }
    }

    private static SvJsonNode CellRow(StealthCell c) => ApiJson.Arr([
        ApiJson.Num(Math.Round(c.Floor.X)), ApiJson.Num(Math.Round(c.Floor.Y)), ApiJson.Num(Math.Round(c.Floor.Z)),
        ApiJson.Num(Math.Round(c.Stand, 3)), ApiJson.Num(Math.Round(c.Crouch, 3)), ApiJson.Str(c.Band.ToString().ToLowerInvariant()),
        c.MercDistance is { } d ? ApiJson.Num(Math.Round(d)) : SvJsonNode.Null, ApiJson.Num(Math.Round(c.Torch, 3)),
        c.Background is { } b ? ApiJson.Num(Math.Round(b, 3)) : SvJsonNode.Null, ApiJson.Num(Math.Round(c.BackAlbedo, 2)), ApiJson.Bool(c.Silhouette),
    ]);

    // ---- reach -----------------------------------------------------------------------------------------------

    public static SvJsonObject Reach(AnalysisMap map, SvJsonObject q)
    {
        DropRule drops = Profile(q).Drops;
        byte mask = Mask(map, q);
        ReachMap spy, merc;
        List<SpyOnlyArea> areas;
        List<Finding> findings;
        lock (map.Gate)
        {
            spy = Reachability.FromStarts(map, new ReachProfile(TeamSide.Spy, drops), mask);
            merc = Reachability.FromStarts(map, new ReachProfile(TeamSide.Merc, drops), mask);
            areas = Reachability.SpyOnlyAreas(spy, merc);
            findings = Reachability.Findings(map, spy, merc);
        }

        int limit = (int)ApiJson.Number(q, "limit", 10);
        return ApiJson.Obj(
            ("mask", SvJsonNode.Integer(mask)),
            ("drops", ApiJson.Str(drops.ToString().ToLowerInvariant())),
            ("movers", ApiJson.Str("a shut door blocks unless something the side reaches opens it (a switch by the game's use test, a touch trigger or sensor that detects it, or walking into it); a lift carries the side once it can work it")),
            ("spy", SideJson(spy)),
            ("merc", SideJson(merc)),
            ("spyOnly", ApiJson.Obj(
                ("nodes", SvJsonNode.Integer(areas.Sum(a => a.Nodes))),
                ("area", ApiJson.Num(Math.Round(areas.Sum(a => a.Area)))),
                ("cols", ApiJson.Strings(["x", "y", "z", "nodes", "area"])),
                ("rows", ApiJson.Arr(areas.Take(limit).Select(a => (SvJsonNode)ApiJson.Arr([
                    ApiJson.Num(Math.Round(a.Centre.X)), ApiJson.Num(Math.Round(a.Centre.Y)), ApiJson.Num(Math.Round(a.Centre.Z)),
                    SvJsonNode.Integer(a.Nodes), ApiJson.Num(Math.Round(a.Area))])))))),
            ("cols", ApiJson.Strings(["sev", "code", "message", "ids", "at"])),
            ("findings", ApiJson.Arr(findings.Select(MapQuery.FindingRow))));
    }

    private static SvJsonObject SideJson(ReachMap m)
    {
        var links = new SvJsonObject();
        foreach (NavEdgeKind kind in Enum.GetValues<NavEdgeKind>().Where(k => m.Graph.CountLinks(k) > 0))
        {
            links.Add(kind.ToString(), SvJsonNode.Integer(m.Graph.CountLinks(kind)));
        }

        return ApiJson.Obj(
            ("maxDrop", ApiJson.Num(m.Profile.MaxDrop)),
            ("nodes", SvJsonNode.Integer(m.Graph.NodeCount)),
            ("reached", SvJsonNode.Integer(m.ReachedCount)),
            ("starts", ApiJson.Arr(m.Starts.Select(s => (SvJsonNode)ApiJson.Arr([ApiJson.Str(s.Id), ApiJson.Bool(s.Node >= 0)])))),
            ("links", links),
            ("moverCols", m.Movers.Count > 0 ? ApiJson.Strings(["id", "kind", "works", "used", "by"]) : null),
            ("movers", m.Movers.Count > 0 ? ApiJson.Arr(m.Movers.Select(MoverRow)) : null));
    }

    /// <summary>A door or lift as a side found it: id, door or lift, how it works (walk, trigger, startsOpen, never),
    /// whether the side opened or rode it, and what opened it.</summary>
    private static SvJsonNode MoverRow(MoverUse u) => ApiJson.Arr([
        ApiJson.Str(u.Id), ApiJson.Str(u.Kind == MoverKind.Lift ? "lift" : "door"),
        ApiJson.Str(char.ToLowerInvariant(u.Works.ToString()[0]) + u.Works.ToString()[1..]), ApiJson.Bool(u.Used),
        u.By is { } by ? ApiJson.Str(by) : SvJsonNode.Null,
    ]);

    // ---- route -----------------------------------------------------------------------------------------------

    public static SvJsonObject Route(AnalysisMap map, SvJsonObject q, Func<SvJsonNode, string, Vec3> point)
    {
        Vec3 from = point(q["from"] ?? throw new ApiException("from is the start point (a point or an id)", "from"), "from");
        Vec3 to = point(q["to"] ?? throw new ApiException("to is the goal (a point or an id)", "to"), "to");
        ReachProfile profile = Profile(q);
        RouteResult r;
        lock (map.Gate)
        {
            r = Reachability.Route(map, profile, Mask(map, q), from, to);
        }

        return RouteJson(profile, r);
    }

    public static SvJsonObject RouteJson(ReachProfile profile, RouteResult r) => ApiJson.Obj(
        ("side", ApiJson.Str(profile.Side.ToString().ToLowerInvariant())),
        ("reachable", ApiJson.Bool(r.Reachable)),
        ("problem", r.Problem is { } p ? ApiJson.Str(p) : null),
        ("from", r.From is { } f ? ApiJson.Vec(Round(f)) : null),
        ("to", r.To is { } t ? ApiJson.Vec(Round(t)) : null),
        ("closest", r.Closest is { } c ? ApiJson.Vec(Round(c)) : null),
        ("length", r.Legs.Count > 0 ? ApiJson.Num(Math.Round(r.Length)) : null),
        ("damage", r.Damage > 0 ? ApiJson.Num(Math.Round(r.Damage)) : null),
        ("legs", r.Legs.Count > 0 ? ApiJson.Arr(r.Legs.Select(LegJson)) : null));

    private static SvJsonNode LegJson(RouteLeg l) => ApiJson.Obj(
        ("how", ApiJson.Str(l.How)),
        ("mover", l.Mover is { } mover ? ApiJson.Str(mover) : null),
        ("openedBy", l.OpenedBy is { } by ? ApiJson.Str(by) : null),
        ("from", ApiJson.Vec(Round(l.From))),
        ("to", ApiJson.Vec(Round(l.To))),
        ("length", ApiJson.Num(Math.Round(l.Length))),
        ("ge", l.Ge is { } ge ? ApiJson.Str(ge) : null),
        ("drop", l.Drop > 0 ? ApiJson.Num(Math.Round(l.Drop)) : null),
        ("damage", l.Damage > 0 ? ApiJson.Num(Math.Round(l.Damage)) : null));

    /// <summary>An edge kind as plain words: walk, crouch, drop, ledge climb, ladder up...</summary>
    public static string Words(NavEdgeKind kind) => kind switch
    {
        NavEdgeKind.LedgeClimb => "ledge climb",
        NavEdgeKind.PipeClimb => "pipe climb",
        NavEdgeKind.ZipLine => "zip line",
        NavEdgeKind.LadderUp => "ladder up",
        NavEdgeKind.LadderDown => "ladder down",
        NavEdgeKind.HandOverHand => "hand over hand",
        NavEdgeKind.FenceClimb => "fence climb",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ---- nav -------------------------------------------------------------------------------------------------

    public static SvJsonObject Nav(AnalysisMap map, SvJsonObject q, Func<SvJsonNode, string, Vec3> point)
    {
        ReachProfile profile = Profile(q, TeamSide.Spy) with { Drops = ApiJson.GetString(q, "drops") is { } d ? ReachProfile.ParseDrops(d) : DropRule.Bot };
        byte mask = Mask(map, q);
        NavGraph graph;
        lock (map.Gate)
        {
            graph = map.Graph(profile, mask);
        }

        var edges = new SvJsonObject();
        foreach (NavEdgeKind kind in Enum.GetValues<NavEdgeKind>().Where(k => graph.CountEdges(k) > 0))
        {
            edges.Add(Words(kind), SvJsonNode.Integer(graph.CountEdges(kind)));
        }

        var ges = map.GeList().ToDictionary(g => g.Index, g => g.Id);
        IEnumerable<NavLink> links = graph.Links;
        if (q["near"] is { } near)
        {
            FVector c = AnalysisMap.F(point(near, "near"));
            float radius = (float)ApiJson.Number(q, "radius", 1024);
            links = links.Where(l => (l.Start - c).Size() <= radius || (l.End - c).Size() <= radius);
        }

        int limit = (int)ApiJson.Number(q, "limit", 200);
        List<NavLink> shown = links.ToList();
        return ApiJson.Obj(
            ("side", ApiJson.Str(profile.Side.ToString().ToLowerInvariant())),
            ("drops", ApiJson.Str(profile.Drops.ToString().ToLowerInvariant())),
            ("mask", SvJsonNode.Integer(mask)),
            ("nodes", SvJsonNode.Integer(graph.NodeCount)),
            ("standing", SvJsonNode.Integer(Enumerable.Range(0, graph.NodeCount).Count(i => graph.Node(i).Standing))),
            ("edges", edges),
            ("cols", ApiJson.Strings(["how", "ge", "start", "end", "height"])),
            ("links", ApiJson.Arr(shown.Take(limit).Select(l => (SvJsonNode)ApiJson.Arr([
                ApiJson.Str(Words(l.Kind)), ges.TryGetValue(l.GE, out string? id) ? ApiJson.Str(id) : SvJsonNode.Null,
                ApiJson.Vec(Round(AnalysisMap.V(l.Start))), ApiJson.Vec(Round(AnalysisMap.V(l.End))), ApiJson.Num(Math.Round(l.Height)),
            ])))),
            ("more", shown.Count > limit ? SvJsonNode.True : null));
    }

    /// <summary>A cell within <paramref name="radius"/> across of a point, its floor from 400 below the point to 200 above.</summary>
    private static bool Near(StealthCell cell, Vec3 p, double radius)
    {
        Vec3 d = cell.Floor - p;
        return d.X * d.X + d.Y * d.Y <= radius * radius && d.Z is >= -400 and <= 200;
    }

    private static Vec3 Round(Vec3 v) => new(Math.Round(v.X), Math.Round(v.Y), Math.Round(v.Z));
}
