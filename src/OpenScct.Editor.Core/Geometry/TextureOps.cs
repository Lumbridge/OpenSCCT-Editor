using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>Builds faces with the stock default mapping.</summary>
public static class FaceKit
{
    /// <summary>A face with <paramref name="material"/> (or the default) and box mapping (UE2 default alignment).</summary>
    public static BrushFace Make(string? material, IEnumerable<Vec3> vertices)
    {
        var f = new BrushFace { Material = material ?? Starter.DefaultMaterial, Vertices = vertices.ToList() };
        TextureAlign.Planar(f);
        return f;
    }

    public static List<BrushFace> Make(string? material, IEnumerable<IEnumerable<Vec3>> polygons, int flags = 0) =>
        polygons.Select(p =>
        {
            BrushFace f = Make(material, p);
            f.Flags = flags;
            return f;
        }).ToList();
}

/// <summary>A copied surface (UE2 copy/paste surface): material, flags and mapping.</summary>
public sealed record SurfaceStyle(string? Material, int Flags, Vec3 TextureBase, Vec3 TextureU, Vec3 TextureV, int PanU, int PanV,
    double LightMapScale)
{
    public static SurfaceStyle Of(BrushFace f) => new(f.Material, f.Flags, f.TextureBase, f.TextureU, f.TextureV, f.PanU, f.PanV, f.LightMapScale);
}

/// <summary>
/// Texture mapping tools (UE2 surface properties: alignment, pan, rotate, scale, fit, copy/paste). A face maps a
/// point to texels by <c>u = dot(p - TextureBase, TextureU) + PanU</c>, so the length of <c>TextureU</c> is texels per
/// unit: a scale of 2 (texture twice as big) is a vector of length 0.5. World-aligned modes work in world space
/// through the brush transform, so rotated or scaled brushes line up with their neighbours. Each method returns a
/// new face.
/// </summary>
public static class TextureOps
{
    /// <summary>Below this cosine between U and V the axes count as square to each other.</summary>
    private const double SquareAxes = 1e-6;

    /// <summary>
    /// The texture origin with the pans folded in (UE2's FPoly at this version has no pans), so
    /// <c>dot(p - result, U) = dot(p - base, U) + panU</c> and the same for V. UE2 folds each pan along its own axis
    /// (<c>base -= U panU / |U|^2</c>), which is exact only while U and V are square to each other; axes that are not
    /// (a texture kept through a non-uniform scale of a turned brush) are folded exactly. Square axes keep UE2's sum.
    /// </summary>
    public static Vec3 FoldPans(Vec3 textureBase, Vec3 u, Vec3 v, int panU, int panV)
    {
        if (panU == 0 && panV == 0)
        {
            return textureBase;
        }

        double uu = u.LengthSquared, vv = v.LengthSquared, uv = Vec3.Dot(u, v), det = uu * vv - uv * uv;
        if (Math.Abs(uv) > SquareAxes * Math.Sqrt(uu * vv) && det > 1e-12 * uu * vv)
        {
            // Solve [uu uv; uv vv] (a, b) = (panU, panV) and move the origin back by a U + b V.
            double a = (panU * vv - panV * uv) / det, b = (panV * uu - panU * uv) / det;
            return textureBase - u * a - v * b;
        }

        Vec3 o = textureBase;
        if (panU != 0 && uu > 0)
        {
            o -= u * (panU / uu);
        }

        if (panV != 0 && vv > 0)
        {
            o -= v * (panV / vv);
        }

        return o;
    }

    /// <summary>True when U and V are far enough from square that UE2's own pan fold (T3D <c>Pan</c> lines) would
    /// place the texture wrongly: write the pans folded (<see cref="FoldPans"/>) instead.</summary>
    public static bool PansNeedExactFold(Vec3 u, Vec3 v, int panU, int panV) =>
        (panU != 0 || panV != 0) && Math.Abs(Vec3.Dot(u, v)) > SquareAxes * Math.Sqrt(u.LengthSquared * v.LengthSquared);

