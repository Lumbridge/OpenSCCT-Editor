using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>What a vertex edit made: the new brush, how many faces were split to stay planar, and whether the
/// result is still a clean convex solid (the engine's CSG needs that).</summary>
public sealed record VertexEditResult(Brush Brush, int SplitFaces, int RemovedFaces, bool Convex)
{
    /// <summary>A one-line warning for the status bar, or null when the brush is fine.</summary>
    public string? Warning => Convex
        ? SplitFaces > 0 ? $"{Brush.Id}: {SplitFaces} face{(SplitFaces == 1 ? "" : "s")} split into triangles to stay flat" : null
        : $"{Brush.Id} is no longer convex: CSG will be wrong (weld, undo or move the vertex back)";
}

/// <summary>
/// Vertex and edge editing of brushes. Vertices are named by their world position (what the viewports show); every
/// face corner within <see cref="Tolerance"/> of a selected position moves with it, so shared corners stay shared.
/// Faces that stop being flat (or convex) after a move are split into triangles; texture mapping stays where it
/// was on the surface. Each method returns a new brush and drops the builder spec (it no longer describes it).
/// </summary>
public static class VertexEdit
{
    /// <summary>How close (units) a corner must be to a selected position to be that vertex.</summary>
    public const double Tolerance = 0.5;

    /// <summary>The brush's distinct vertices in world space.</summary>
    public static List<Vec3> Vertices(Brush b) => DistinctVertices(b.WorldFaces().Select(f => f.Vertices));

    /// <summary>The distinct corners of some polygons (corners within <see cref="Tolerance"/> are one).</summary>
    public static List<Vec3> DistinctVertices(IEnumerable<IReadOnlyList<Vec3>> faces)
    {
        var result = new List<Vec3>();
        foreach (IReadOnlyList<Vec3> f in faces)
        {
            foreach (Vec3 v in f)
            {
                if (!result.Exists(p => Near(p, v)))
                {
                    result.Add(v);
                }
            }
        }

        return result;
    }

    /// <summary>The brush's distinct edges in world space.</summary>
    public static List<(Vec3 A, Vec3 B)> Edges(Brush b) => DistinctEdges(b.WorldFaces().Select(f => f.Vertices));

