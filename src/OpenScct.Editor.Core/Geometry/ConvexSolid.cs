using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>Builds convex solids as outward-wound polygon lists: from half-spaces, from a point cloud, or as a prism.</summary>
public static class ConvexSolid
{
    /// <summary>Half size of the starting polygon each plane is clipped from; larger than any SCCT map.</summary>
    private const double Extent = 1 << 21;

    /// <summary>The solid inside every plane (the back sides), one outward face per plane that bounds it. Empty
    /// when the half-spaces do not enclose a solid.</summary>
    public static List<List<Vec3>> FromPlanes(IReadOnlyList<Plane3> planes, double tolerance = 1e-6)
    {
        var unique = new List<Plane3>(planes.Count);
        foreach (Plane3 p in planes)
        {
            if (!unique.Any(q => q.NearlyEquals(p, 1e-9, 1e-9)))
            {
                unique.Add(p);
            }
        }

        var faces = new List<List<Vec3>>(unique.Count);
        for (int i = 0; i < unique.Count; i++)
        {
            Plane3 plane = unique[i];
            (Vec3 u, Vec3 v) = Poly.Basis(plane.Normal);
            Vec3 c = plane.Normal * plane.D;
            List<Vec3>? poly = [c - u * Extent - v * Extent, c + u * Extent - v * Extent, c + u * Extent + v * Extent, c - u * Extent + v * Extent];
            for (int j = 0; j < unique.Count && poly is not null; j++)
            {
                if (j == i)
                {
                    continue;
                }

                PolySplit s = Poly.Split(poly, unique[j], tolerance);
                poly = s.Side switch
                {
                    PlaneSide.Front => null,
                    PlaneSide.Spanning => s.Back,
                    // A parallel plane facing the same way that lies on this one: keep only the first of the two.
                    PlaneSide.Coplanar => Vec3.Dot(unique[j].Normal, plane.Normal) > 0 && j < i ? null : poly,
                    _ => poly,
                };
            }

            if (poly is not null && Poly.Area(poly) > 1e-6)
            {
                faces.Add(poly);
            }
        }

        return faces;
    }

    /// <summary>The convex hull of a point cloud as outward faces (coplanar triangles merged into polygons). Empty
    /// when the points are flat or fewer than four.</summary>
    public static List<List<Vec3>> Hull(IEnumerable<Vec3> points)
    {
        var pts = new List<Vec3>();
        foreach (Vec3 p in points)
        {
            if (!pts.Any(q => (q - p).LengthSquared < 1e-12))
            {
                pts.Add(p);
            }
        }

        if (pts.Count < 4)
        {
            return [];
        }

        double size = pts.Max(p => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        double eps = 1e-7 * Math.Max(1, size);
        if (TriangleHull(pts, eps) is not { } triangles)
        {
            return []; // flat
        }

        // The supporting planes of the hull triangles, each once; a face is the 2D hull of every point on its plane
        // (exact corners, no clipping noise). A triangle whose corners all lie on a plane already found is part of
        // that face: two triangles of one face can give normals a little apart when the corners are rounded. Large
        // triangles go first, as their planes are the most accurate.
        var planes = new List<Plane3>();
        foreach ((int a, int b, int c) in triangles.OrderByDescending(t => Vec3.Cross(pts[t.B] - pts[t.A], pts[t.C] - pts[t.A]).LengthSquared))
        {
            if (planes.Any(q => OnPlane(q, pts[a], eps) && OnPlane(q, pts[b], eps) && OnPlane(q, pts[c], eps)))
            {
                continue;
            }

            if (Plane3.FromPoints(pts[a], pts[b], pts[c]) is { } plane)
            {
                planes.Add(plane);
            }
        }

        var faces = new List<List<Vec3>>(planes.Count);
        foreach (Plane3 plane in planes)
        {
            List<Vec3>? face = Poly.Hull2D(pts.Where(p => OnPlane(plane, p, eps)), plane.Normal, 1e-9);
            if (face is not null && Poly.Area(face) > 1e-9)
            {
                faces.Add(face);
            }
        }

        return faces;
    }

    /// <summary>
    /// Incremental hull: outward triangles (index triples, normal by the right-hand rule) of the points, or null when
    /// they are flat. Points within <paramref name="eps"/> of the surface are not added (the per-plane outlines pick
    /// them up). Quadratic in the number of points.
    /// </summary>
    private static List<(int A, int B, int C)>? TriangleHull(List<Vec3> pts, double eps)
    {
        // A starting tetrahedron from points far apart.
        int i0 = 0;
        int i1 = Farthest(pts, p => (p - pts[i0]).LengthSquared);
        int i2 = Farthest(pts, p => Vec3.Cross(pts[i1] - pts[i0], p - pts[i0]).LengthSquared);
        Vec3 n = Vec3.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]);
        if (n.LengthSquared < eps * eps)
        {
            return null;
        }

