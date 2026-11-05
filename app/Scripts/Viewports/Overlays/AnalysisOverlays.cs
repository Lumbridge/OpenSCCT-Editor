using Godot;
using OpenScct.Core;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.AI;
using OpenScctEditor.Gameplay;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>Shared look of the analysis overlays: a result made before the map's last edit is drawn faded and says so.</summary>
internal static class AnalysisLook
{
    public const string StaleText = "out of date: the map changed, run the analysis again";

    public static Color Fade(Color c, bool stale) => stale ? new Color(c.R, c.G, c.B, c.A * 0.35f) : c;
}

/// <summary>
/// The stealth light map (Gameplay/Analysis): one dot per floor cell spies reach, coloured by how lit a spy is there
/// (crouched, or standing when chosen): blue hidden, amber partly lit, red exposed; a white ring where a spy who is not
/// exposed stands out dark against a bright background behind him. Where mercs' trackers and torches reach is the Merc
/// Sight overlay.
/// </summary>
public sealed class StealthOverlay : IViewOverlay
{
    public static readonly Color Hidden = new(0.25f, 0.45f, 1f);
    public static readonly Color Partial = new(1f, 0.75f, 0.2f);
    public static readonly Color Exposed = new(1f, 0.25f, 0.2f);
    public static readonly Color Silhouette = new(1f, 1f, 1f);

    public string Id => "stealth";

    public string Title => "Stealth Light Map (blue hidden, amber partly lit, red exposed, white ring silhouetted)";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Stealth is not { Cells.Count: > 0 } r)
        {
            return;
        }

        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Stealth));
        foreach (StealthCell cell in r.Cells)
        {
            Color colour = cell.Band switch
            {
                StealthBand.Hidden => Hidden,
                StealthBand.Partial => Partial,
                _ => Exposed,
            };
            batch.Dot(cell.Floor + new Vec3(0, 0, 4), AnalysisLook.Fade(colour, stale), 2.5f);
            if (cell.Silhouette)
            {
                batch.Ring(cell.Floor + new Vec3(0, 0, 4), Vec3.UnitX, Vec3.UnitY, 12, AnalysisLook.Fade(Silhouette, stale), 1.5f, segments: 12);
            }
        }

        if (stale)
        {
            batch.Label(r.Cells[0].Floor + new Vec3(0, 0, 16), AnalysisLook.StaleText, Exposed, pinned: true);
        }
    }
}

/// <summary>
/// The lights of the last stealth map (Gameplay/Analysis) that light only one part of the lighting, off by default as
/// a large map has hundreds: grey where a light lights the world (baked or drawn live) but not pawns (the place looks
/// lit, a spy there is not), pink where it lights pawns but not the world (a spy is lit where the place looks dark). A
/// dot on the light and a dashed ring at its reach; a sun gets a label instead, as it reaches the whole map.
/// </summary>
public sealed class LightRoleOverlay : IViewOverlay
{
    public static readonly Color WorldOnly = new(0.8f, 0.8f, 0.8f);
    public static readonly Color PawnsOnly = new(1f, 0.4f, 0.8f);

    public string Id => "stealth.lightRoles";

    public string Title => "Light Roles (grey lights only the world, pink only pawns)";

    public bool OnByDefault => false;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Stealth is not { } r)
        {
            return;
        }

        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Stealth));
        foreach (LightRole l in r.LightRoles)
        {
            Color colour = AnalysisLook.Fade(l.Kind == LightRoleKind.WorldOnly ? WorldOnly : PawnsOnly, stale);
            batch.Dot(l.Location, colour, 4);
            if (l.Sun)
            {
                batch.Label(l.Location + new Vec3(0, 0, 16), l.Kind == LightRoleKind.WorldOnly ? "sun: world only" : "sun: pawns only", colour, pinned: true);
            }
            else
            {
                batch.Ring(l.Location, new Vec3(1, 0, 0), new Vec3(0, 1, 0), l.Radius, colour, dashed: true);
            }
        }
    }
}

/// <summary>
/// Merc sight of the last stealth map (Gameplay/Analysis), off by default: a small violet dot over each cell a merc's
/// motion tracker can lock from somewhere mercs reach (a spy moving at 150 or more there, crouched or not), yellow
/// where a merc torch reaches too. Kept apart from the light bands because most of a map is in some tracker's range.
/// </summary>
public sealed class MercSightOverlay : IViewOverlay
{
    public static readonly Color Tracked = new(0.7f, 0.35f, 1f);
    public static readonly Color Torch = new(1f, 0.88f, 0.65f);

    public string Id => "stealth.mercSight";

    public string Title => "Merc Sight (violet tracker, yellow torch)";