    /// <summary>The distinct edges of some polygons (an edge shared by two faces once).</summary>
    public static List<(Vec3 A, Vec3 B)> DistinctEdges(IEnumerable<IReadOnlyList<Vec3>> faces)
    {
        var result = new List<(Vec3 A, Vec3 B)>();
        foreach (IReadOnlyList<Vec3> f in faces)
        {
            for (int i = 0; i < f.Count; i++)
            {
                Vec3 a = f[i], c = f[(i + 1) % f.Count];
                if (!result.Exists(e => (Near(e.A, a) && Near(e.B, c)) || (Near(e.A, c) && Near(e.B, a))))
                {
                    result.Add((a, c));
                }
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="v"/> is one of the selected positions.</summary>
    public static bool IsSelected(IEnumerable<Vec3> selected, Vec3 v) => selected.Any(s => Near(s, v));

    /// <summary>Moves the selected vertices by a world offset.</summary>
    public static VertexEditResult Move(Brush b, IReadOnlyCollection<Vec3> selected, Vec3 delta) =>
        Remap(b, v => IsSelected(selected, v) ? v + delta : v);

    /// <summary>Snaps the selected vertices (all when <paramref name="selected"/> is null) to the world grid.</summary>
    public static VertexEditResult Snap(Brush b, IReadOnlyCollection<Vec3>? selected, double grid) =>
        Remap(b, v => selected is null || IsSelected(selected, v) ? v.Snap(grid) : v);

    /// <summary>Welds the selected vertices into one point: <paramref name="at"/>, or their centre. Faces that collapse
    /// are removed. Several brushes welded together pass the same <paramref name="at"/> so they meet.</summary>
    public static VertexEditResult Weld(Brush b, IReadOnlyCollection<Vec3> selected, Vec3? at = null)
    {
        List<Vec3> hits = Vertices(b).Where(v => IsSelected(selected, v)).ToList();
        if (hits.Count < (at is null ? 2 : 1))
        {
            throw new ArgumentException("weld: select two or more vertices of the brush");
        }

        Vec3 target = Poly.Tidy(at ?? Centre(hits));
        return Remap(b, v => IsSelected(selected, v) ? target : v);
    }

    /// <summary>The average of the points (zero for none).</summary>
    public static Vec3 Centre(IReadOnlyCollection<Vec3> points) =>
        points.Count == 0 ? Vec3.Zero : points.Aggregate(Vec3.Zero, (s, v) => s + v) / points.Count;

    /// <summary>
    /// Deletes the selected vertices: the brush becomes the convex hull of the vertices left. Faces that keep their
    /// plane keep their material and mapping. Fails when fewer than four vertices (or only a flat set) remain.
    /// </summary>
    public static VertexEditResult Delete(Brush b, IReadOnlyCollection<Vec3> selected)
    {
        ShapeTransform t = ShapeTransform.For(b);
        var kept = new BrushShape();
        int removed = 0;
        foreach (BrushFace f in b.Shape.Faces)
        {
            BrushFace c = f.Clone();
            c.Vertices = f.Vertices.Where(v => !IsSelected(selected, t.ToWorld(v))).ToList();
            removed += f.Vertices.Count - c.Vertices.Count;
            kept.Faces.Add(c);
        }

        if (removed == 0)
        {
            throw new ArgumentException("delete vertex: none of the selected vertices belongs to the brush");
        }

        BrushShape hull = BrushOps.ConvexHull(kept);
        if (hull.Faces.Count < 4)
        {
            throw new ArgumentException("delete vertex: too few vertices would be left for a solid");
        }

        Brush o = b.Clone();
        o.Shape = hull;
        return new VertexEditResult(o, 0, Math.Max(0, b.Shape.Faces.Count - hull.Faces.Count), ShapeAnalysis.IsConvexSolid(hull));
    }

    /// <summary>
    /// Bevels (chamfers) the edge from <paramref name="a"/> to <paramref name="c"/>: a new face cuts it off,
    /// <paramref name="size"/> units back along both faces that meet there. Needs a convex brush.
    /// </summary>
    public static Brush Bevel(Brush b, Vec3 a, Vec3 c, double size)
    {
        if (size <= 0)
        {
            throw new ArgumentException("bevel: the size must be positive");
        }

        Vec3 e = (c - a).Normalized();
        var faces = b.WorldFaces().Select((f, i) => (Face: f, Index: i))
            .Where(x => HasEdge(x.Face.Vertices, a, c))
            .ToList();
        if (faces.Count != 2 || e == Vec3.Zero)
        {
            throw new ArgumentException("bevel: pick an edge where two faces of the brush meet");
        }

        Vec3 Inward(BrushFace f)
        {
            Vec3 n = f.Normal();
            Vec3 w = Vec3.Cross(n, e).Normalized();
            return Vec3.Dot(w, Poly.Average(f.Vertices) - a) >= 0 ? w : -w;
        }

        Vec3 p1 = a + Inward(faces[0].Face) * size, p2 = a + Inward(faces[1].Face) * size;
        Vec3 outward = faces[0].Face.Normal() + faces[1].Face.Normal();
        Plane3 plane = Plane3.FromPoints(p1, p2, p1 + e) ?? throw new ArgumentException("bevel: the faces at that edge are flat");
        if (Vec3.Dot(plane.Normal, outward) < 0)
        {
            plane = plane.Flipped;
        }

        return BrushOps.Clip(b, plane, faces[0].Face.Material) ?? throw new ArgumentException("bevel: the size cuts away the whole brush");
    }

    /// <summary>Maps every vertex through <paramref name="map"/> (world space) and repairs the faces.</summary>
    public static VertexEditResult Remap(Brush b, Func<Vec3, Vec3> map)
    {
        ShapeTransform t = ShapeTransform.For(b);
        var moved = new List<(BrushFace Face, List<Vec3>? Ring)>();
        foreach (BrushFace f in b.Shape.Faces)
        {
            List<Vec3>? ring = Poly.Clean(f.Vertices.Select(v => Poly.Tidy(t.ToLocal(map(t.ToWorld(v))))).ToList());
            moved.Add((f, ring is not null && Poly.Area(ring) >= ShapeAnalysis.ZeroArea ? ring : null));
        }

        List<Vec3> all = moved.Where(m => m.Ring is not null).SelectMany(m => m.Ring!).ToList();
        var shape = new BrushShape();
        int split = 0, removed = 0;
        foreach ((BrushFace f, List<Vec3>? ring) in moved)
        {
            if (ring is null)
            {
                removed++;
                continue;
            }

            if (ring.Count > 3 && (Poly.PlanarError(ring) > ShapeAnalysis.PlanarTolerance || !Poly.IsConvex(ring, Poly.Normal(ring), 1e-4)))
            {
                split++;
                shape.Faces.AddRange(Fan(ring, all).Select(tri => WithVertices(f, tri)));
                continue;
            }

            shape.Faces.Add(WithVertices(f, ring));
        }

        if (shape.Faces.Count < 4 && b.Shape.Faces.Count >= 4)
        {
            throw new ArgumentException("the brush would collapse (fewer than four faces left)");
        }

        Brush o = b.Clone();
        o.Shape = shape;
        return new VertexEditResult(o, split, removed, ShapeAnalysis.IsConvexSolid(shape));
    }

    /// <summary>Triangles of a bent face: the fan (from whichever corner) that keeps every vertex of the brush behind
    /// its triangles, so a convex brush stays convex; the first fan when none does.</summary>
    private static List<List<Vec3>> Fan(List<Vec3> ring, List<Vec3> all)
    {
        List<List<Vec3>>? first = null;
        for (int start = 0; start < ring.Count; start++)
        {
            var tris = new List<List<Vec3>>();
            for (int i = 1; i + 1 < ring.Count; i++)
            {
                List<Vec3> tri = [ring[start], ring[(start + i) % ring.Count], ring[(start + i + 1) % ring.Count]];
                if (Poly.Area(tri) >= ShapeAnalysis.ZeroArea)
                {
                    tris.Add(tri);
                }
            }

            first ??= tris;
            if (tris.All(tri => Plane3.FromPolygon(tri) is not { } pl || all.All(v => pl.Distance(v) <= ShapeAnalysis.PlanarTolerance)))
            {
                return tris;
            }
        }

        return first ?? [];
    }

    public static bool Near(Vec3 a, Vec3 b) => (a - b).LengthSquared <= Tolerance * Tolerance;

    private static bool HasEdge(List<Vec3> poly, Vec3 a, Vec3 c)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 p = poly[i], q = poly[(i + 1) % poly.Count];
            if ((Near(p, a) && Near(q, c)) || (Near(p, c) && Near(q, a)))
            {
                return true;
            }
        }

        return false;
    }

    private static BrushFace WithVertices(BrushFace f, List<Vec3> vertices)
    {
        BrushFace c = f.Clone();
        c.Vertices = vertices;
        return c;
    }
}