        int i3 = Farthest(pts, p => Math.Abs(Vec3.Dot(n.Normalized(), p - pts[i0])));
        if (Math.Abs(Vec3.Dot(n.Normalized(), pts[i3] - pts[i0])) <= eps)
        {
            return null;
        }

        Vec3 inside = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) / 4;
        var faces = new List<(int A, int B, int C)>();
        void AddFace(int a, int b, int c)
        {
            Vec3 normal = Vec3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            faces.Add(Vec3.Dot(normal, pts[a] - inside) >= 0 ? (a, b, c) : (a, c, b));
        }

        AddFace(i0, i1, i2);
        AddFace(i0, i1, i3);
        AddFace(i0, i2, i3);
        AddFace(i1, i2, i3);
        for (int k = 0; k < pts.Count; k++)
        {
            if (k == i0 || k == i1 || k == i2 || k == i3)
            {
                continue;
            }

            Vec3 p = pts[k];
            var visible = faces.Where(f => Distance(pts, f, p) > eps).ToList();
            if (visible.Count == 0)
            {
                continue; // inside or on the surface
            }

            // The horizon: edges of visible faces whose other face is not visible.
            var edges = new HashSet<(int, int)>();
            foreach ((int a, int b, int c) in visible)
            {
                edges.Add((a, b));
                edges.Add((b, c));
                edges.Add((c, a));
            }

            faces.RemoveAll(visible.Contains);
            foreach ((int a, int b) in edges)
            {
                if (!edges.Contains((b, a)))
                {
                    faces.Add((a, b, k));
                }
            }
        }

        return faces;
    }

    private static int Farthest(List<Vec3> pts, Func<Vec3, double> score)
    {
        int best = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (score(pts[i]) > score(pts[best]))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Signed distance of a point from a triangle's plane (positive outside), in units.</summary>
    private static double Distance(List<Vec3> pts, (int A, int B, int C) f, Vec3 p)
    {
        Vec3 normal = Vec3.Cross(pts[f.B] - pts[f.A], pts[f.C] - pts[f.A]);
        double len = normal.Length;
        return len < 1e-18 ? 0 : Vec3.Dot(normal, p - pts[f.A]) / len;
    }

    private static bool OnPlane(Plane3 plane, Vec3 p, double eps) => Math.Abs(plane.Distance(p)) <= eps * 10;

    /// <summary>A prism: a convex outline in the XY plane (any winding) extruded from <paramref name="z0"/> to
    /// <paramref name="z1"/>.</summary>
    public static List<List<Vec3>> Prism(IReadOnlyList<(double X, double Y)> outline, double z0, double z1)
    {
        List<(double X, double Y)> ring = outline.ToList();
        double signedArea = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            (double x0, double y0) = ring[i];
            (double x1, double y1) = ring[(i + 1) % ring.Count];
            signedArea += x0 * y1 - x1 * y0;
        }

        if (signedArea < 0)
        {
            ring.Reverse(); // counter-clockwise about +Z
        }

        var faces = new List<List<Vec3>>(ring.Count + 2);
        faces.Add(ring.Select(p => new Vec3(p.X, p.Y, z1)).ToList());
        faces.Add(ring.Select(p => new Vec3(p.X, p.Y, z0)).Reverse().ToList());
        for (int i = 0; i < ring.Count; i++)
        {
            (double ax, double ay) = ring[i];
            (double bx, double by) = ring[(i + 1) % ring.Count];
            faces.Add([new Vec3(ax, ay, z0), new Vec3(bx, by, z0), new Vec3(bx, by, z1), new Vec3(ax, ay, z1)]);
        }

        return faces;
    }

    /// <summary>An axis-aligned box from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static List<List<Vec3>> Box(Vec3 min, Vec3 max) =>
        Prism([(min.X, min.Y), (max.X, min.Y), (max.X, max.Y), (min.X, max.Y)], min.Z, max.Z);

    /// <summary>Planes of a convex face list (outward).</summary>
    public static List<Plane3> PlanesOf(IEnumerable<IReadOnlyList<Vec3>> faces) =>
        faces.Select(Plane3.FromPolygon).Where(p => p.HasValue).Select(p => p!.Value).ToList();
}
