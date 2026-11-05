using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>
/// Pure brush editing operations for the app, the CLI and scripts. Each returns new objects and leaves its inputs
/// untouched. Shapes are in local space; operations that take world-space input (planes, grids) use the brush's
/// transform. An operation that changes the polygons drops the shape's builder spec (it no longer describes them).
/// </summary>
public static class BrushOps
{
    // ---- bounds ----------------------------------------------------------------------------------------------

    /// <summary>The world-space bounds of a brush.</summary>
    public static (Vec3 Min, Vec3 Max) WorldBounds(Brush b) => BoundsOf(b.Shape.Faces.SelectMany(f => f.Vertices).Select(b.LocalToWorld));

    /// <summary>The world-space bounds of several brushes (zero box when there are none).</summary>
    public static (Vec3 Min, Vec3 Max) WorldBounds(IEnumerable<Brush> brushes) =>
        BoundsOf(brushes.SelectMany(b => b.Shape.Faces.SelectMany(f => f.Vertices).Select(b.LocalToWorld)));

    public static (Vec3 Min, Vec3 Max) BoundsOf(IEnumerable<Vec3> points)
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        bool any = false;
        foreach (Vec3 p in points)
        {
            min = Vec3.Min(min, p);
            max = Vec3.Max(max, p);
            any = true;
        }

