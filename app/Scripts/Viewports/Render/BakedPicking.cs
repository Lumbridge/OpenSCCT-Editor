using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// Picks a surface of an imported map's baked BSP (the textured sections <see cref="BakedWorldNode"/> draws) under a
/// ray, for the surface tool: the nearest triangle seen from the front (either side when its section is two-sided),
/// within the storey shown, and the connected run of triangles in its plane and section around it. The baked world is
/// read-only; the pick only shows what is there.
/// </summary>
public static class BakedPicking
{
    /// <summary>Planes closer than this (metres) are one plane.</summary>
    private const float PlaneSlack = 0.005f;

    /// <summary>A surface bigger than this many triangles is cut short (its highlight shows only part of it).</summary>
    private const int MaxTriangles = 20000;

    /// <summary>The surface under the ray (UE units), or null. <paramref name="maxDistance"/> is in UE units.</summary>
    public static BakedSurfacePick? Pick(IReadOnlyList<MeshSection> sections, Vec3 origin, Vec3 direction, double maxDistance, Storey? storey)
    {
        Vector3 o = Space.ToGodot(origin), d = Space.DirToGodot(direction).Normalized();
        float best = (float)(maxDistance / Space.UnitsPerMetre);
        int hitSection = -1, hitTriangle = -1;
        for (int s = 0; s < sections.Count; s++)
        {
            MeshSection section = sections[s];
            float[] p = section.Positions;
            int[] idx = section.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector3 a = At(p, idx[t]), b = At(p, idx[t + 1]), c = At(p, idx[t + 2]);
                Vector3 n = (b - a).Cross(c - a);
                if (!section.DoubleSided && n.Dot(d) >= 0)
                {
                    continue;
                }

                if (Ray(o, d, a, b, c) is { } hit && hit < best && Inside(storey, o + d * hit))
                {
                    best = hit;
                    hitSection = s;
                    hitTriangle = t;
                }
            }
        }

        if (hitSection < 0)
        {
            return null;
        }

        MeshSection found = sections[hitSection];
        Vector3 a0 = At(found.Positions, found.Indices[hitTriangle]);
        Vector3 normal = (At(found.Positions, found.Indices[hitTriangle + 1]) - a0).Cross(At(found.Positions, found.Indices[hitTriangle + 2]) - a0).Normalized();
        if (normal.Dot(d) > 0)
        {
            normal = -normal;
        }

        List<Vec3> triangles = Surface(found, hitTriangle, normal, normal.Dot(a0));
        return new BakedSurfacePick(MaterialName(found.Material), Space.ToUe(o + d * best), Space.DirToUe(normal), triangles);
    }

    /// <summary>A section's material as the map names it: the exporter's variant suffixes after "__" (a lightmap
    /// page, "__lm1") cut off, as <see cref="BakedWorldNode"/> and the game match it.</summary>
    private static string? MaterialName(string? material) =>
        material is not null && material.IndexOf("__", StringComparison.Ordinal) is var cut and > 0 ? material[..cut] : material;

    /// <summary>The triangles of a section in the plane of triangle <paramref name="start"/> connected to it through
    /// shared corners, as UE points (three per triangle).</summary>
    private static List<Vec3> Surface(MeshSection s, int start, Vector3 normal, float distance)
    {
        float[] p = s.Positions;
        int[] idx = s.Indices;
        var inPlane = new List<int>();
        var byCorner = new Dictionary<(long, long, long), List<int>>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 a = At(p, idx[t]), b = At(p, idx[t + 1]), c = At(p, idx[t + 2]);
            Vector3 n = (b - a).Cross(c - a);
            if (n.LengthSquared() < 1e-12f || Math.Abs(Math.Abs(n.Normalized().Dot(normal)) - 1) > 1e-3f
                || Math.Abs(a.Dot(normal) - distance) > PlaneSlack || Math.Abs(b.Dot(normal) - distance) > PlaneSlack
                || Math.Abs(c.Dot(normal) - distance) > PlaneSlack)
            {
                continue;
            }

            inPlane.Add(t);
            foreach (Vector3 corner in new[] { a, b, c })
            {
                (long, long, long) key = Key(corner);
                if (!byCorner.TryGetValue(key, out List<int>? list))
                {
                    byCorner[key] = list = new List<int>();
                }

                list.Add(t);
            }
        }

        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        var result = new List<Vec3>();
        while (queue.Count > 0 && result.Count < MaxTriangles * 3)
        {
            int t = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                Vector3 corner = At(p, idx[t + k]);
                result.Add(Space.ToUe(corner));
                if (!byCorner.TryGetValue(Key(corner), out List<int>? next))
                {
                    continue;
                }

                foreach (int n in next)
                {
                    if (seen.Add(n))
                    {
                        queue.Enqueue(n);
                    }
                }
            }
        }

        return result;
    }

    private static bool Inside(Storey? storey, Vector3 godot)
    {
        if (storey is null)
        {
            return true;
        }

        double z = Space.ToUe(godot).Z;
        return z >= storey.Bottom - 1 && z <= storey.Top + 1;
    }

    private static Vector3 At(float[] p, int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);

    /// <summary>A corner rounded to a millimetre, so triangles that share it find each other.</summary>
    private static (long, long, long) Key(Vector3 v) =>
        ((long)Math.Round(v.X * 1000), (long)Math.Round(v.Y * 1000), (long)Math.Round(v.Z * 1000));

    /// <summary>Möller–Trumbore: the distance along the ray to the triangle (either side), or null.</summary>
    private static float? Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 h = d.Cross(e2);
        float det = e1.Dot(h);
        if (Math.Abs(det) < 1e-9f)
        {
            return null;
        }

        float inv = 1 / det;
        Vector3 s = o - a;
        float u = inv * s.Dot(h);
        if (u < 0 || u > 1)
        {
            return null;
        }

        Vector3 q = s.Cross(e1);
        float v = inv * d.Dot(q);
        if (v < 0 || u + v > 1)
        {
            return null;
        }

        float t = inv * e2.Dot(q);
        return t > 1e-4f ? t : null;
    }
}
