using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>
/// Keeps a brush's textures the same size when its transform or shape changes (texture lock for scaling). A face
/// stores its mapping in the brush's own space and the brush transform carries it to the world
/// (<see cref="ShapeTransform"/>: <c>Base_w = ToWorld(Base)</c>, <c>U_w = Linear^-T U</c>), so on its own a new
/// MainScale or PostScale stretches the texture with the brush, as in UnrealEd. Here the mapping is rewritten so its
/// world form is what it was: <c>U' = Linear'^T U_w</c>, <c>V' = Linear'^T V_w</c>, <c>Base' = ToLocal'(Base_w)</c>,
/// pans unchanged. A face whose plane did not turn then gets the texels it had at every world point: texels keep
/// their size and direction, a face that did not move looks exactly as it did, and a stretched face shows more
/// repeats instead of bigger texels. A face that turns (a slope, or a wall of a rotated brush under PostScale) takes
/// its world mapping round by the smallest turn from its old plane to its new one, about its centre, so its texels
/// keep their size on the face too. A scale that changes sign is a reflection: the texture mirrors with the brush, as
/// the Mirror tool does. The compiler and the .sdc and T3D writers carry the mapping to the world the same way (UE2
/// FPoly::Transform), so the result holds after export.
/// </summary>
public static class TextureLock
{
    /// <summary>A scale component smaller than this squashes the brush flat: the transform has no inverse.</summary>
    private const double MinScale = 1e-9;

    /// <summary>Below this sine two face normals count as the same direction (the face did not turn).</summary>
    private const double MinTurn = 1e-9;

    /// <summary>
    /// Sets the mapping of every face of <paramref name="after"/> to the mapping of the same face of
    /// <paramref name="before"/>, carried from <paramref name="before"/>'s transform and shape to
    /// <paramref name="after"/>'s so its texels keep their size (see the class summary). Always works from
    /// <paramref name="before"/>'s faces, so a drag that recomputes every step from its start state never drifts.
    /// Vertices, materials and flags of <paramref name="after"/> are left alone. A brush whose face count changed
    /// keeps its own mapping; a transform with a zero scale (no world mapping to keep, or none to keep it with) gets
    /// <paramref name="before"/>'s local mapping as it is, and so does a face nothing happened to.
    /// </summary>
    public static void Keep(Brush before, Brush after)
    {
        List<BrushFace> from = before.Shape.Faces, to = after.Shape.Faces;
        if (from.Count != to.Count)
        {
            return;
        }

        bool usable = Invertible(before) && Invertible(after);
        ShapeTransform t0 = ShapeTransform.For(before);
        ShapeTransform t1 = usable
            ? ShapeTransform.For(after) with
            {
                // The reflection is left out here and applied by the brush itself, so the texture mirrors with it.
                Linear = Mat3.Diagonal(SignsOf(after.PostScale, before.PostScale)) * Mat3.Rotation(after.Rotation)
                    * Mat3.Diagonal(SignsOf(after.MainScale, before.MainScale)),
            }
            : t0;
        for (int i = 0; i < from.Count; i++)
        {
            BrushFace s = from[i], d = to[i];
            (Mat3 Turn, Vec3 About)? turn = usable ? Turn(t0, s, t1, d) : null;
            if (!usable || (turn is null && t0 == t1))
            {
                d.TextureU = s.TextureU;
                d.TextureV = s.TextureV;
                d.TextureBase = s.TextureBase;
            }
            else
            {
                Vec3 u = t0.TextureAxisToWorld(s.TextureU), v = t0.TextureAxisToWorld(s.TextureV), b = t0.ToWorld(s.TextureBase);
                if (turn is var (r, about))
                {
                    // The point the face turns about keeps its texel; the texture turns with the face around it.
                    u = r.Apply(u);
                    v = r.Apply(v);
                    b = about - r.Apply(about - b);
                }

                d.TextureU = Axis(t1.TextureAxisToLocal(u));
                d.TextureV = Axis(t1.TextureAxisToLocal(v));
                d.TextureBase = Point(t1.ToLocal(b));
            }

            d.PanU = s.PanU;
            d.PanV = s.PanV;
        }
    }

    /// <summary>Runs <paramref name="change"/> on <paramref name="brush"/> (a new scale) and, when
    /// <paramref name="keepTextures"/>, keeps its textures the size they were (<see cref="Keep"/>).</summary>
    public static void Rescale(Brush brush, Action<Brush> change, bool keepTextures = true)
    {
        if (!keepTextures)
        {
            change(brush);
            return;
        }

        Brush before = brush.Clone();
        change(brush);
        Keep(before, brush);
    }

    /// <summary>
    /// Carries the textures of <paramref name="before"/> onto <paramref name="after"/>, the same brush rebuilt from
    /// new builder parameters (a resized cube): when the builder and face count are the same, each face keeps its
    /// material (unless the builder's material parameter changed), flags, lightmap scale and texture size and
    /// alignment (<see cref="Keep"/>); otherwise the builder's
    /// fresh mapping is kept as made for the unscaled brush (<see cref="Unstretch"/>), so MainScale and PostScale do
    /// not stretch it.
    /// </summary>
    public static void Reshaped(Brush before, Brush after)
    {
        List<BrushFace> from = before.Shape.Faces, to = after.Shape.Faces;
        if (after.Shape.Builder is not { } built)
        {
            return;
        }

        if (from.Count != to.Count || before.Shape.Builder?.Kind is not { } kind || !string.Equals(kind, built.Kind, StringComparison.OrdinalIgnoreCase))
        {
            Unstretch(after);
            return;
        }

        // A new material parameter is the builder's to apply; otherwise each face keeps the one it had.
        Dictionary<string, string> oldStrings = before.Shape.Builder!.Strings;
        bool keepMaterials = oldStrings.Count == built.Strings.Count
            && oldStrings.All(kv => built.Strings.TryGetValue(kv.Key, out string? v) && v == kv.Value);
        for (int i = 0; i < from.Count; i++)
        {
            if (keepMaterials)
            {
                to[i].Material = from[i].Material;
            }

            to[i].Flags = from[i].Flags;
            to[i].LightMapScale = from[i].LightMapScale;
            to[i].LightMapScalePsx2 = from[i].LightMapScalePsx2;
        }

        Keep(before, after);
    }

