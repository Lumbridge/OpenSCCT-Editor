using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Turns CSG polygons into render triangles: T-junction repair, fans.</summary>
public static class MeshPrep
{
    /// <summary>Inserts into each polygon edge the vertices of other polygons that lie on it, so neighbouring
    /// polygons meet at shared vertices and no cracks show between them.</summary>
    public static List<Poly> FixTJunctions(IReadOnlyList<Poly> polys)
    {
        const double cellSize = 64;
        var grid = new Dictionary<(long, long, long), List<Vec3>>();
        var seen = new HashSet<(long, long, long)>();
        foreach (Poly p in polys)
        {
            foreach (Vec3 v in p.Verts)
            {
                if (seen.Add(Quant(v)))
                {
                    var key = Cell(v, cellSize);
                    if (!grid.TryGetValue(key, out List<Vec3>? list))
                    {
                        list = new List<Vec3>();
                        grid[key] = list;
                    }

                    list.Add(v);
                }
            }
        }

        var result = new List<Poly>(polys.Count);
        var extra = new List<(double T, Vec3 P)>();
        var visited = new HashSet<(long, long, long)>();
        foreach (Poly p in polys)
        {
            var verts = new List<Vec3>(p.Verts.Length);
            for (int i = 0; i < p.Verts.Length; i++)
            {
                Vec3 a = p.Verts[i], b = p.Verts[(i + 1) % p.Verts.Length];
                verts.Add(a);
                Vec3 d = b - a;
                double len2 = d.LengthSquared;
                if (len2 < 1e-6)
                {
                    continue;
                }

                extra.Clear();
                visited.Clear();
                int steps = (int)Math.Ceiling(Math.Sqrt(len2) / cellSize) + 1;
                for (int s = 0; s <= steps; s++)
                {
                    var (cx, cy, cz) = Cell(a + d * ((double)s / steps), cellSize);
                    for (long x = cx - 1; x <= cx + 1; x++)
                    {
                        for (long y = cy - 1; y <= cy + 1; y++)
                        {
                            for (long z = cz - 1; z <= cz + 1; z++)
                            {
                                if (!visited.Add((x, y, z)) || !grid.TryGetValue((x, y, z), out List<Vec3>? list))
                                {
                                    continue;
                                }

                                foreach (Vec3 c in list)
                                {
                                    double t = Vec3.Dot(c - a, d) / len2;
                                    if (t <= 1e-4 || t >= 1 - 1e-4)
                                    {
                                        continue;
                                    }

                                    Vec3 onLine = a + d * t;
                                    if ((onLine - c).LengthSquared < 0.01 * 0.01 && (c - a).LengthSquared > 1e-4 && (c - b).LengthSquared > 1e-4)
                                    {
                                        extra.Add((t, c));
                                    }
                                }
                            }
                        }
                    }
                }

                if (extra.Count > 0)
                {
                    extra.Sort((u, w) => u.T.CompareTo(w.T));
                    foreach ((double _, Vec3 c) in extra)
                    {
                        if ((c - verts[^1]).LengthSquared > 1e-6)
                        {
                            verts.Add(c);
                        }
                    }
                }
            }

            result.Add(verts.Count == p.Verts.Length ? p : p.WithVerts(verts.ToArray()));
        }

        return result;
    }

    /// <summary>Triangles of a convex polygon as vertex index triples into <see cref="Poly.Verts"/> plus, when the
    /// polygon has vertices in the middle of its edges, one extra centre point (index Verts.Length), so no triangle is
    /// degenerate.</summary>
    public static (List<(int, int, int)> Tris, Vec3? Centre) Triangulate(Poly p)
    {
        var tris = new List<(int, int, int)>();
        int n = p.Verts.Length;
        bool collinear = false;
        for (int i = 0; i < n && !collinear; i++)
        {
            Vec3 prev = p.Verts[(i + n - 1) % n], v = p.Verts[i], next = p.Verts[(i + 1) % n];
            collinear = Vec3.Cross(prev - v, next - v).Length < 1e-3 * Math.Max(1, (prev - v).Length * (next - v).Length);
        }

        if (!collinear)
        {
            for (int i = 1; i < n - 1; i++)
            {
                tris.Add((0, i, i + 1));
            }

            return (tris, null);
        }

        for (int i = 0; i < n; i++)
        {
            tris.Add((n, i, (i + 1) % n));
        }

        return (tris, p.Centroid());
    }

    private static (long, long, long) Quant(Vec3 v) => ((long)Math.Round(v.X * 512), (long)Math.Round(v.Y * 512), (long)Math.Round(v.Z * 512));

    private static (long, long, long) Cell(Vec3 v, double size) =>
        ((long)Math.Floor(v.X / size), (long)Math.Floor(v.Y / size), (long)Math.Floor(v.Z / size));
}
