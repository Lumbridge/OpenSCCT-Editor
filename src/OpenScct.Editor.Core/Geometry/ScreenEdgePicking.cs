using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>A projected point and its positive camera depth (use 1 for orthographic projections).</summary>
public readonly record struct ScreenPoint(double X, double Y, double Depth);
public readonly record struct ScreenEdgeHit(Vec3 Point, double Pixels, bool Vertex);

/// <summary>Pixel-tolerant edge picking with perspective-correct interpolation back into world space.</summary>
public static class ScreenEdgePicking
{
    public static ScreenEdgeHit? Near(Vec3 a, Vec3 b, Func<Vec3, ScreenPoint?> project, double x, double y, double radius, bool vertices = true)
    {
        if (project(a) is not { Depth: > 0 } sa || project(b) is not { Depth: > 0 } sb) return null;
        return NearProjected(a, b, sa, sb, x, y, radius, vertices);
    }

    public static ScreenEdgeHit? NearProjected(Vec3 a, Vec3 b, ScreenPoint sa, ScreenPoint sb, double x, double y, double radius, bool vertices = true)
    {
        if (sa.Depth <= 0 || sb.Depth <= 0) return null;
        double dx = sb.X - sa.X, dy = sb.Y - sa.Y, length2 = dx * dx + dy * dy;
        double t = length2 > 1e-12 ? Math.Clamp(((x - sa.X) * dx + (y - sa.Y) * dy) / length2, 0, 1) : sa.Depth <= sb.Depth ? 0 : 1;
        double distance = Math.Sqrt(Math.Pow(sa.X + dx * t - x, 2) + Math.Pow(sa.Y + dy * t - y, 2));
        if (distance > radius) return null;
        double da = Math.Sqrt(Math.Pow(sa.X - x, 2) + Math.Pow(sa.Y - y, 2));
        double db = Math.Sqrt(Math.Pow(sb.X - x, 2) + Math.Pow(sb.Y - y, 2));
        if (vertices && Math.Min(da, db) <= radius * .65) return new(da <= db ? a : b, Math.Min(da, db), true);
        // Linear interpolation in the image is nonlinear along an edge at varying depths.
        double worldT = t * sa.Depth / ((1 - t) * sb.Depth + t * sa.Depth);
        return new(Vec3.Lerp(a, b, worldT), distance, false);
    }
}
