using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// Boolean operations on convex shapes, by plane splits (<see cref="BrushOps.SplitShape"/>): the part of one convex
/// solid inside another, and one minus another as convex pieces (at most one per plane of the subtracted solid).
/// Shapes are in whatever space both share (the builder brush CSG uses world space). Cut faces keep their material
/// and mapping; new caps take the material, flags and lightmap scale of the face that cut them.
/// </summary>
public static class ShapeCsg
{
    /// <summary>Pieces smaller than this (cubic units) are dropped as slivers.</summary>
    public const double MinVolume = 1;

    /// <summary>How far (units) a vertex may stand outside a face plane for a shape to still count as convex.</summary>
    public const double ConvexTolerance = 0.2;

    /// <summary>The shape of a brush in world space (faces wound outwards, texture axes in world space), without
    /// degenerate faces.</summary>
    public static BrushShape World(Brush b) => new() { Faces = b.WorldFaces().Where(f => f.Vertices.Count >= 3).ToList() };

    /// <summary>True when the shape is a closed convex solid (what these operations need).</summary>
    public static bool IsConvex(BrushShape s) => s.Faces.Count >= 4 && ShapeAnalysis.IsConvexSolid(s, ConvexTolerance);

    /// <summary>The part of <paramref name="a"/> inside <paramref name="b"/>, or null when they do not overlap.</summary>
    public static BrushShape? Intersect(BrushShape a, BrushShape b)
    {
        BrushShape? rest = a;
        foreach (BrushFace f in b.Faces)
        {
            if (rest is null)
            {
                break;
            }

            if (Plane3.FromPolygon(f.Vertices) is { } plane)
            {
                rest = Keep(CapStyle(BrushOps.SplitShape(rest, plane, f.Material).Back, plane, f, rest));
            }
        }

        return rest;
    }

    /// <summary><paramref name="a"/> minus <paramref name="b"/> as convex pieces (<paramref name="a"/> itself when they
    /// do not overlap).</summary>
    public static List<BrushShape> Subtract(BrushShape a, BrushShape b)
    {
        var pieces = new List<BrushShape>();
        BrushShape? rest = a;
        foreach (BrushFace f in b.Faces)
        {
            if (rest is null)
            {
                break;
            }

            if (Plane3.FromPolygon(f.Vertices) is not { } plane)
            {
                continue;
            }

            (BrushShape? front, BrushShape? back) = BrushOps.SplitShape(rest, plane, f.Material);
            if (Keep(CapStyle(front, plane.Flipped, f, rest)) is { } piece)
            {
                pieces.Add(piece);
            }

            rest = Keep(CapStyle(back, plane, f, rest));
        }

        // Whatever is left lies inside b and is removed.
        return pieces;
    }

    /// <summary>True when the bounding boxes of the two shapes touch.</summary>
    public static bool Overlaps(BrushShape a, BrushShape b)
    {
        (Vec3 amin, Vec3 amax) = a.Bounds();
        (Vec3 bmin, Vec3 bmax) = b.Bounds();
        return Overlaps(amin, amax, bmin, bmax);
    }

    /// <summary>True when two boxes touch.</summary>
    public static bool Overlaps(Vec3 amin, Vec3 amax, Vec3 bmin, Vec3 bmax) =>
        amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;

    /// <summary>Gives the cap a split of <paramref name="before"/> made on <paramref name="plane"/> (the new face lying
    /// on it, facing the same way) the flags and lightmap scale of the face that cut it; the split already gave it
    /// that face's material.</summary>
    private static BrushShape? CapStyle(BrushShape? s, Plane3 plane, BrushFace cutter, BrushShape before)
    {
        bool OnPlane(BrushFace f) => Plane3.FromPolygon(f.Vertices) is { } p && p.NearlyEquals(plane, 1e-4, Poly.Epsilon);
        if (s is null || before.Faces.Any(OnPlane))
        {
            return s;
        }

        foreach (BrushFace f in s.Faces.Where(OnPlane))
        {
            f.Flags = cutter.Flags;
            f.LightMapScale = cutter.LightMapScale;
        }

        return s;
    }

    private static BrushShape? Keep(BrushShape? s) => s is { Faces.Count: >= 4 } && ShapeAnalysis.Volume(s) >= MinVolume ? s : null;
}
