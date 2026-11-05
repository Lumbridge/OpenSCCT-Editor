using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Svmap;

/// <summary>
/// Rebuilds the editable shape of a volume (BlockingVolume, damage volumes, zone volumes...) from its brush collider in
/// <c>sim/collision.bin</c>: the triangles are grouped by plane and each group becomes one convex face when the group
/// is convex (always so for convex brushes), else one face per triangle. Faces end in the actor's local space.
/// </summary>
public static class BrushRecovery
{
    private const double PlaneTolerance = 0.05;
    private const double WeldTolerance = 0.01;

    public static BrushShape? FromCollider(CollisionData data, int colliderIndex, Actor owner)
    {
        Collider c = data.Colliders[colliderIndex];
        if (c.TriCount == 0)
        {
            return null;
        }

        // The collider's own transform, so the shape lands in the owner's local space even when a converter baked it.
        var colliderSpace = new Actor("collider", owner.Class)
        {
            Location = V(c.Location),
            Rotation = new Rot(c.Rotation.Pitch, c.Rotation.Yaw, c.Rotation.Roll),
            DrawScale3D = V(c.Scale),
            PrePivot = V(c.PrePivot),
        };
        Vec3 ToOwner(FVector p) => owner.WorldToLocal(colliderSpace.LocalToWorld(V(p)));

        var groups = new List<PlaneGroup>();
        foreach (CollisionTriangle t in data.TrianglesOf(c))
        {
            Vec3 a = ToOwner(data.Vertices[(int)t.I0]), b = ToOwner(data.Vertices[(int)t.I1]), d = ToOwner(data.Vertices[(int)t.I2]);
            Vec3 cross = Vec3.Cross(b - a, d - a);
            if (cross.Length < 1e-6)
            {
                continue;
            }

            Vec3 n = cross.Normalized();
            double w = Vec3.Dot(n, a);
            PlaneGroup? g = groups.Find(x => Vec3.Dot(x.Normal, n) > 0.9999 && Math.Abs(x.W - w) < PlaneTolerance);
            if (g is null)
            {
                g = new PlaneGroup(n, w);
                groups.Add(g);
            }

            g.Triangles.Add((a, b, d));
        }

        var shape = new BrushShape();
        foreach (PlaneGroup g in groups)
        {
            List<Vec3> hull = PlanarHull(g);
            double hullArea = Area(hull, g.Normal);
            double triArea = g.Triangles.Sum(t => Vec3.Cross(t.B - t.A, t.C - t.A).Length / 2);
            if (hull.Count >= 3 && Math.Abs(hullArea - triArea) <= Math.Max(1e-3 * triArea, 0.01))
            {
                shape.Faces.Add(Face(hull));
            }
            else
            {
                shape.Faces.AddRange(g.Triangles.Select(t => Face([t.A, t.B, t.C])));
            }
        }

        return shape.Faces.Count > 0 ? shape : null;
    }

    private static BrushFace Face(List<Vec3> vertices)
    {
        var f = new BrushFace { Vertices = vertices };
        TextureAlign.Planar(f);
        return f;
    }

    /// <summary>The convex hull of a plane group's corners, wound so its Newell normal is the plane's normal.</summary>
    private static List<Vec3> PlanarHull(PlaneGroup g)
    {
        Vec3 n = g.Normal;
        Vec3 u = Vec3.Cross(Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX, n).Normalized();
        Vec3 v = Vec3.Cross(n, u);
        var points = new List<Vec3>();
        foreach ((Vec3 a, Vec3 b, Vec3 c) in g.Triangles)
        {
            foreach (Vec3 p in new[] { a, b, c })
            {
                if (!points.Any(q => (q - p).LengthSquared < WeldTolerance * WeldTolerance))
                {
                    points.Add(p);
                }
            }
        }

        // Andrew's monotone chain in (u, v): counter-clockwise there is right-handed about n = u x v.
        List<(double X, double Y, Vec3 P)> pts = points.Select(p => (Vec3.Dot(p, u), Vec3.Dot(p, v), p))
            .OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
        if (pts.Count < 3)
        {
            return points;
        }

        var hull = new List<(double X, double Y, Vec3 P)>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach ((double X, double Y, Vec3 P) p in pass == 0 ? pts : Enumerable.Reverse(pts))
            {
                while (hull.Count >= start + 2 && Turn(hull[^2], hull[^1], p) <= 1e-6)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull.Select(h => h.P).ToList();
    }

    private static double Turn((double X, double Y, Vec3 P) a, (double X, double Y, Vec3 P) b, (double X, double Y, Vec3 P) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static double Area(List<Vec3> polygon, Vec3 n)
    {
        Vec3 sum = Vec3.Zero;
        for (int i = 0; i < polygon.Count; i++)
        {
            sum += Vec3.Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
        }

        return Math.Abs(Vec3.Dot(sum, n)) / 2;
    }

    private static Vec3 V(FVector v) => new(v.X, v.Y, v.Z);

    private sealed class PlaneGroup(Vec3 normal, double w)
    {
        public Vec3 Normal { get; } = normal;

        public double W { get; } = w;

        public List<(Vec3 A, Vec3 B, Vec3 C)> Triangles { get; } = new();
    }
}
