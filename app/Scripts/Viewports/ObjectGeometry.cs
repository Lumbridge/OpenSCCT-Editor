using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>World-space shapes of placed objects in UE units, shared by drawing, picking, framing and box select.</summary>
public static class ObjectGeometry
{
    /// <summary>Half size of an actor's drawn box. Collision cylinders are not scaled (UE2); static mesh
    /// placeholders follow the draw scale.</summary>
    public static Vec3 ActorHalfExtent(Actor a, ActorLook look) =>
        a.ShortClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase)
            ? look.HalfExtent.Scale(a.DrawScale3D) * Math.Abs(a.DrawScale)
            : look.HalfExtent;

    public static (Vec3 Min, Vec3 Max) ActorBounds(Actor a)
    {
        if (a.Shape is { Faces.Count: > 0 } shape)
        {
            return BoundsOf(shape.Faces.SelectMany(f => f.Vertices).Select(a.LocalToWorld));
        }

        if (Render.StaticMeshDrawing.WorldBounds(a) is { } mesh)
        {
            return mesh;
        }

        Vec3 h = ActorHalfExtent(a, ActorLook.For(a));
        return (a.Location - h, a.Location + h);
    }

    public static (Vec3 Min, Vec3 Max) BrushBounds(Brush b)
    {
        WorldBrush w = BrushGeometry.Of(b);
        return (w.Min, w.Max);
    }

    public static (Vec3 Min, Vec3 Max) GeBounds(Ge g) => BoundsOf(g.Points);

    /// <summary>World bounds of an object, or null when it does not exist.</summary>
    public static (Vec3 Min, Vec3 Max)? Bounds(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush when p.FindBrush(r.Id) is { } b => BrushBounds(b),
        ObjectKind.Actor when p.FindActor(r.Id) is { } a => ActorBounds(a),
        ObjectKind.Ge when p.FindGe(r.Id) is { Points.Count: > 0 } g => GeBounds(g),
        _ => null,
    };

    /// <summary>Bounds of several objects, or null when none exists.</summary>
    public static (Vec3 Min, Vec3 Max)? Bounds(MapProject p, IEnumerable<ObjectRef> refs)
    {
        (Vec3 Min, Vec3 Max)? total = null;
        foreach (ObjectRef r in refs)
        {
            if (Bounds(p, r) is { } b)
            {
                total = total is { } t ? (Vec3.Min(t.Min, b.Min), Vec3.Max(t.Max, b.Max)) : b;
            }
        }

        return total;
    }

    /// <summary>
    /// Where the bulk of the map is, for the first view of a map just opened: on each axis, from the 2nd percentile
    /// of the objects' low edges to the 98th of their high edges, plus a small margin. A mesh parked far away (imported
    /// maps have one at Z=262143) or one huge box does not shrink the map to a speck. <paramref name="leftOut"/>
    /// counts the objects lying far outside. Frame All still frames everything.
    /// </summary>
    public static (Vec3 Min, Vec3 Max)? MainBounds(MapProject p, IEnumerable<ObjectRef> refs, out int leftOut)
    {
        leftOut = 0;
        var boxes = refs.Select(r => Bounds(p, r)).OfType<(Vec3 Min, Vec3 Max)>().ToList();
        if (boxes.Count < 20)
        {
            return Bounds(p, refs);
        }

        var low = new double[3];
        var high = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            int a = axis;
            List<double> mins = boxes.Select(b => b.Min[a]).Order().ToList();
            List<double> maxes = boxes.Select(b => b.Max[a]).Order().ToList();
            double from = mins[mins.Count * 2 / 100], to = maxes[maxes.Count * 98 / 100];
            double margin = Math.Max((to - from) * 0.05, 64);
            low[axis] = from - margin;
            high[axis] = to + margin;
        }

        // Far outside: the centre is more than half the box's size beyond it (edge pieces cut by the percentiles are not).
        bool Far(Vec3 c) => Enumerable.Range(0, 3).Any(axis =>
            Math.Max(low[axis] - c[axis], c[axis] - high[axis]) > (high[axis] - low[axis]) / 2);
        leftOut = boxes.Count(b => Far((b.Min + b.Max) / 2));
        return (new Vec3(low[0], low[1], low[2]), new Vec3(high[0], high[1], high[2]));
    }

    /// <summary>The point an object transforms around: an actor's or brush's location, a GE's first point.</summary>
    public static Vec3? Pivot(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush => p.FindBrush(r.Id)?.Location,
        ObjectKind.Actor => p.FindActor(r.Id)?.Location,
        ObjectKind.Ge when p.FindGe(r.Id) is { Points.Count: > 0 } g => g.Points[0],
        _ => null,
    };

    /// <summary>The 8 corners of a box (index bits x, y, z).</summary>
    public static Vec3[] Corners(Vec3 min, Vec3 max)
    {
        var c = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            c[i] = new Vec3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        }

        return c;
    }

    public static (Vec3 Min, Vec3 Max) BoundsOf(IEnumerable<Vec3> points)
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        bool any = false;
        foreach (Vec3 v in points)
        {
            min = Vec3.Min(min, v);
            max = Vec3.Max(max, v);
            any = true;
        }

        return any ? (min, max) : (Vec3.Zero, Vec3.Zero);
    }
}