        return any ? (min, max) : (Vec3.Zero, Vec3.Zero);
    }

    // ---- winding and mirroring --------------------------------------------------------------------------------

    /// <summary>Reverses every face (turns the shape inside out, or fixes one built inside out).</summary>
    public static BrushShape Flip(BrushShape shape) => Map(shape, keepBuilder: false, f =>
    {
        BrushFace c = f.Clone();
        c.Vertices.Reverse();
        return c;
    });

    /// <summary>Mirrors the shape across a local axis plane through the origin (0 = X, 1 = Y, 2 = Z), keeping the
    /// faces wound outwards.</summary>
    public static BrushShape Mirror(BrushShape shape, int axis)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)axis, 2u);
        Vec3 Flip(Vec3 p) => p.With(axis, -p[axis]);
        return Map(shape, keepBuilder: false, f =>
        {
            BrushFace c = f.Clone();
            c.Vertices = f.Vertices.Select(Flip).Reverse().ToList();
            c.TextureBase = Flip(f.TextureBase);
            c.TextureU = Flip(f.TextureU);
            c.TextureV = Flip(f.TextureV);
            return c;
        });
    }

    /// <summary>Moves every vertex (and texture origin) by <paramref name="delta"/> in local space. A real move drops
    /// the builder spec: the piece would regenerate back at its old place.</summary>
    public static BrushShape Translate(BrushShape shape, Vec3 delta) => Map(shape, keepBuilder: delta == Vec3.Zero, f =>
    {
        BrushFace c = f.Clone();
        c.Vertices = f.Vertices.Select(p => p + delta).ToList();
        c.TextureBase = f.TextureBase + delta;
        return c;
    });

    // ---- split and clip ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cuts a brush by a world-space plane into the part in front of it and the part behind it (either null when the
    /// brush is wholly on one side). Cut faces keep their material and mapping; each part gets a cap on the plane with
    /// <paramref name="capMaterial"/> (or the material of the first cut face). Caps are exact for convex brushes.
    /// </summary>
    public static (Brush? Front, Brush? Back) Split(Brush brush, Plane3 worldPlane, string? capMaterial = null)
    {
        ShapeTransform t = ShapeTransform.For(brush);
        Plane3 local = ToLocalPlane(t, worldPlane);
        (BrushShape? front, BrushShape? back) = SplitShape(brush.Shape, local, capMaterial);
        return (front is null ? null : WithShape(brush, front), back is null ? null : WithShape(brush, back));
    }

    /// <summary>Keeps the part of a brush behind a world-space plane (UE2 brush clip); null when nothing is left.</summary>
    public static Brush? Clip(Brush brush, Plane3 worldPlane, string? capMaterial = null) => Split(brush, worldPlane, capMaterial).Back;

    /// <summary>Splits a shape by a local-space plane; see <see cref="Split(Brush, Plane3, string?)"/>.</summary>
    public static (BrushShape? Front, BrushShape? Back) SplitShape(BrushShape shape, Plane3 plane, string? capMaterial = null)
    {
        var front = new List<BrushFace>();
        var back = new List<BrushFace>();
        var cut = new List<Vec3>();
        string? firstCutMaterial = null;
        foreach (BrushFace f in shape.Faces)
        {
            PolySplit s = Poly.Split(f.Vertices, plane);
            switch (s.Side)
            {
                case PlaneSide.Front:
                    front.Add(f.Clone());
                    cut.AddRange(f.Vertices.Where(p => Math.Abs(plane.Distance(p)) <= Poly.Epsilon));
                    break;
                case PlaneSide.Back:
                    back.Add(f.Clone());
                    cut.AddRange(f.Vertices.Where(p => Math.Abs(plane.Distance(p)) <= Poly.Epsilon));
                    break;
                case PlaneSide.Coplanar:
                    // A face on the cut plane belongs to the side it faces away from.
                    (Vec3.Dot(f.Normal(), plane.Normal) > 0 ? back : front).Add(f.Clone());
                    cut.AddRange(f.Vertices);
                    break;
                default:
                    firstCutMaterial ??= f.Material;
                    if (s.Front is { } fv)
                    {
                        front.Add(WithVertices(f, fv));
                        cut.AddRange(fv.Where(p => Math.Abs(plane.Distance(p)) <= Poly.Epsilon));
                    }

                    if (s.Back is { } bv)
                    {
                        back.Add(WithVertices(f, bv));
                    }

                    break;
            }
        }

        if (back.Count == 0 || front.Count == 0)
        {
            return (front.Count > 0 ? new BrushShape { Faces = front } : null, back.Count > 0 ? new BrushShape { Faces = back } : null);
        }

        string? material = capMaterial ?? firstCutMaterial ?? shape.Faces.FirstOrDefault()?.Material;
        bool backHasCap = back.Any(f => Plane3.FromPolygon(f.Vertices) is { } p && p.NearlyEquals(plane, 1e-4, Poly.Epsilon));
        bool frontHasCap = front.Any(f => Plane3.FromPolygon(f.Vertices) is { } p && p.NearlyEquals(plane.Flipped, 1e-4, Poly.Epsilon));
        if (Poly.Hull2D(cut.Select(plane.Project), plane.Normal) is { } cap && Poly.Area(cap) > ShapeAnalysis.ZeroArea)
        {
            if (!backHasCap)
            {
                back.Add(FaceKit.Make(material, cap));
            }

            if (!frontHasCap)
            {
                front.Add(FaceKit.Make(material, Enumerable.Reverse(cap).ToList()));
            }
        }

        return (new BrushShape { Faces = front }, new BrushShape { Faces = back });
    }

    private static Plane3 ToLocalPlane(ShapeTransform t, Plane3 world)
    {
        // Three points of the world plane taken back to local space define the local plane.
        (Vec3 u, Vec3 v) = Poly.Basis(world.Normal);
        Vec3 o = world.Normal * world.D;
        Vec3 a = t.ToLocal(o), b = t.ToLocal(o + u * 256), c = t.ToLocal(o + v * 256);
        Plane3 local = Plane3.FromPoints(a, b, c) ?? throw new ArgumentException("the brush has a zero scale");
        // Keep the normal on the same side as the world plane's front.
        Vec3 frontPoint = t.ToLocal(o + world.Normal * 256);
        return local.Distance(frontPoint) >= 0 ? local : local.Flipped;
    }

    // ---- faces ------------------------------------------------------------------------------------------------

    /// <summary>Merges neighbouring coplanar faces that share an edge, a material and flags, when the merged
    /// polygon stays convex. Texture mapping of the first face of each pair is kept.</summary>
    public static BrushShape MergeCoplanarFaces(BrushShape shape)
    {
        List<BrushFace> faces = shape.Faces.Select(f => f.Clone()).ToList();
        bool merged = true;
        bool any = false;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < faces.Count && !merged; i++)
            {
                for (int j = i + 1; j < faces.Count && !merged; j++)
                {
                    if (TryMerge(faces[i], faces[j]) is { } m)
                    {
                        faces[i] = m;
                        faces.RemoveAt(j);
                        merged = true;
                        any = true;
                    }
                }
            }
        }

        return any ? new BrushShape { Faces = faces } : shape.Clone();
    }

    private static BrushFace? TryMerge(BrushFace a, BrushFace b)
    {
        if (a.Material != b.Material || a.Flags != b.Flags || a.Vertices.Count < 3 || b.Vertices.Count < 3)
        {
            return null;
        }

        if (Plane3.FromPolygon(a.Vertices) is not { } pa || Plane3.FromPolygon(b.Vertices) is not { } pb || !pa.NearlyEquals(pb, 1e-4, Poly.Epsilon))
        {
            return null;
        }

        List<Vec3> va = a.Vertices, vb = b.Vertices;
        for (int i = 0; i < va.Count; i++)
        {
            Vec3 p0 = va[i], p1 = va[(i + 1) % va.Count];
            for (int j = 0; j < vb.Count; j++)
            {
                // b runs the shared edge the other way: p1 -> p0.
                if ((vb[j] - p1).Length > Poly.Epsilon || (vb[(j + 1) % vb.Count] - p0).Length > Poly.Epsilon)
                {
                    continue;
                }

                var loop = new List<Vec3>(va.Count + vb.Count - 2);
                for (int k = 0; k <= i; k++)
                {
                    loop.Add(va[k]);
                }

                // b's other vertices, starting after p0 (j + 2) and stopping before p1 (j).
                for (int k = 2; k < vb.Count; k++)
                {
                    loop.Add(vb[(j + k) % vb.Count]);
                }

                for (int k = i + 1; k < va.Count; k++)
                {
                    loop.Add(va[k]);
                }

                if (Poly.Clean(loop, 1e-3) is { } clean && Poly.IsConvex(clean, pa.Normal, 1e-6))
                {
                    return WithVertices(a, clean);
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>Splits every face of more than three vertices into a fan of triangles (same material and mapping).</summary>
    public static BrushShape Triangulate(BrushShape shape)
    {
        var faces = new List<BrushFace>();
        foreach (BrushFace f in shape.Faces)
        {
            if (f.Vertices.Count <= 3)
            {
                faces.Add(f.Clone());
                continue;
            }

            for (int i = 1; i + 1 < f.Vertices.Count; i++)
            {
                faces.Add(WithVertices(f, [f.Vertices[0], f.Vertices[i], f.Vertices[i + 1]]));
            }
        }

        return new BrushShape { Faces = faces };
    }

    /// <summary>The convex hull of the shape's vertices, keeping each face's material where a hull face lies on an
    /// original face plane (the default material elsewhere).</summary>
    public static BrushShape ConvexHull(BrushShape shape)
    {
        List<List<Vec3>> hull = ConvexSolid.Hull(shape.Faces.SelectMany(f => f.Vertices));
        var faces = new List<BrushFace>(hull.Count);
        foreach (List<Vec3> h in hull)
        {
            Plane3 hp = Plane3.FromPolygon(h)!.Value;
            BrushFace? source = shape.Faces.FirstOrDefault(f => Plane3.FromPolygon(f.Vertices) is { } p && p.NearlyEquals(hp, 1e-3, Poly.Epsilon));
            faces.Add(source is null ? FaceKit.Make(shape.Faces.FirstOrDefault()?.Material, h) : WithVertices(source, h));
        }

        return new BrushShape { Faces = faces };
    }

    // ---- snapping and baking ----------------------------------------------------------------------------------

    /// <summary>Snaps every vertex to the grid in world space (what the viewports show), then takes it back to local
    /// space: <see cref="Edit.VertexEdit.Snap"/> on all vertices, so faces that become degenerate are removed and
    /// faces that bend are split into triangles.</summary>
    public static Brush SnapVertices(Brush brush, double grid) =>
        grid <= 0 ? brush.Clone() : Edit.VertexEdit.Snap(brush, null, grid).Brush;

    /// <summary>Snaps local-space vertices to a grid (for shapes edited in their own space).</summary>
    public static BrushShape SnapLocal(BrushShape shape, double grid) => grid <= 0 ? shape.Clone() : Map(shape, keepBuilder: false, f =>
    {
        BrushFace c = f.Clone();
        c.Vertices = f.Vertices.Select(p => p.Snap(grid)).ToList();
        return c;
    });

    /// <summary>
    /// Bakes rotation, scales and pre-pivot into the vertices (UE2 "Transform permanently"): the brush keeps its
    /// Location, and its Rotation, MainScale, PostScale and PrePivot become identity. Textures stay where they were.
    /// </summary>
    public static Brush Bake(Brush brush)
    {
        ShapeTransform t = ShapeTransform.For(brush);
        bool identity = t.Linear.IsIdentity() && brush.PrePivot == Vec3.Zero;
        Brush o = brush.Clone();
        o.Rotation = Rot.Zero;
        o.MainScale = Vec3.One;
        o.PostScale = Vec3.One;
        o.PrePivot = Vec3.Zero;
        if (identity)
        {
            return o;
        }

        o.Shape = Map(brush.Shape, keepBuilder: false, f =>
        {
            BrushFace c = f.Clone();
            c.Vertices = t.FaceToWorld(f).Select(p => Poly.Tidy(p - brush.Location)).ToList();
            c.TextureBase = t.ToWorld(f.TextureBase) - brush.Location;
            c.TextureU = t.TextureAxisToWorld(f.TextureU);
            c.TextureV = t.TextureAxisToWorld(f.TextureV);
            return c;
        });
        return o;
    }

    /// <summary>Moves the brush's pivot to the centre of its bounds without moving the geometry.</summary>
    public static Brush Recenter(Brush brush)
    {
        Brush baked = Bake(brush);
        (Vec3 min, Vec3 max) = baked.Shape.Bounds();
        Vec3 centre = Poly.Tidy((min + max) / 2);
        baked.Shape = Translate(baked.Shape, -centre);
        baked.Location = brush.Location + centre;
        return baked;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static BrushShape Map(BrushShape shape, bool keepBuilder, Func<BrushFace, BrushFace> map) =>
        new() { Faces = shape.Faces.Select(map).ToList(), Builder = keepBuilder ? shape.Builder?.Clone() : null };

    private static BrushFace WithVertices(BrushFace f, List<Vec3> vertices)
    {
        BrushFace c = f.Clone();
        c.Vertices = vertices.Select(Poly.Tidy).ToList();
        return c;
    }

    private static Brush WithShape(Brush b, BrushShape shape)
    {
        Brush o = b.Clone();
        o.Shape = shape;
        return o;
    }
}
