using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Tools;

/// <summary>Ray helpers the drag handles share.</summary>
public static class DragMath
{
    /// <summary>The parameter along a line (point + dir * t) closest to a ray.</summary>
    public static double LineParam(Vec3 point, Vec3 dir, Vec3 o, Vec3 d)
    {
        Vec3 w = point - o;
        double b = Vec3.Dot(dir, d), dd = Vec3.Dot(dir, w), e = Vec3.Dot(d, w);
        double denom = 1 - b * b;
        return Math.Abs(denom) < 1e-9 ? 0 : (b * e - dd) / denom;
    }

    /// <summary>Where a ray meets the plane through <paramref name="point"/> with <paramref name="normal"/>, or null.</summary>
    public static Vec3? PlaneHit(Vec3 point, Vec3 normal, Vec3 o, Vec3 d)
    {
        double denom = Vec3.Dot(d, normal);
        if (Math.Abs(denom) < 1e-9)
        {
            return null;
        }

        return o + d * (Vec3.Dot(point - o, normal) / denom);
    }
}