    public bool OnByDefault => false;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Stealth is not { } r)
        {
            return;
        }

        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Stealth));
        foreach (StealthCell cell in r.Cells.Where(cell => cell.Tracked))
        {
            batch.Dot(cell.Floor + new Vec3(0, 0, 10), AnalysisLook.Fade(cell.InTorchReach ? Torch : Tracked, stale), 1.5f);
        }
    }
}

/// <summary>
/// Reachability (Gameplay/Analysis): the floors each side reaches from its starts (walking, climbing, through the doors
/// and lifts it can work), one dot per 128 units:
/// green where both sides go, cyan where only spies go, orange where only mercs go.
/// </summary>
public sealed class ReachOverlay : IViewOverlay
{
    public static readonly Color Both = new(0.35f, 0.85f, 0.35f);
    public static readonly Color SpyOnly = new(0.2f, 0.9f, 1f);
    public static readonly Color MercOnly = new(1f, 0.55f, 0.15f);

    private const float Spacing = 128f;

    public string Id => "reach";

    public string Title => "Reachability";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Reach is not { } r)
        {
            return;
        }

        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Reach));
        var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < r.Spy.Graph.NodeCount; i++)
        {
            if (r.Spy.Reached(i) && seen.Add(Cell(r.Spy.Graph.Node(i).Floor)))
            {
                batch.Dot(AnalysisMap.V(r.Spy.Graph.Node(i).Floor) + new Vec3(0, 0, 4), AnalysisLook.Fade(r.SpyOnly[i] ? SpyOnly : Both, stale), 2.5f);
            }
        }

        for (int i = 0; i < r.Merc.Graph.NodeCount; i++)
        {
            if (r.Merc.Reached(i) && seen.Add(Cell(r.Merc.Graph.Node(i).Floor)))
            {
                batch.Dot(AnalysisMap.V(r.Merc.Graph.Node(i).Floor) + new Vec3(0, 0, 4), AnalysisLook.Fade(MercOnly, stale), 2.5f);
            }
        }

        if (stale && r.Spy.Starts.FirstOrDefault(s => s.Node >= 0) is { Id: not null } start)
        {
            batch.Label(AnalysisMap.V(r.Spy.Graph.Node(start.Node).Floor) + new Vec3(0, 0, 16), AnalysisLook.StaleText, MercOnly, pinned: true);
        }
    }

    private static (int, int, int) Cell(FVector f) => ((int)MathF.Floor(f.X / Spacing), (int)MathF.Floor(f.Y / Spacing), (int)MathF.Floor(f.Z / 64f));
}

/// <summary>
/// The bots' navigation graph (Gameplay/Analysis): every traversal link (ledges, pipes, zip lines, ladders, fences)
/// as a labelled arrow; drops (dashed arrows) and walk and crouch edges only within 1024 of the selection, since a
/// whole map has thousands of drops and hundreds of thousands of edges.
/// </summary>
public sealed class NavGraphOverlay : IViewOverlay
{
    public static readonly Color WalkColour = new(0.55f, 0.6f, 0.65f, 0.6f);
    public static readonly Color CrouchColour = new(0.4f, 0.7f, 1f, 0.7f);
    public static readonly Color DropColour = new(1f, 0.6f, 0.3f, 0.7f);
    public static readonly Color LinkColour = new(0.3f, 1f, 0.5f);

    private const double EdgeRadius = 1024;

    public string Id => "navgraph";

    public string Title => "Bots' Nav Graph";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Nav is not { } nav)
        {
            return;
        }

        NavGraph g = nav.Graph;
        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Nav));
        Color link = AnalysisLook.Fade(LinkColour, stale);
        foreach (NavLink l in g.Links)
        {
            Vec3 start = AnalysisMap.V(l.Start), end = AnalysisMap.V(l.End);
            batch.Arrow(start, end, link, 2);
            string ge = nav.Ges.TryGetValue(l.GE, out string? id) ? " " + id : "";
            batch.Label((start + end) / 2, AnalysisJson.Words(l.Kind) + ge, link);
        }

        if (c.Selected.Count == 0)
        {
            return;
        }

        Vec3 centre = SelectionCentre(c);
        if (stale)
        {
            batch.Label(centre, AnalysisLook.StaleText, DropColour, pinned: true);
        }

        Color drop = AnalysisLook.Fade(DropColour, stale), walk = AnalysisLook.Fade(WalkColour, stale), crouch = AnalysisLook.Fade(CrouchColour, stale);
        for (int n = 0; n < g.NodeCount; n++)
        {
            Vec3 from = AnalysisMap.V(g.Node(n).Floor);
            if ((from - centre).Length > EdgeRadius)
            {
                continue;
            }

            foreach ((int to, NavEdgeKind kind, _, int linkIndex) in g.EdgesOf(n))
            {
                if (linkIndex >= 0)
                {
                    continue;
                }

                Vec3 end = AnalysisMap.V(g.Node(to).Floor);
                if (kind == NavEdgeKind.Drop)
                {
                    batch.Line(from, end, drop, 1, dashed: true, arrow: true);
                }
                else if (n < to)
                {
                    batch.Line(from + new Vec3(0, 0, 2), end + new Vec3(0, 0, 2), kind == NavEdgeKind.Crouch ? crouch : walk, 1);
                }
            }
        }
    }

    private static Vec3 SelectionCentre(OverlayContext c)
    {
        Vec3 sum = Vec3.Zero;
        foreach (ObjectRef r in c.Selected)
        {
            (Vec3 min, Vec3 max) = MapObjects.Bounds(c.Project, r);
            sum += (min + max) / 2;
        }

        return sum / c.Selected.Count;
    }
}