    /// <summary>Applies <paramref name="op"/> to the chosen faces of a brush (all when <paramref name="faces"/> is null).</summary>
    public static Brush Apply(Brush brush, IReadOnlyList<int>? faces, Func<BrushFace, ShapeTransform, BrushFace> op)
    {
        int count = brush.Shape.Faces.Count;
        if (faces?.FirstOrDefault(i => i >= count, -1) is int bad and >= 0)
        {
            throw new ArgumentException($"brush {brush.Id} has no face {bad} (it has {count})");
        }

        ShapeTransform t = ShapeTransform.For(brush);
        Brush o = brush.Clone();
        for (int i = 0; i < count; i++)
        {
            if (faces is null || faces.Contains(i))
            {
                o.Shape.Faces[i] = op(brush.Shape.Faces[i], t);
            }
        }

        return o;
    }

    // ---- alignment --------------------------------------------------------------------------------------------

    /// <summary>Box mapping along the face's dominant world axis (the stock default).</summary>
    public static BrushFace AlignDefault(BrushFace f, ShapeTransform t, double scale = 1)
    {
        Vec3 n = WorldNormal(f, t);
        double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
        (Vec3 u, Vec3 v) = az >= ax && az >= ay ? (Vec3.UnitX, -Vec3.UnitY)
            : ax >= ay ? (Vec3.UnitY, -Vec3.UnitZ)
            : (Vec3.UnitX, -Vec3.UnitZ);
        return SetWorld(f, t, u / scale, v / scale, Vec3.Zero);
    }

    /// <summary>Floor/ceiling mapping: projected straight down the world Z axis.</summary>
    public static BrushFace AlignFloor(BrushFace f, ShapeTransform t, double scale = 1) =>
        SetWorld(f, t, Vec3.UnitX / scale, -Vec3.UnitY / scale, Vec3.Zero);

    /// <summary>Wall mapping: U runs level along the face, V straight down, so textures stand upright and are not
    /// mirrored seen from the front. Near-horizontal faces fall back to floor mapping.</summary>
    public static BrushFace AlignWall(BrushFace f, ShapeTransform t, double scale = 1)
    {
        Vec3 n = WorldNormal(f, t);
        Vec3 u = Vec3.Cross(n, Vec3.UnitZ);
        if (u.Length < 1e-3)
        {
            return AlignFloor(f, t, scale);
        }

        return SetWorld(f, t, u.Normalized() / scale, -Vec3.UnitZ / scale, Vec3.Zero);
    }

    /// <summary>Face mapping: U along the face's first edge, V across it, origin at its first vertex.</summary>
    public static BrushFace AlignFace(BrushFace f, ShapeTransform t, double scale = 1)
    {
        List<Vec3> w = t.FaceToWorld(f);
        if (w.Count < 3)
        {
            return f.Clone();
        }

        Vec3 n = Poly.Normal(w);
        Vec3 u = (w[1] - w[0]).Normalized();
        Vec3 v = Vec3.Cross(n, u);
        return SetWorld(f, t, u / scale, v / scale, w[0]);
    }

