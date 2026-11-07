using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>Snap faces of brushes, shaped actors and boxes, and the moving shape of a selection.</summary>
public static class SnapFaces
{
    /// <summary>A single moving brush with more faces than this sticks by its box and the axis faces on its box's edges.</summary>
    public const int MaxMovingFaces = 64;

    /// <summary>A brush's world faces (degenerate ones, which recovered brushes can have, are skipped).</summary>
    public static IReadOnlyList<SnapFace> OfBrush(Brush b) =>
        OfPolygons(ObjectRef.ForBrush(b), SnapSource.Brush, b.WorldFaces().Select(f => (IReadOnlyList<Vec3>)f.Vertices));

    /// <summary>Faces from world polygons, numbered in order (degenerate polygons keep their number but give no face).</summary>
    public static IReadOnlyList<SnapFace> OfPolygons(ObjectRef? owner, SnapSource source, IEnumerable<IReadOnlyList<Vec3>> polygons, bool approximate = false)
    {
        var faces = new List<SnapFace>();
        int i = 0;
        foreach (IReadOnlyList<Vec3> poly in polygons)
        {
            if (SnapFace.Of(owner, i, source, poly, approximate) is { } f)
            {
                faces.Add(f);
            }

            i++;
        }

        return faces;
    }

    /// <summary>A shaped actor's (volume, mover brush) world faces.</summary>
    public static IReadOnlyList<SnapFace> OfShape(Actor a) =>
        a.Shape is { } shape
            ? OfPolygons(ObjectRef.Actor(a.Id), SnapSource.Brush, shape.Faces.Select(f => (IReadOnlyList<Vec3>)f.Vertices.Select(a.LocalToWorld).ToArray()))
            : [];

    /// <summary>The six faces of an actor's box.</summary>
    public static IReadOnlyList<SnapFace> OfActor(Actor a, SnapBox box) => box.Box.Faces(ObjectRef.Actor(a.Id), box.Source, box.Approximate);

    /// <summary>The bounds of some faces.</summary>
    public static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<SnapFace> faces)
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (SnapFace f in faces)
        {
            min = Vec3.Min(min, f.Min);
            max = Vec3.Max(max, f.Max);
        }

        return (min, max);
    }

    /// <summary>
    /// The moving shape of one object's faces: all of them up to <see cref="MaxMovingFaces"/>, else its box's faces
    /// and the axis faces lying on the box's sides. Directions are the normals of its 8 largest faces. Null when it
    /// has no faces (a recovered brush whose faces are all degenerate).
    /// </summary>
    public static MovingShape? Shape(IReadOnlyList<SnapFace> faces, ObjectRef? owner)
    {
        if (faces.Count == 0)
        {
            return null;
        }

        (Vec3 min, Vec3 max) = Bounds(faces);

        IReadOnlyList<Vec3> directions = Directions(faces);
        if (faces.Count <= MaxMovingFaces)
        {
            return new MovingShape(min, max, faces, directions);
        }

        var kept = new List<SnapFace>(OrientedBox.Aligned(min, max).Faces(owner, SnapSource.Brush, false));
        kept.AddRange(faces.Where(f => f.IsAxis && (Math.Abs(f.Coord - min[f.Axis]) < 1e-6 || Math.Abs(f.Coord - max[f.Axis]) < 1e-6)));
        return new MovingShape(min, max, kept, directions);
    }

    /// <summary>The normals of the largest faces, opposite directions counted once.</summary>
    public static IReadOnlyList<Vec3> Directions(IEnumerable<SnapFace> faces, int count = 8)
    {
        var dirs = new List<Vec3>();
        foreach (SnapFace f in faces.OrderByDescending(f => f.Area))
        {
            if (dirs.Count >= count)
            {
                break;
            }

            if (!dirs.Any(d => Math.Abs(Vec3.Dot(d, f.Normal)) >= SnapFace.ParallelCos))
            {
                dirs.Add(f.Normal);
            }
        }

        return dirs;
    }
}
