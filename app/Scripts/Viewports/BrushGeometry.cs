using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>One face of a brush in world space: its vertices, outward Newell normal, material and flags.</summary>
public sealed record WorldFace(Vec3[] Vertices, Vec3 Normal, string? Material, int Flags);

/// <summary>A brush's world faces and bounds, computed once per brush state.</summary>
public sealed record WorldBrush(IReadOnlyList<WorldFace> Faces, Vec3 Min, Vec3 Max)
{
    public bool IsEmpty => Faces.Count == 0;
}

/// <summary>
/// World-space brush geometry for drawing, picking, framing and box select. Results are cached per brush instance and
/// checked against a hash of the brush's transform and shape, so hovering over a big map does not rebuild thousands
/// of faces on every mouse move, and an edit (which replaces or changes the brush) is never missed.
/// </summary>
public static class BrushGeometry
{
    private static readonly ConditionalWeakTable<Brush, Cached> s_cache = new();

    public static WorldBrush Of(Brush b)
    {
        int key = HashCode.Combine(ShapeKey(b), b.Location);
        if (s_cache.TryGetValue(b, out Cached? hit) && hit.Key == key)
        {
            return hit.Value;
        }

        var faces = new List<WorldFace>(b.Shape.Faces.Count);
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (BrushFace f in b.WorldFaces())
        {
            faces.Add(new WorldFace([.. f.Vertices], f.Normal(), f.Material, f.Flags));
            foreach (Vec3 v in f.Vertices)
            {
                min = Vec3.Min(min, v);
                max = Vec3.Max(max, v);
            }
        }

        WorldBrush value = faces.Count == 0 ? new WorldBrush(faces, b.Location, b.Location) : new WorldBrush(faces, min, max);
        s_cache.AddOrUpdate(b, new Cached(key, value));
        return value;
    }

    /// <summary>A hash of everything that shapes a brush except its location (a move only shifts the drawing).</summary>
    public static int ShapeKey(Brush b)
    {
        var h = new HashCode();
        h.Add(b.Oper);
        h.Add(b.PolyFlags);
        h.Add(b.Rotation);
        h.Add(b.PrePivot);
        h.Add(b.MainScale);
        h.Add(b.PostScale);
        AddShape(ref h, b.Shape);
        return h.ToHashCode();
    }

    public static void AddShape(ref HashCode h, BrushShape s)
    {
        foreach (BrushFace f in s.Faces)
        {
            h.Add(f.Material);
            h.Add(f.Flags);
            foreach (Vec3 v in f.Vertices)
            {
                h.Add(v);
            }
        }
    }

    private sealed record Cached(int Key, WorldBrush Value);
}