    /// <summary>Wraps a texture round the chosen faces like a label on a can: each face gets wall mapping, and U runs
    /// on from one face to the next round the brush's centre (UE2 cylinder alignment).</summary>
    public static Brush AlignCylinder(Brush brush, IReadOnlyList<int>? faces, double scale = 1)
    {
        ShapeTransform t = ShapeTransform.For(brush);
        List<int> chosen = (faces ?? Enumerable.Range(0, brush.Shape.Faces.Count).ToList())
            .Where(i => i < brush.Shape.Faces.Count && Math.Abs(WorldNormal(brush.Shape.Faces[i], t).Z) < 0.7)
            .ToList();
        (Vec3 min, Vec3 max) = BrushOps.WorldBounds(brush);
        Vec3 centre = (min + max) / 2;
        // U = n x Z runs the way the angle about Z decreases, so walk the faces in that order.
        var ordered = chosen
            .Select(i => (Index: i, Angle: Angle(Poly.Average(t.FaceToWorld(brush.Shape.Faces[i])) - centre)))
            .OrderByDescending(x => x.Angle).ThenBy(x => x.Index)
            .ToList();
        Brush o = brush.Clone();
        double run = 0;
        foreach ((int i, double _) in ordered)
        {
            BrushFace f = brush.Shape.Faces[i];
            Vec3 n = WorldNormal(f, t);
            Vec3 u = Vec3.Cross(n, Vec3.UnitZ).Normalized();
            List<Vec3> w = t.FaceToWorld(f);
            double start = w.Min(p => Vec3.Dot(p, u));
            double end = w.Max(p => Vec3.Dot(p, u));
            Vec3 startPoint = u * start;
            // u(p) = dot(p - base, U) / scale; the face's start edge maps to the running length so far.
            Vec3 baseW = startPoint - u * run;
            o.Shape.Faces[i] = SetWorld(f, t, u / scale, -Vec3.UnitZ / scale, baseW);
            run += end - start;
        }

        return o;

        static double Angle(Vec3 d) => Math.Atan2(d.Y, d.X);
    }

    /// <summary>Stretches the texture so it covers the face exactly <paramref name="repeatU"/> x
    /// <paramref name="repeatV"/> times, keeping the current directions. Needs the texture's size in texels.</summary>
    public static BrushFace Fit(BrushFace f, ShapeTransform t, double textureWidth, double textureHeight, double repeatU = 1, double repeatV = 1)
    {
        if (textureWidth <= 0 || textureHeight <= 0 || repeatU <= 0 || repeatV <= 0)
        {
            throw new ArgumentException("fit: texture size and repeats must be positive");
        }

        List<Vec3> w = t.FaceToWorld(f);
        if (w.Count < 3)
        {
            return f.Clone();
        }

        Vec3 du = t.TextureAxisToWorld(f.TextureU).Normalized();
        Vec3 dv = t.TextureAxisToWorld(f.TextureV).Normalized();
        if (du == Vec3.Zero || dv == Vec3.Zero || Vec3.Cross(du, dv).Length < 1e-6)
        {
            BrushFace aligned = AlignFace(f, t);
            du = t.TextureAxisToWorld(aligned.TextureU).Normalized();
            dv = t.TextureAxisToWorld(aligned.TextureV).Normalized();
        }

        double minU = w.Min(p => Vec3.Dot(p, du)), maxU = w.Max(p => Vec3.Dot(p, du));
        double minV = w.Min(p => Vec3.Dot(p, dv)), maxV = w.Max(p => Vec3.Dot(p, dv));
        double sizeU = Math.Max(1e-6, maxU - minU), sizeV = Math.Max(1e-6, maxV - minV);

        // The base sits where dot(base, du) = minU and dot(base, dv) = minV (du and dv need not be perpendicular).
        double c = Vec3.Dot(du, dv);
        double det = 1 - c * c;
        double a = (minU - c * minV) / det, b = (minV - c * minU) / det;
        Vec3 baseW = du * a + dv * b;
        BrushFace o = SetWorld(f, t, du * (textureWidth * repeatU / sizeU), dv * (textureHeight * repeatV / sizeV), baseW);
        o.PanU = 0;
        o.PanV = 0;
        return o;
    }

    // ---- adjustments ------------------------------------------------------------------------------------------

    /// <summary>Pans by whole texels (UE2 pan).</summary>
    public static BrushFace Pan(BrushFace f, int du, int dv)
    {
        BrushFace o = f.Clone();
        o.PanU += du;
        o.PanV += dv;
        return o;
    }

