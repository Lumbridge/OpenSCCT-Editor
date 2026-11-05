using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>A 3x3 matrix by columns: <c>M v = C0 v.X + C1 v.Y + C2 v.Z</c>.</summary>
public readonly record struct Mat3(Vec3 C0, Vec3 C1, Vec3 C2)
{
    public static readonly Mat3 Identity = new(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);

    public static Mat3 Diagonal(Vec3 s) => new(new Vec3(s.X, 0, 0), new Vec3(0, s.Y, 0), new Vec3(0, 0, s.Z));

    /// <summary>The UE2 rotation matrix (columns forward, right, up), so <c>Apply</c> equals <see cref="Rot.Rotate"/>.</summary>
    public static Mat3 Rotation(Rot r)
    {
        (Vec3 f, Vec3 rt, Vec3 u) = r.Axes();
        return new Mat3(f, rt, u);
    }

    public Vec3 Apply(Vec3 v) => C0 * v.X + C1 * v.Y + C2 * v.Z;

    public Vec3 ApplyTranspose(Vec3 v) => new(Vec3.Dot(C0, v), Vec3.Dot(C1, v), Vec3.Dot(C2, v));

    public static Mat3 operator *(Mat3 a, Mat3 b) => new(a.Apply(b.C0), a.Apply(b.C1), a.Apply(b.C2));

    public double Determinant => Vec3.Dot(C0, Vec3.Cross(C1, C2));

    public Mat3 Transpose() => new(new Vec3(C0.X, C1.X, C2.X), new Vec3(C0.Y, C1.Y, C2.Y), new Vec3(C0.Z, C1.Z, C2.Z));

    /// <summary>The inverse; a singular matrix (a zero scale) gives the zero matrix.</summary>
    public Mat3 Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-15)
        {
            return new Mat3(Vec3.Zero, Vec3.Zero, Vec3.Zero);
        }

        // Rows of the inverse are the cross products of the columns over the determinant.
        Vec3 r0 = Vec3.Cross(C1, C2) / det, r1 = Vec3.Cross(C2, C0) / det, r2 = Vec3.Cross(C0, C1) / det;
        return new Mat3(r0, r1, r2).Transpose();
    }

    public bool IsIdentity(double tolerance = 1e-12) =>
        (C0 - Vec3.UnitX).Length <= tolerance && (C1 - Vec3.UnitY).Length <= tolerance && (C2 - Vec3.UnitZ).Length <= tolerance;
}

/// <summary>
/// The local-to-world transform of a shape: <c>world = Linear (p - PrePivot) + Location</c>. For a brush the linear
/// part is <c>PostScale * Rotation * MainScale</c> (UE2 brush transform); for an actor's shape it is
/// <c>Rotation * DrawScale3D * DrawScale</c>. Texture axes go the other way round (<c>U_world = Linear^-T U_local</c>)
/// so a texture keeps its place on the surface.
/// </summary>
public readonly record struct ShapeTransform(Mat3 Linear, Vec3 PrePivot, Vec3 Location)
{
    public static readonly ShapeTransform Identity = new(Mat3.Identity, Vec3.Zero, Vec3.Zero);

    public static ShapeTransform For(Brush b) =>
        new(Mat3.Diagonal(b.PostScale) * Mat3.Rotation(b.Rotation) * Mat3.Diagonal(b.MainScale), b.PrePivot, b.Location);

    public static ShapeTransform For(Actor a) =>
        new(Mat3.Rotation(a.Rotation) * Mat3.Diagonal(a.DrawScale3D * a.DrawScale), a.PrePivot, a.Location);

    /// <summary>True when the transform mirrors (an odd number of negative scales): windings must be reversed.</summary>
    public bool Mirrors => Linear.Determinant < 0;

    public Vec3 ToWorld(Vec3 local) => Linear.Apply(local - PrePivot) + Location;

    public Vec3 ToLocal(Vec3 world) => Linear.Inverse().Apply(world - Location) + PrePivot;

    /// <summary>A texture axis from local to world space.</summary>
    public Vec3 TextureAxisToWorld(Vec3 local) => Linear.Inverse().ApplyTranspose(local);

    /// <summary>A texture axis from world to local space.</summary>
    public Vec3 TextureAxisToLocal(Vec3 world) => Linear.ApplyTranspose(world);

    /// <summary>A face's vertices in world space, wound outwards.</summary>
    public List<Vec3> FaceToWorld(BrushFace f)
    {
        var v = f.Vertices.Select(ToWorld).ToList();
        if (Mirrors)
        {
            v.Reverse();
        }

        return v;
    }
}
