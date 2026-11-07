using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Windows;

/// <summary>One planar outline and extrusion implementation for the editor, collider and playable mesh.</summary>
public static class WindowGeometry
{
    public static List<WindowPoint> Outline(WindowDesign d)
    {
        d.Validate();
        if (d.Outline.Count >= 3) return Positive(d.Outline);
        double w = d.Width / 2, h = d.Height / 2;
        if (d.Shape == "rectangle") return [new(-w, -h), new(w, -h), new(w, h), new(-w, h)];
        if (d.Shape == "triangle") return [new(-w, -h), new(w, -h), new(0, h)];
        if (d.Shape == "arch")
        {
            var p = new List<WindowPoint> { new(-w, -h), new(w, -h), new(w, 0) };
            for (int i = 1; i <= 32; i++) { double a = Math.PI * i / 32; p.Add(new(w * Math.Cos(a), h * Math.Sin(a))); }
            return p;
        }
        int n = d.Shape == "polygon" ? d.Sides : 64;
        return Enumerable.Range(0, n).Select(i => { double a = 2 * Math.PI * i / n + Math.PI / 2; return new WindowPoint(w * Math.Cos(a), h * Math.Sin(a)); }).ToList();
    }

    public static double Area(IReadOnlyList<WindowPoint> p) => Enumerable.Range(0, p.Count).Sum(i => p[i].Y * p[(i + 1) % p.Count].Z - p[(i + 1) % p.Count].Y * p[i].Z) / 2;
    public static List<WindowPoint> Positive(IEnumerable<WindowPoint> points) { var p = points.ToList(); if (Area(p) < 0) p.Reverse(); return p; }
    public static bool Contains(IReadOnlyList<WindowPoint> p, WindowPoint v)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Z > v.Z) != (p[j].Z > v.Z) && v.Y < (p[j].Y - p[i].Y) * (v.Z - p[i].Z) / (p[j].Z - p[i].Z) + p[i].Y) inside = !inside;
        return inside;
    }

    /// <summary>Offset the edges, intersecting adjacent offset lines (positive grows the outline).</summary>
    public static List<WindowPoint> Offset(IReadOnlyList<WindowPoint> source, double amount)
    {
        var p = Positive(source); var result = new List<WindowPoint>();
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[(i + p.Count - 1) % p.Count]; var b = p[i]; var c = p[(i + 1) % p.Count];
            Vec3 u = new Vec3(0, b.Y - a.Y, b.Z - a.Z).Normalized(), v = new Vec3(0, c.Y - b.Y, c.Z - b.Z).Normalized();
            Vec3 n1 = new(0, u.Z, -u.Y), n2 = new(0, v.Z, -v.Y), bisector = n1 + n2;
            double denominator = Vec3.Dot(bisector, n1);
            Vec3 delta = denominator < 1e-8 ? n1 * amount : bisector * (amount / denominator);
            result.Add(new(b.Y + delta.Y, b.Z + delta.Z));
        }
        if (Math.Abs(Area(result)) < .01 || Math.Sign(Area(result)) != Math.Sign(Area(p))) throw new ArgumentException("Glass clearance consumes this opening; reduce the clearance");
        if (amount < 0 && result.Any(q => !Contains(p, q) || Enumerable.Range(0, p.Count).Min(i =>
        {
            var a = p[i]; var b = p[(i + 1) % p.Count]; double dy = b.Y - a.Y, dz = b.Z - a.Z;
            double t = Math.Clamp(((q.Y - a.Y) * dy + (q.Z - a.Z) * dz) / Math.Max(1e-12, dy * dy + dz * dz), 0, 1);
            return Math.Sqrt(Math.Pow(q.Y - a.Y - t * dy, 2) + Math.Pow(q.Z - a.Z - t * dz, 2));
        }) < -amount - .01)) throw new ArgumentException("Glass clearance does not fit this opening");
        return result;
    }

    /// <summary>Ear clipping preserves concave detected outlines instead of substituting a convex hull.</summary>
    public static List<(int A, int B, int C)> Triangles(IReadOnlyList<WindowPoint> p)
    {
        var remaining = Enumerable.Range(0, p.Count).ToList(); var triangles = new List<(int, int, int)>();
        double Cross(WindowPoint a, WindowPoint b, WindowPoint c) => (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y);
        bool In(WindowPoint v, WindowPoint a, WindowPoint b, WindowPoint c) => Cross(a, b, v) >= -1e-7 && Cross(b, c, v) >= -1e-7 && Cross(c, a, v) >= -1e-7;
        while (remaining.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Cross(p[a], p[b], p[c]) <= 1e-8 || remaining.Any(k => k != a && k != b && k != c && In(p[k], p[a], p[b], p[c]))) continue;
                triangles.Add((a, b, c)); remaining.RemoveAt(i); found = true; break;
            }
            if (!found) throw new ArgumentException("The frame outline crosses itself or is not a closed simple opening");
        }
        if (remaining.Count == 3) triangles.Add((remaining[0], remaining[1], remaining[2]));
        return triangles;
    }

    public static BrushShape Prism(IReadOnlyList<WindowPoint> points, double depth, double offset = 0)
    {
        var p = Positive(points); var shape = new BrushShape();
        Vec3 At(int i, double x) => new(x + offset, p[i].Y, p[i].Z);
        void Face(IEnumerable<Vec3> v) => shape.Faces.Add(new BrushFace { Vertices = v.ToList() });
        foreach (var (a, b, c) in Triangles(p)) { Face([At(c, -depth / 2), At(b, -depth / 2), At(a, -depth / 2)]); Face([At(a, depth / 2), At(b, depth / 2), At(c, depth / 2)]); }
        for (int i = 0; i < p.Count; i++) { int j = (i + 1) % p.Count; Face([At(i, -depth / 2), At(j, -depth / 2), At(j, depth / 2), At(i, depth / 2)]); }
        return shape;
    }
    public static BrushShape Pane(WindowDesign d) => Prism(Offset(Outline(d), -d.Clearance), d.Thickness, d.DepthOffset);
    public static List<Brush> Parts(Actor a, bool includeFrame = true)
    {
        if (a.Window is not { } d) return [];
        var inner = Outline(d); var brushes = new List<Brush>();
        void Add(string suffix, BrushShape shape, CsgOper operation, string? material = null)
        {
            foreach (var f in shape.Faces)
            { f.Material = material; var basis = Poly.Basis(f.Normal()); f.TextureU = basis.U; f.TextureV = basis.V; }
            brushes.Add(new Brush(a.Id + "_" + suffix) { Shape = shape, Oper = operation, Location = a.Location, Rotation = a.Rotation, MainScale = a.DrawScale3D * a.DrawScale, PrePivot = a.PrePivot, Editor = a.Editor.Clone() });
        }
        if (d.CutDepth > 0)
        {
            bool convex = Enumerable.Range(0, inner.Count).All(i => { var a = inner[i]; var b = inner[(i + 1) % inner.Count]; var c = inner[(i + 2) % inner.Count]; return (b.Y - a.Y) * (c.Z - b.Z) - (b.Z - a.Z) * (c.Y - b.Y) >= -1e-7; });
            if (convex) Add("Cut", Prism(inner, d.CutDepth), CsgOper.Subtract);
            else foreach (var (x, y, z) in Triangles(inner)) Add("Cut" + brushes.Count, Prism([inner[x], inner[y], inner[z]], d.CutDepth), CsgOper.Subtract);
        }
        if (includeFrame && d.Framed)
        {
            var outer = Offset(inner, d.FrameWidth);
            for (int i = 0; i < inner.Count; i++) { int j = (i + 1) % inner.Count; Add("Frame" + i, Prism([inner[i], outer[i], outer[j], inner[j]], d.FrameDepth), CsgOper.Add, d.FrameMaterial); }
        }
        return brushes;
    }
    public static bool CanCarve(MapProject project, WindowDesign d) => !Compile.BakedContent.BakedWorldKept(project)
        || d.Wall is { } id && project.FindBrush(id) is { } wall && !Compile.BakedContent.IsBaked(wall);

    public static MapProject Expand(MapProject project, Assets.AssetResolver? assets = null)
    {
        if (!project.Actors.Any(a => a.Window is { IsLowered: false })) return project;
        var p = ProjectSnapshot.Take(project);
        var used = p.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors.Where(a => a.Window is { IsLowered: false }))
        {
            a.Window!.Validate();
            if (a.Window.CutDepth > 0 && !CanCarve(p, a.Window)) throw new ArgumentException($"{a.Id}: windows cannot cut retained baked geometry; place in an existing opening");
            WindowAssets.Prepare(a, assets);
            foreach (var part in Parts(a))
            {
                string stem = part.Id;
                for (int suffix = 1; !used.Add(part.Id); suffix++) part.Id = stem + "_" + suffix;
                p.Brushes.Add(part);
            }
            // Lowering is idempotent: the build copy now contains ordinary actors and ordinary frame/cut brushes.
            a.Window.IsLowered = true;
        }
        return p;
    }
}