    /// <summary>Turns the mapping about the face normal by <paramref name="degrees"/> (clockwise seen from the front).</summary>
    public static BrushFace Rotate(BrushFace f, ShapeTransform t, double degrees)
    {
        Vec3 n = WorldNormal(f, t);
        double a = -degrees * Math.PI / 180;
        Vec3 Turn(Vec3 v) => v * Math.Cos(a) + Vec3.Cross(n, v) * Math.Sin(a) + n * (Vec3.Dot(n, v) * (1 - Math.Cos(a)));
        Vec3 u = t.TextureAxisToWorld(f.TextureU), v = t.TextureAxisToWorld(f.TextureV);
        return SetWorld(f, t, Turn(u), Turn(v), t.ToWorld(f.TextureBase));
    }

    /// <summary>Multiplies the texture's size (2 = twice as big) along U and V.</summary>
    public static BrushFace Scale(BrushFace f, double scaleU, double scaleV)
    {
        if (scaleU == 0 || scaleV == 0)
        {
            throw new ArgumentException("scale must not be zero");
        }

        BrushFace o = f.Clone();
        o.TextureU = f.TextureU / scaleU;
        o.TextureV = f.TextureV / scaleV;
        return o;
    }

    /// <summary>Sets the texture's size absolutely (world units per texel) keeping the directions.</summary>
    public static BrushFace ScaleTo(BrushFace f, ShapeTransform t, double scaleU, double scaleV)
    {
        if (scaleU <= 0 || scaleV <= 0)
        {
            throw new ArgumentException("scale must be positive");
        }

        Vec3 u = t.TextureAxisToWorld(f.TextureU).Normalized(), v = t.TextureAxisToWorld(f.TextureV).Normalized();
        return SetWorld(f, t, u / scaleU, v / scaleV, t.ToWorld(f.TextureBase));
    }

    public static BrushFace FlipU(BrushFace f)
    {
        BrushFace o = f.Clone();
        o.TextureU = -f.TextureU;
        return o;
    }

    public static BrushFace FlipV(BrushFace f)
    {
        BrushFace o = f.Clone();
        o.TextureV = -f.TextureV;
        return o;
    }

    // ---- copy / paste -----------------------------------------------------------------------------------------

    /// <summary>
    /// Pastes a copied surface. By default only the material, flags and lightmap scale move over and the target
    /// keeps its own alignment (as RE+ does). With <paramref name="withMapping"/> the source's texture axes are
    /// projected onto the target's plane (world space), keeping their lengths, and its base and pan copied.
    /// </summary>
    public static BrushFace Paste(BrushFace target, ShapeTransform targetTransform, SurfaceStyle style, ShapeTransform sourceTransform,
        bool withMapping = false)
    {
        BrushFace o = target.Clone();
        o.Material = style.Material;
        o.Flags = style.Flags;
        o.LightMapScale = style.LightMapScale;
        if (!withMapping)
        {
            return o;
        }

        Vec3 n = WorldNormal(target, targetTransform);
        Vec3 Project(Vec3 axis)
        {
            Vec3 p = axis - n * Vec3.Dot(n, axis);
            return p.Length < 1e-9 ? axis : p.Normalized() * axis.Length;
        }

        Vec3 u = Project(sourceTransform.TextureAxisToWorld(style.TextureU));
        Vec3 v = Project(sourceTransform.TextureAxisToWorld(style.TextureV));
        o = SetWorld(o, targetTransform, u, v, sourceTransform.ToWorld(style.TextureBase));
        o.PanU = style.PanU;
        o.PanV = style.PanV;
        return o;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static Vec3 WorldNormal(BrushFace f, ShapeTransform t) => Poly.Normal(t.FaceToWorld(f));

    private static BrushFace SetWorld(BrushFace f, ShapeTransform t, Vec3 uWorld, Vec3 vWorld, Vec3 baseWorld)
    {
        BrushFace o = f.Clone();
        o.TextureU = Poly.Tidy6(t.TextureAxisToLocal(uWorld));
        o.TextureV = Poly.Tidy6(t.TextureAxisToLocal(vWorld));
        o.TextureBase = Poly.Tidy(t.ToLocal(baseWorld));
        return o;
    }
}