/// <summary>The last route asked for (Gameplay/Analysis): its legs, GE legs and hurting drops labelled (red when its
/// drops add up to a fatal fall), or the dead end it stops at.</summary>
public sealed class RouteOverlay : IViewOverlay
{
    /// <summary>A warm white, so the route never reads as the (yellow) selection.</summary>
    public static readonly Color RouteColour = new(0.98f, 0.93f, 0.82f);
    public static readonly Color DeadEnd = new(1f, 0.3f, 0.3f);

    public string Id => "route";

    public string Title => "Route";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (AnalysisState.Instance.Route is not { } r)
        {
            return;
        }

        var lift = new Vec3(0, 0, 8);
        bool stale = AnalysisState.Instance.IsStale(nameof(AnalysisState.Route));
        Color colour = AnalysisLook.Fade(r.Route.Reachable ? RouteColour : DeadEnd, stale);
        foreach (RouteLeg leg in r.Route.Legs)
        {
            batch.Arrow(leg.From + lift, leg.To + lift, colour, leg.IsWalk ? 2 : 3);
            if (!leg.IsWalk)
            {
                string damage = leg.Damage > 0 ? $" (-{Math.Round(leg.Damage)}% health)" : "";
                batch.Label((leg.From + leg.To) / 2 + lift, leg.Label + damage, colour, pinned: true, whole: true);
            }
        }

        if (stale && r.Route.From is { } from)
        {
            batch.Label(from + lift * 2, AnalysisLook.StaleText, DeadEnd, pinned: true);
        }

        if (!r.Route.Reachable && r.Route.Closest is { } closest)
        {
            batch.Dot(closest + lift, DeadEnd, 6);
            batch.Label(closest + lift, $"a {(r.Profile.Side == TeamSide.Spy ? "spy" : "merc")} gets no further", DeadEnd, pinned: true);
            if (r.Route.To is { } goal)
            {
                batch.Line(closest + lift, goal + lift, DeadEnd, 1, dashed: true);
            }
        }
    }
}

/// <summary>
/// The shapes of the Map Check findings on show: a zone leak's way from one zone actor to the other (dashed) and the
/// smallest opening that would seal it, and a portal that seals nothing with the way round it. Drawn faded once the
/// map has changed since the check, and gone when the check is run again and finds none.
/// </summary>
public sealed class FindingShapesOverlay : IViewOverlay
{
    // The semantic colours of the finding (yellow is the views' selection colour, so the openings are amber).
    public static readonly Color PathColour = Shell.EditorTheme.Error;
    public static readonly Color OutlineColour = Shell.EditorTheme.Warning;

    public string Id => "findings.shapes";

    public string Title => "Map Check Shapes (leak paths, openings to seal)";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        bool stale = c.Editor.FindingsStale;
        Color path = AnalysisLook.Fade(PathColour, stale), outline = AnalysisLook.Fade(OutlineColour, stale);
        foreach (Finding f in c.Editor.Findings.Where(f => f.Shapes.Count > 0))
        {
            foreach (FindingShape shape in f.Shapes)
            {
                if (shape.Kind == FindingShapeKind.Path)
                {
                    batch.Polyline(shape.Points, path, 2, dashed: true);
                    batch.Dot(shape.Points[0], path, 4);
                    batch.Dot(shape.Points[^1], path, 4);
                }
                else
                {
                    batch.Polyline(shape.Points, outline, 2.5f, closed: true);
                }
            }

            if (f.Location is { } at)
            {
                string label = Label(f) + (stale ? " (out of date: check again)" : "");
                batch.Label(at, label, f.Shapes.Any(s => s.Kind == FindingShapeKind.Outline) ? outline : path, pinned: true, whole: true);
            }
        }
    }

    /// <summary>What the shape shows, in words (the Map Check panel has the full message).</summary>
    private static string Label(Finding f) => f.Code switch
    {
        MapCompiler.ZoneLeakCode => f.Shapes.Any(s => s.Kind == FindingShapeKind.Outline) ? "zone leak: put a zone portal here" : "zone leak: the way through",
        MapCompiler.ZonePortalCode => "zone portal that seals nothing",
        _ => f.Code,
    };
}
