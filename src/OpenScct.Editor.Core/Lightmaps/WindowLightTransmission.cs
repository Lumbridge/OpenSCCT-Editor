using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>Coloured light through the actual pane outlines, shared by surface and vertex lighting.</summary>
public sealed class WindowLightTransmission
{
    private readonly TriangleBvh _panes;
    private readonly Vec3[] _colours;
    public List<OpenScct.Content.WindowLightTransfer.Pane> Panes { get; } = [];
    public sealed record Sheet(string Actor, Vec3 Colour, IReadOnlyList<Vec3> Triangles);
    public List<Sheet> Sheets { get; } = [];

    public WindowLightTransmission(IEnumerable<Actor> windows)
    {
        var triangles = new List<OccluderTriangle>();
        var colours = new List<Vec3>();
        foreach (Actor actor in windows)
        {
            if (actor.Window is not { } d || Math.Abs(actor.DrawScale3D.X * actor.DrawScale3D.Y * actor.DrawScale3D.Z * actor.DrawScale) < 1e-12) continue;
            var outline = WindowGeometry.Offset(WindowGeometry.Outline(d), -d.Clearance);
            int source = colours.Count;
            var points = new List<Vec3>();
            // Treat the pane as one optical sheet, so its front and back do not filter the light twice.
            foreach (var (a, b, c) in WindowGeometry.Triangles(outline))
            {
                Vec3 Point(int i) => actor.LocalToWorld(new Vec3(d.DepthOffset, outline[i].Y, outline[i].Z));
                triangles.Add(new(Point(a), Point(b), Point(c), OccluderFlags.None, source));
                points.AddRange([Point(a), Point(b), Point(c)]);
            }
            double transmission = d.Finish == "frosted" ? .5 : d.Finish == "tinted" ? .72 : .94;
            colours.Add(new Vec3(Math.Clamp(d.Tint[0], 0, 1), Math.Clamp(d.Tint[1], 0, 1), Math.Clamp(d.Tint[2], 0, 1)) * transmission);
            Sheets.Add(new(actor.Id, colours[^1], points));
            Panes.Add(new() { Actor = actor.Id, Tint = [(float)colours[^1].X, (float)colours[^1].Y, (float)colours[^1].Z] });
        }
        _panes = TriangleBvh.Build(triangles);
        _colours = colours.ToArray();
    }

    public bool IsEmpty => _colours.Length == 0;

    public Vec3 Filter(BakeLight light, Vec3 point) => Between(point,
        light.IsSun ? point - light.Direction.Normalized() * BakeLight.SunTraceLength : light.Location);

    public WindowLightPath Path(BakeLight light, Vec3 point) => Trace(point,
        light.IsSun ? point - light.Direction.Normalized() * BakeLight.SunTraceLength : light.Location);

    public Vec3 Between(Vec3 from, Vec3 to) => Trace(from, to).Colour;
    private WindowLightPath Trace(Vec3 from, Vec3 to)
    {
        if (IsEmpty) return new([], Vec3.One);
        Vec3 colour = Vec3.One;
        var panes = new List<int>();
        Vec3 direction = (to - from).Normalized();
        double remaining = (to - from).Length - .1;
        Vec3 origin = from + direction * .05;
        for (int i = 0; i < _colours.Length && remaining > .1; i++)
        {
            BvhHit? hit = _panes.Closest(LightmapBaker.F(origin), LightmapBaker.F(direction), (float)remaining);
            if (hit is not { } h) break;
            int pane = _panes.SourceOf(h.Triangle);
            if (!panes.Contains(pane)) { panes.Add(pane); colour = colour.Scale(_colours[pane]); }
            double step = h.Distance + .05;
            origin += direction * step;
            remaining -= step;
        }
        return new(panes.ToArray(), colour);
    }
}
