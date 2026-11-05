using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// A face's mapping in the terms the surface panel shows: world units per texel along U and V, the rotation in
/// degrees from wall/floor alignment (as <see cref="TextureOps.Rotate"/> turns it), the pan, and the world normal.
/// </summary>
public readonly record struct SurfaceInfo(double ScaleU, double ScaleV, double Rotation, int PanU, int PanV, Vec3 Normal, double Area)
{
    public static SurfaceInfo Of(BrushFace f, ShapeTransform t)
    {
        List<Vec3> w = t.FaceToWorld(f);
        Vec3 n = Poly.Normal(w);
        Vec3 u = t.TextureAxisToWorld(f.TextureU), v = t.TextureAxisToWorld(f.TextureV);
        double scaleU = u.Length > 1e-12 ? 1 / u.Length : 0, scaleV = v.Length > 1e-12 ? 1 / v.Length : 0;
        return new SurfaceInfo(Poly.Tidy(scaleU), Poly.Tidy(scaleV), Poly.Tidy(RotationOf(u, n)), f.PanU, f.PanV, n, Poly.Area(w));
    }

    /// <summary>The reference U of wall alignment (level along the face), or world X for floors and ceilings.</summary>
    public static Vec3 ReferenceU(Vec3 normal)
    {
        Vec3 u = Vec3.Cross(normal, Vec3.UnitZ);
        return u.Length < 1e-3 ? Vec3.UnitX : u.Normalized();
    }

    /// <summary>Degrees the reference U must turn (clockwise seen from the front) to reach <paramref name="u"/>,
    /// in (-180, 180].</summary>
    private static double RotationOf(Vec3 u, Vec3 n)
    {
        Vec3 inPlane = (u - n * Vec3.Dot(n, u)).Normalized();
        if (inPlane == Vec3.Zero || n == Vec3.Zero)
        {
            return 0;
        }

        Vec3 r = ReferenceU(n);
        r = (r - n * Vec3.Dot(n, r)).Normalized();
        double a = Math.Atan2(Vec3.Dot(n, Vec3.Cross(r, inPlane)), Vec3.Dot(r, inPlane));
        double degrees = -a * 180 / Math.PI;
        return degrees <= -180 + 1e-9 ? 180 : degrees;
    }
}
