using System.Runtime.CompilerServices;
using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Gameplay;

/// <summary>Visible geometric edges under the cursor, independent of the drag's camera-facing plane.</summary>
internal static class GeEdgePicking
{
    private readonly record struct Edge(Vec3 A, Vec3 B, bool Top, string? Brush);
    private static readonly ConditionalWeakTable<TraversalWorld, List<Edge>> Cache = new();

    public static ScreenPoint? Project(ViewCamera camera, Vec3 point) => camera.ToScreen(point) is { } p
        ? new(p.X, p.Y, camera.IsOrtho ? 1 : Vec3.Dot(point - camera.Location, camera.LookDirection)) : null;

    public static ScreenEdgeHit? Near(ViewCamera camera, Vector2 mouse, Vec3 a, Vec3 b, double radius, bool vertices = true)
    {
        if (!camera.IsOrtho)
        {
            double da = Vec3.Dot(a - camera.Location, camera.LookDirection), db = Vec3.Dot(b - camera.Location, camera.LookDirection);
            const double near = 6;
            if (da < near && db < near) return null;
            if (da < near) { a = Vec3.Lerp(a, b, (near - da) / (db - da)); vertices = false; }
            if (db < near) { b = Vec3.Lerp(b, a, (near - db) / (Math.Max(da, near) - db)); vertices = false; }
        }
        return Project(camera, a) is { } pa && Project(camera, b) is { } pb
            ? ScreenEdgePicking.NearProjected(a, b, pa, pb, mouse.X, mouse.Y, radius, vertices) : null;
    }

    public static bool Visible(TraversalWorld world, ViewCamera camera, Vec3 point)
    {
        if (camera.ToScreen(point) is not { } pixel) return false;
        (Vec3 origin, Vec3 direction) = camera.Ray(pixel);
        if (camera.IsOrtho) origin -= direction * 65536;
        TraceHit? hit = world.Trace(origin, point);
        return hit is null || hit.Value.Distance >= (point - origin).Length - 2;
    }

    public static ScreenEdgeHit? Pick(TraversalWorld world, ViewCamera camera, Vector2 mouse, double radius, string excludedGe, bool topOnly, Vec3? vertical = null)
    {
        string prefix = excludedGe + "_Geometry";
        var hits = new List<ScreenEdgeHit>();
        if (vertical is { } line)
        {
            foreach (SurfacePoly surface in world.Surfaces.Where(s => Math.Abs(s.Normal.Z) > .01 && s.BrushId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true))
            {
                Vec3 a = surface.Verts[0], n = surface.Normal;
                Vec3 point = line with { Z = a.Z - ((line.X - a.X) * n.X + (line.Y - a.Y) * n.Y) / n.Z };
                bool inside = true;
                for (int i = 0; i < surface.Verts.Length; i++)
                    if (Vec3.Dot(Vec3.Cross(surface.Verts[(i + 1) % surface.Verts.Length] - surface.Verts[i], point - surface.Verts[i]), n) < -.1) { inside = false; break; }
                if (inside && Near(camera, mouse, point, point, radius) is { } hit) hits.Add(hit);
            }
        }
        else
        {
            foreach (Edge edge in Cache.GetValue(world, Edges))
                if ((!topOnly || edge.Top) && edge.Brush?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true
                    && Near(camera, mouse, edge.A, edge.B, radius) is { } hit) hits.Add(hit);
        }
        foreach (ScreenEdgeHit hit in hits.OrderBy(h => h.Pixels - (h.Vertex ? 2 : 0)).ThenBy(h => (h.Point - camera.Location).Length).Take(16))
            if (Visible(world, camera, hit.Point)) return hit;
        return null;
    }

    private static List<Edge> Edges(TraversalWorld world)
    {
        var edges = new Dictionary<(Vec3, Vec3), (Edge Edge, Vec3 Normal, bool Internal)>();
        foreach (SurfacePoly surface in world.Surfaces)
        {
            for (int i = 0; i < surface.Verts.Length; i++)
            {
                Vec3 a = Poly.Tidy(surface.Verts[i]), b = Poly.Tidy(surface.Verts[(i + 1) % surface.Verts.Length]);
                if ((a - b).Length < .01) continue;
                bool swap = a.X > b.X || a.X == b.X && (a.Y > b.Y || a.Y == b.Y && a.Z > b.Z);
                var key = swap ? (b, a) : (a, b);
                bool top = surface.Normal.Z > .7 && Math.Abs(a.Z - b.Z) <= .5;
                if (edges.TryGetValue(key, out var other))
                    edges[key] = (other.Edge with { Top = other.Edge.Top || top }, other.Normal, other.Internal || Vec3.Dot(other.Normal, surface.Normal) > .999);
                else edges[key] = (new(a, b, top, surface.BrushId), surface.Normal, false);
            }
        }
        return edges.Values.Where(e => !e.Internal).Select(e => e.Edge).ToList();
    }
}
