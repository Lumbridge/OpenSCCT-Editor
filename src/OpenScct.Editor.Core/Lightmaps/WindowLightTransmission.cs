using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>Coloured light through the actual pane outlines, shared by surface and vertex lighting.</summary>
public sealed class WindowLightTransmission
{
    private readonly TriangleBvh _panes;
    private readonly Vec3[] _colours;

    public WindowLightTransmission(IEnumerable<Actor> windows)
    {
        var triangles = new List<OccluderTriangle>();
        var colours = new List<Vec3>();
        foreach (Actor actor in windows)
        {
            if (actor.Window is not { } d || Math.Abs(actor.DrawScale3D.X * actor.DrawScale3D.Y * actor.DrawScale3D.Z * actor.DrawScale) < 1e-12) continue;
            var outline = WindowGeometry.Offset(WindowGeometry.Outline(d), -d.Clearance);
            int source = colours.Count;
            // Treat the pane as one optical sheet, so its front and back do not filter the light twice.
            foreach (var (a, b, c) in WindowGeometry.Triangles(outline))
            {
                Vec3 Point(int i) => actor.LocalToWorld(new Vec3(d.DepthOffset, outline[i].Y, outline[i].Z));
                triangles.Add(new(Point(a), Point(b), Point(c), OccluderFlags.None, source));
            }
            double transmission = d.Finish == "frosted" ? .5 : d.Finish == "tinted" ? .72 : .94;
            colours.Add(new Vec3(Math.Clamp(d.Tint[0], 0, 1), Math.Clamp(d.Tint[1], 0, 1), Math.Clamp(d.Tint[2], 0, 1)) * transmission);
        }
        _panes = TriangleBvh.Build(triangles);
        _colours = colours.ToArray();
    }

    public bool IsEmpty => _colours.Length == 0;

    public Vec3 Filter(BakeLight light, Vec3 point) => Between(point,
        light.IsSun ? point - light.Direction.Normalized() * BakeLight.SunTraceLength : light.Location);

    public Vec3 Between(Vec3 from, Vec3 to)
    {
        if (IsEmpty) return Vec3.One;
        Vec3 colour = Vec3.One;
        Vec3 direction = (to - from).Normalized();
        double remaining = (to - from).Length - .1;
        Vec3 origin = from + direction * .05;
        for (int i = 0; i < _colours.Length && remaining > .1; i++)
        {
            BvhHit? hit = _panes.Closest(LightmapBaker.F(origin), LightmapBaker.F(direction), (float)remaining);
            if (hit is not { } h) break;
            colour = colour.Scale(_colours[_panes.SourceOf(h.Triangle)]);
            double step = h.Distance + .05;
            origin += direction * step;
            remaining -= step;
        }
        return colour;
    }
}
