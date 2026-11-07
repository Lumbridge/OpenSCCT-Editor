using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Windows;

public sealed record WindowOpening(string[] Sources, Vec3 Centre, Rot Rotation, List<WindowPoint> Outline, double Distance, double Depth);

/// <summary>Finds holes in actual frame cross-sections, including closed, watertight imported frame meshes.</summary>
public static class WindowFit
{
    private sealed record MeshSlice(long Stamp, List<List<Vec3[]>> Sections, Vec3 Min, Vec3 Max);
    private static readonly ConcurrentDictionary<string, MeshSlice> MeshCache = new();
    private static (int X, int Y, int Z) DirectionKey(Vec3 n)
    {
        n = n.Normalized(); if (n.X < -.00001 || Math.Abs(n.X) < .00001 && (n.Y < -.00001 || Math.Abs(n.Y) < .00001 && n.Z < 0)) n = -n;
        return ((int)Math.Round(n.X * 1000), (int)Math.Round(n.Y * 1000), (int)Math.Round(n.Z * 1000));
    }
    private static MeshSlice ReadMesh(LibraryMesh mesh)
    {
        string key = mesh.GlbPath + ":" + mesh.Mesh; long stamp = File.GetLastWriteTimeUtc(mesh.GlbPath).Ticks;
        if (MeshCache.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached;
        var sections = MeshSections.Read(mesh).Select(s => Enumerable.Range(0, s.Indices.Length / 3).Select(i => new[] { Point(s, s.Indices[i * 3]), Point(s, s.Indices[i * 3 + 1]), Point(s, s.Indices[i * 3 + 2]) }).ToList()).ToList();
        static Vec3 Point(MeshSection s, int i) => new(s.Positions[i * 3] * 100, s.Positions[i * 3 + 2] * 100, s.Positions[i * 3 + 1] * 100);
        var points = sections.SelectMany(s => s).SelectMany(t => t).ToList();
        return MeshCache[key] = new(stamp, sections, points.Count == 0 ? Vec3.Zero : points.Aggregate(Vec3.Min), points.Count == 0 ? Vec3.Zero : points.Aggregate(Vec3.Max));
    }
    private static bool RayBox(Vec3 origin, Vec3 direction, Vec3 min, Vec3 max)
    {
        double enter = 0, leave = double.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(direction[axis]) < 1e-10) { if (origin[axis] < min[axis] || origin[axis] > max[axis]) return false; continue; }
            double a = (min[axis] - origin[axis]) / direction[axis], b = (max[axis] - origin[axis]) / direction[axis];
            enter = Math.Max(enter, Math.Min(a, b)); leave = Math.Min(leave, Math.Max(a, b)); if (leave < enter) return false;
        }
        return true;
    }
    public static ShapeTransform? Transform(MapProject p, string id) => p.FindActor(id) is { } a ? ShapeTransform.For(a) : p.FindBrush(id) is { } b ? ShapeTransform.For(b) : null;
    public static string GeometryKey(MapProject p, IEnumerable<string> ids)
    {
        string key = string.Join('|', ids.Select(id => p.FindActor(id) is { } a ? PropValues.AssetPath(a.Props["StaticMesh"]) ?? a.Props.ToJson().ToString()
            : p.FindBrush(id) is { } b ? string.Join(';', b.Shape.Faces.SelectMany(f => f.Vertices).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:R},{v.Y:R},{v.Z:R}"))) : "missing"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
    public static List<WindowOpening> Find(MapProject p, AssetResolver assets, Vec3 origin, Vec3 direction, Func<EditorMeta, bool>? allowed = null, string? ignorePane = null)
    {
        allowed ??= m => !m.Hidden && !m.Locked;
        var result = new List<WindowOpening>();
        foreach (Actor a in p.Actors.Where(a => a.Window is null && allowed(a.Editor)))
        {
            string? path = PropValues.AssetPath(a.Props["StaticMesh"] ?? Services.EditorServices.Classes?.Default(a.Class, "StaticMesh"));
            if (path is null || assets.FindViewMesh(path) is not { } mesh) continue;
            var local = ReadMesh(mesh); var localOrigin = a.WorldToLocal(origin); var localDirection = a.WorldToLocal(origin + direction) - localOrigin;
            if (!RayBox(localOrigin, localDirection, local.Min, local.Max)) continue;
            var sections = local.Sections.Select(s => s.Select(tri => tri.Select(a.LocalToWorld).ToArray()).ToList()).ToList();
            var triangles = sections.SelectMany(s => s).ToList();
            if (triangles.Count == 0) continue;
            var planes = triangles.Select(t => Vec3.Cross(t[1] - t[0], t[2] - t[0])).Where(n => n.Length > 1e-6)
                .GroupBy(DirectionKey).OrderByDescending(g => g.Sum(n => n.Length)).Take(12).Select(g => g.First().Normalized());
            foreach (Vec3 normal in planes.Concat([a.Rotation.Axes().Forward, a.Rotation.Axes().Right, a.Rotation.Axes().Up])
                .DistinctBy(DirectionKey).OrderByDescending(n => Math.Abs(Vec3.Dot(n, direction))))
                Slice(triangles, [a.Id], normal, union: true, sections: sections);
        }
        foreach (Brush b in p.Brushes.Where(b => b.Oper == CsgOper.Subtract && b.Shape.Builder?.Kind == "window" && allowed(b.Editor)))
        {
            var v = b.Shape.Faces.SelectMany(f => f.Vertices).ToList(); if (v.Count == 0) continue;
            Vec3 lo = v.Aggregate(Vec3.Min), hi = v.Aggregate(Vec3.Max), centre = (lo + hi) / 2;
            var transform = ShapeTransform.For(b); Vec3 n = transform.Linear.C0.Normalized();
            Rot rotation = Orientation.FromAxes(n, transform.Linear.C2);
            Vec3 at = transform.ToWorld(centre);
            var outline = new[] { new Vec3(centre.X, lo.Y, lo.Z), new Vec3(centre.X, hi.Y, lo.Z), new Vec3(centre.X, hi.Y, hi.Z), new Vec3(centre.X, lo.Y, hi.Z) }
                .Select(transform.ToWorld).Select(x => rotation.Unrotate(x - at)).Select(x => new WindowPoint(x.Y, x.Z)).ToList();
            Add([b.Id], at, rotation, outline, (hi.X - lo.X) * transform.Linear.C0.Length);
        }
        // Brush frames may consist of separate intersecting bars. Slice their union, discarding internal edges.
        var solids = p.Brushes.Where(b => b.Oper == CsgOper.Add && allowed(b.Editor) && b.Stair is null).ToList();
        foreach (var group in solids.GroupBy(b => DirectionKey(new[] { b.Rotation.Axes().Forward, b.Rotation.Axes().Right, b.Rotation.Axes().Up }
            .MaxBy(n => Math.Abs(Vec3.Dot(n, direction))))))
        {
            var sections = group.Select(b => b.WorldFaces().SelectMany(f => Enumerable.Range(1, Math.Max(0, f.Vertices.Count - 2))
                .Select(i => new[] { f.Vertices[0], f.Vertices[i], f.Vertices[i + 1] })).ToList()).ToList();
            var triangles = sections.SelectMany(s => s).ToList();
            var facing = group.First().Rotation.Axes();
            foreach (var normal in new[] { new[] { facing.Forward, facing.Right, facing.Up }.MaxBy(n => Math.Abs(Vec3.Dot(n, direction))) })
            {
                if (Math.Abs(Vec3.Dot(normal, direction)) < .05) continue;
                foreach (double level in group.Select(b => Vec3.Dot(b.Location, normal)).Distinct().Take(128))
                    Slice(triangles, group.Select(b => b.Id).ToArray(), normal, level, union: true, sections);
            }
        }
        return result.OrderBy(x => x.Distance).ToList();

        void Slice(List<Vec3[]> triangles, string[] sources, Vec3 normal, double? slice = null, bool union = false, List<List<Vec3[]>>? sections = null)
        {
            if (Math.Abs(Vec3.Dot(normal, direction)) < .05) return;
            double lo = triangles.SelectMany(t => t).Min(v => Vec3.Dot(v, normal)), hi = triangles.SelectMany(t => t).Max(v => Vec3.Dot(v, normal));
            double level = slice ?? (lo + hi) / 2;
            double rayT = (level - Vec3.Dot(origin, normal)) / Vec3.Dot(direction, normal); if (rayT <= .01 || rayT > 65536) return;
            Rot rot = Orientation.FromAxes(normal, Math.Abs(normal.Z) < .95 ? Vec3.UnitZ : Vec3.UnitY); Vec3 centre = normal * level;
            var regions = new List<List<List<WindowPoint>>>();
            foreach (var batch in sections ?? [triangles])
            {
              var segments = new List<(WindowPoint A, WindowPoint B)>();
              foreach (var tri in batch)
              {
                var points = new List<Vec3>();
                for (int i = 0; i < 3; i++)
                {
                    Vec3 a = tri[i], b = tri[(i + 1) % 3]; double da = Vec3.Dot(a, normal) - level, db = Vec3.Dot(b, normal) - level;
                    if (Math.Abs(da) < 1e-7) points.Add(a);
                    if (da * db < -1e-12) points.Add(a + (b - a) * (da / (da - db)));
                }
                var unique = points.DistinctBy(v => (Math.Round(v.X, 5), Math.Round(v.Y, 5), Math.Round(v.Z, 5))).ToList();
                if (unique.Count != 2) continue;
                WindowPoint Project(Vec3 v) { var q = rot.Unrotate(v - centre); return new(q.Y, q.Z); }
                var start = Project(unique[0]); var end = Project(unique[1]);
                if (Distance(start, end) > 1e-5) segments.Add((start, end));
              }
              regions.Add(Loops(segments));
            }
            var loops = union ? UnionBoundary(regions) : regions.SelectMany(r => r).ToList();
            foreach (var loop in loops.Where(loop => Math.Abs(WindowGeometry.Area(loop)) >= 4))
            {
                // A hole is enclosed by an odd number of larger contours; an outer boundary is not an opening.
                int nesting = loops.Count(other => !ReferenceEquals(loop, other) && Math.Abs(WindowGeometry.Area(other)) > Math.Abs(WindowGeometry.Area(loop)) + .01 && WindowGeometry.Contains(other, loop[0]));
                if (nesting % 2 == 1) Add(sources, centre, rot, loop, hi - lo);
            }
        }
        void Add(string[] sources, Vec3 centre, Rot rotation, List<WindowPoint> outline, double depth)
        {
            double denom = Vec3.Dot(direction, rotation.Axes().Forward); if (Math.Abs(denom) < .05) return;
            double t = Vec3.Dot(centre - origin, rotation.Axes().Forward) / denom; if (t <= .01) return;
            Vec3 hit = rotation.Unrotate(origin + direction * t - centre);
            if (!WindowGeometry.Contains(outline, new(hit.Y, hit.Z))) return;
            double y = (outline.Min(v => v.Y) + outline.Max(v => v.Y)) / 2, z = (outline.Min(v => v.Z) + outline.Max(v => v.Z)) / 2;
            Vec3 pivot = centre + rotation.Rotate(new Vec3(0, y, z));
            if (sources.Length > 1)
            {
                var boundary = outline.Select(q => centre + rotation.Rotate(new Vec3(0, q.Y, q.Z))).ToList();
                sources = sources.Where(id => p.FindBrush(id) is { } b && boundary.Any(q => b.WorldFaces().All(f => Vec3.Dot(f.Normal(), q - f.Vertices[0]) <= .05))).ToArray();
                if (sources.Length == 0) return;
            }
            var local = WindowGeometry.Positive(outline.Select(v => new WindowPoint(v.Y - y, v.Z - z)));
            try { WindowGeometry.Triangles(local); WindowGeometry.Offset(local, -.5); } catch (ArgumentException) { return; }
            if (result.Any(r => (r.Centre - pivot).Length < .5 && Math.Abs(Math.Abs(WindowGeometry.Area(r.Outline)) - Math.Abs(WindowGeometry.Area(local))) < 1)) return;
            if (p.Actors.Any(a => Occupied(a, pivot))) return;
            result.Add(new(sources, pivot, rotation, local, t, depth));
        }
        bool Occupied(Actor a, Vec3 pivot)
        {
            if (a.Id == ignorePane) return false;
            if (a.Window is { } d)
            {
                Vec3 q = a.WorldToLocal(pivot);
                return Math.Abs(q.X - d.DepthOffset) <= Math.Max(2, d.Thickness / 2) && WindowGeometry.Contains(WindowGeometry.Outline(d), new(q.Y, q.Z));
            }
            if (a.ShortClass != "SVitre" && Services.EditorServices.Classes?.IsA(a.Class, "SBase.SVitre") != true) return false;
            string? path = PropValues.AssetPath(a.Props["StaticMesh"] ?? Services.EditorServices.Classes?.Default(a.Class, "StaticMesh"));
            if (path is null || assets.FindViewMesh(path) is not { } mesh) return (a.Location - pivot).Length < 2;
            var pane = ReadMesh(mesh); Vec3 point = a.WorldToLocal(pivot);
            return Enumerable.Range(0, 3).All(axis => point[axis] >= pane.Min[axis] - .5 && point[axis] <= pane.Max[axis] + .5);
        }
    }

    private static double Distance(WindowPoint a, WindowPoint b) => Math.Sqrt((a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static List<List<WindowPoint>> Loops(List<(WindowPoint A, WindowPoint B)> segments, bool cancelShared = true)
    {
        // Endpoint welding handles both shared vertices and triangles split along the frame's planar faces.
        var points = new List<WindowPoint>(); var edges = new HashSet<(int, int)>();
        int Index(WindowPoint p) { int i = points.FindIndex(q => Distance(p, q) < .02); if (i >= 0) return i; points.Add(p); return points.Count - 1; }
        foreach (var (a, b) in segments)
        {
            int x = Index(a), y = Index(b); if (x == y) continue;
            var edge = x < y ? (x, y) : (y, x);
            // Adjacent closed frame pieces share internal faces; their duplicate section edges cancel.
            if (!edges.Add(edge) && cancelShared) edges.Remove(edge);
        }
        var loops = new List<List<WindowPoint>>();
        while (edges.Count > 0)
        {
            var edge = edges.First(); edges.Remove(edge); var ids = new List<int> { edge.Item1, edge.Item2 };
            while (ids[^1] != ids[0] && ids.Count <= points.Count + 1)
            {
                var next = edges.Where(e => e.Item1 == ids[^1] || e.Item2 == ids[^1]).ToList();
                if (next.Count != 1) break;
                var e = next[0]; edges.Remove(e); ids.Add(e.Item1 == ids[^1] ? e.Item2 : e.Item1);
            }
            if (ids[^1] != ids[0]) continue;
            ids.RemoveAt(ids.Count - 1); var loop = ids.Select(i => points[i]).ToList();
            for (int i = loop.Count - 1; i >= 0 && loop.Count > 3; i--)
            {
                var a = loop[(i + loop.Count - 1) % loop.Count]; var b = loop[i]; var c = loop[(i + 1) % loop.Count];
                if (Math.Abs((b.Y - a.Y) * (c.Z - b.Z) - (b.Z - a.Z) * (c.Y - b.Y)) < 1e-5) loop.RemoveAt(i);
            }
            if (loop.Count >= 3) loops.Add(WindowGeometry.Positive(loop));
        }
        return loops;
    }
    private static List<List<WindowPoint>> UnionBoundary(List<List<List<WindowPoint>>> regions)
    {
        var polygons = regions.SelectMany(r => r).ToList();
        var holes = regions.SelectMany(region => region.Where(loop => region.Count(other => !ReferenceEquals(loop, other)
            && Math.Abs(WindowGeometry.Area(other)) > Math.Abs(WindowGeometry.Area(loop)) + .01 && WindowGeometry.Contains(other, loop[0])) % 2 == 1)).ToHashSet();
        var segments = new List<(WindowPoint, WindowPoint)>();
        foreach (var p in polygons) for (int i = 0; i < p.Count; i++)
        {
            WindowPoint a = p[i], b = p[(i + 1) % p.Count]; var cuts = new List<double> { 0, 1 };
            foreach (var q in polygons) foreach (var v in q)
            {
                double length = (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z); if (length < 1e-9) continue;
                double t = ((v.Y - a.Y) * (b.Y - a.Y) + (v.Z - a.Z) * (b.Z - a.Z)) / length;
                double cross = Math.Abs((v.Y - a.Y) * (b.Z - a.Z) - (v.Z - a.Z) * (b.Y - a.Y));
                if (t > 1e-7 && t < 1 - 1e-7 && cross < .02) cuts.Add(t);
            }
            foreach (var q in polygons) for (int k = 0; k < q.Count; k++)
            {
                var c = q[k]; var e = q[(k + 1) % q.Count]; double dy = b.Y - a.Y, dz = b.Z - a.Z, ey = e.Y - c.Y, ez = e.Z - c.Z;
                double denominator = dy * ez - dz * ey; if (Math.Abs(denominator) < 1e-10) continue;
                double t = ((c.Y - a.Y) * ez - (c.Z - a.Z) * ey) / denominator;
                double u = ((c.Y - a.Y) * dz - (c.Z - a.Z) * dy) / denominator;
                if (t > 1e-7 && t < 1 - 1e-7 && u >= -1e-7 && u <= 1 + 1e-7) cuts.Add(t);
            }
            cuts = cuts.Distinct().Order().ToList();
            for (int j = 0; j + 1 < cuts.Count; j++)
            {
                WindowPoint At(double t) => new(a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                var mid = At((cuts[j] + cuts[j + 1]) / 2);
                double dy = b.Y - a.Y, dz = b.Z - a.Z, len = Math.Sqrt(dy * dy + dz * dz);
                double sign = holes.Contains(p) ? -1 : 1;
                var outside = new WindowPoint(mid.Y + dz / len * .03 * sign, mid.Z - dy / len * .03 * sign);
                if (!regions.Any(region => region.Count(q => WindowGeometry.Contains(q, outside)) % 2 == 1)) segments.Add((At(cuts[j]), At(cuts[j + 1])));
            }
        }
        return Loops(segments, cancelShared: false);
    }
}