    /// <summary>Treats <paramref name="brush"/>'s mapping as made for the brush without its scales (a builder's
    /// fresh default mapping) and carries it to the brush's real scales, so they do not stretch it.</summary>
    public static void Unstretch(Brush brush)
    {
        Brush plain = brush.Clone();
        plain.MainScale = SignsOf(Vec3.One, brush.MainScale);
        plain.PostScale = SignsOf(Vec3.One, brush.PostScale);
        Keep(plain, brush);
    }

    /// <summary>
    /// Keeps textures through a run of scale edits that each start from the last (a value typed into a field applies
    /// at every pause): every step carries the mapping from the brush as it was before the run, so passing a zero scale
    /// on the way (typing "0.5" passes "0") or any other step loses nothing. A brush changed by anything else since its
    /// last step starts a new run.
    /// </summary>
    public sealed class Series
    {
        private readonly Dictionary<string, (Brush Start, Brush Last)> _brushes = new(StringComparer.OrdinalIgnoreCase);

        public void Rescale(Brush brush, Action<Brush> change, bool keepTextures = true)
        {
            if (!keepTextures)
            {
                _brushes.Remove(brush.Id);
                change(brush);
                return;
            }

            Brush start = _brushes.TryGetValue(brush.Id, out (Brush Start, Brush Last) run) && Same(run.Last, brush) ? run.Start : brush.Clone();
            change(brush);
            Keep(start, brush);
            _brushes[brush.Id] = (start, brush.Clone());
        }

        public void Clear() => _brushes.Clear();

        private static bool Same(Brush a, Brush b) =>
            a.MainScale == b.MainScale && a.PostScale == b.PostScale && a.Rotation == b.Rotation && a.PrePivot == b.PrePivot
            && a.Location == b.Location && a.Shape.Faces.Count == b.Shape.Faces.Count
            && a.Shape.Faces.Zip(b.Shape.Faces).All(p => p.First.TextureBase == p.Second.TextureBase && p.First.TextureU == p.Second.TextureU
                && p.First.TextureV == p.Second.TextureV && p.First.PanU == p.Second.PanU && p.First.PanV == p.Second.PanV
                && p.First.Vertices.SequenceEqual(p.Second.Vertices));
    }

    /// <summary>The smallest turn that takes face <paramref name="s"/>'s world plane to face <paramref name="d"/>'s,
    /// and the point it turns about (the new face's centre); null when the face did not turn (or turned right over,
    /// where no turn is the smallest).</summary>
    private static (Mat3, Vec3)? Turn(ShapeTransform t0, BrushFace s, ShapeTransform t1, BrushFace d)
    {
        if (d.Vertices.Count == 0)
        {
            return null;
        }

        Vec3 n0 = t0.TextureAxisToWorld(s.Normal()).Normalized(), n1 = t1.TextureAxisToWorld(d.Normal()).Normalized();
        Vec3 axis = Vec3.Cross(n0, n1);
        double sin = axis.Length, cos = Vec3.Dot(n0, n1);
        if (sin < MinTurn)
        {
            return null;
        }

        // Rodrigues: v cos + (k x v) sin + k (k . v)(1 - cos).
        Vec3 k = axis / sin;
        Vec3 Rotate(Vec3 v) => v * cos + Vec3.Cross(k, v) * sin + k * (Vec3.Dot(k, v) * (1 - cos));
        Vec3 centre = d.Vertices.Aggregate(Vec3.Zero, (a, v) => a + v) / d.Vertices.Count;
        return (new Mat3(Rotate(Vec3.UnitX), Rotate(Vec3.UnitY), Rotate(Vec3.UnitZ)), t1.ToWorld(centre));
    }

    private static bool Invertible(Brush b) =>
        Math.Abs(b.MainScale.X) > MinScale && Math.Abs(b.MainScale.Y) > MinScale && Math.Abs(b.MainScale.Z) > MinScale
        && Math.Abs(b.PostScale.X) > MinScale && Math.Abs(b.PostScale.Y) > MinScale && Math.Abs(b.PostScale.Z) > MinScale;

    /// <summary><paramref name="v"/> with the signs of <paramref name="like"/> (positive where it is zero).</summary>
    private static Vec3 SignsOf(Vec3 v, Vec3 like) =>
        new(Math.Abs(v.X) * (like.X < 0 ? -1 : 1), Math.Abs(v.Y) * (like.Y < 0 ? -1 : 1), Math.Abs(v.Z) * (like.Z < 0 ? -1 : 1));

    // Rounded far below a texel (axes are texels per unit, bases are points): only float noise and -0 go.
    private static Vec3 Axis(Vec3 v) => new(Round(v.X, 10), Round(v.Y, 10), Round(v.Z, 10));

    private static Vec3 Point(Vec3 v) => new(Round(v.X, 6), Round(v.Y, 6), Round(v.Z, 6));

    private static double Round(double x, int digits)
    {
        double r = Math.Round(x, digits);
        return r == 0 ? 0 : r;
    }
}
