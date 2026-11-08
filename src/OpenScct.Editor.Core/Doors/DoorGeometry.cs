using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Doors;

public static class DoorGeometry
{
    public sealed record Part(BrushShape Shape, string Material);
    public static List<Part> Leaf(DoorDesign d)
    {
        d.Validate();
        var parts = new List<Part>();
        double w = d.Width - d.Clearance * 2, h = d.Height - d.Clearance * 2, t = d.Thickness;
        string face = string.IsNullOrWhiteSpace(d.Material) ? "Editor.Door_" + d.Style : d.Material;
        void BoxPart(Vec3 size, Vec3 at, string material)
        {
            var shape = Box.Shape(size);
            foreach (var f in shape.Faces) { f.Vertices = f.Vertices.Select(v => v + at).ToList(); f.Material = material; }
            parts.Add(new(shape, material));
        }
        if (d.Style == "glazed")
        {
            double edge = Math.Min(w / 6, 12), glassH = h * .46;
            BoxPart(new(t, edge, h), new(0, -(w - edge) / 2, 0), face);
            BoxPart(new(t, edge, h), new(0, (w - edge) / 2, 0), face);
            BoxPart(new(t, w - edge * 2, h * .5), new(0, 0, -h * .25), face);
            BoxPart(new(t, w - edge * 2, h * .04), new(0, 0, h * .48), face);
            BoxPart(new(2, w - edge * 2, glassH), new(0, 0, glassH / 2), "Editor.DoorGlass");
        }
        else
        {
            BoxPart(new(t, w, h), Vec3.Zero, face);
            foreach (int side in new[] { -1, 1 })
            {
                if (d.Style == "panelled")
                    foreach (double z in new[] { -h * .25, h * .23 }) BoxPart(new(2, w * .72, h * .36), new(side * (t / 2 + 1), 0, z), "Editor.DoorInset");
                if (d.Style is "industrial" or "security")
                {
                    foreach (double z in new[] { -h * .38, 0, h * .38 }) BoxPart(new(3, w * .9, 6), new(side * (t / 2 + 1.5), 0, z), "Editor.DoorMetal");
                    if (d.Style == "security") foreach (double y in new[] { -w * .32, w * .32 }) BoxPart(new(3, 6, h * .9), new(side * (t / 2 + 1.5), y, 0), "Editor.DoorMetal");
                }
                if (d.Style == "louvred") for (int i = 0; i < 10; i++) BoxPart(new(3, w * .75, 4), new(side * (t / 2 + 1.5), 0, h * (-.34 + i * .075)), "Editor.DoorMetal");
            }
        }
        foreach (int side in new[] { -1, 1 })
        {
            double y = d.Motion == "swing right" ? -w * .36 : w * .36;
            BoxPart(new(3, 6, 18), new(side * (t / 2 + 2), y, -h * .06), "Editor.DoorMetal");
            BoxPart(new(6, 16, 3), new(side * (t / 2 + 5), y - 4, -h * .04), "Editor.DoorMetal");
        }
        return parts;
    }
    /// <summary>The frame stays at the closed pose while the leaf moves between keys.</summary>
    public static Actor Closed(Actor a)
    {
        var closed = a.Clone(); var m = MoverSetup.Read(a); closed.Location = m.BasePos; closed.Rotation = m.BaseRot; return closed;
    }
    public static List<Brush> Parts(Actor actor)
    {
        if (actor.Door is not { } d) return [];
        var a = Closed(actor); var result = new List<Brush>();
        void Add(string suffix, Vec3 size, Vec3 at, CsgOper op)
        {
            var shape = Box.Shape(size);
            foreach (var f in shape.Faces) { f.Vertices = f.Vertices.Select(v => v + at).ToList(); f.Material = d.FrameMaterial; var basis = Poly.Basis(f.Normal()); f.TextureU = basis.U; f.TextureV = basis.V; }
            result.Add(new(a.Id + "_" + suffix) { Shape = shape, Oper = op, Location = a.Location, Rotation = a.Rotation, PrePivot = a.PrePivot, MainScale = a.DrawScale3D * a.DrawScale, Editor = a.Editor.Clone() });
        }
        if (d.CutDepth > 0) Add("Opening", new(d.CutDepth, d.Width, d.Height + .5), new(0, 0, -.25), CsgOper.Subtract);
        if (d.Framed)
        {
            Add("LeftJamb", new(d.FrameDepth, d.FrameWidth, d.Height + d.FrameWidth), new(0, -(d.Width + d.FrameWidth) / 2, d.FrameWidth / 2), CsgOper.Add);
            Add("RightJamb", new(d.FrameDepth, d.FrameWidth, d.Height + d.FrameWidth), new(0, (d.Width + d.FrameWidth) / 2, d.FrameWidth / 2), CsgOper.Add);
            Add("Lintel", new(d.FrameDepth, d.Width, d.FrameWidth), new(0, 0, (d.Height + d.FrameWidth) / 2), CsgOper.Add);
        }
        return result;
    }
    public static bool CanCarve(MapProject p, DoorDesign d) => !Compile.BakedContent.BakedWorldKept(p) || d.Wall is { } id && p.FindBrush(id) is { } wall && !Compile.BakedContent.IsBaked(wall);
    public static MapProject Expand(MapProject source, Assets.AssetResolver? assets = null)
    {
        if (!source.Actors.Any(a => a.Door is { IsLowered: false })) return source;
        var p = ProjectSnapshot.Take(source); var ids = new FreshIds(p);
        foreach (var a in p.Actors.Where(a => a.Door is { IsLowered: false }))
        {
            a.Door!.Validate();
            if (a.Door.CutDepth > 0 && !CanCarve(p, a.Door)) throw new ArgumentException(a.Id + ": retained baked walls cannot be cut; place the door in an existing opening");
            DoorAssets.Prepare(a, assets);
            foreach (var part in Parts(a)) { part.Id = ids.Next(part.Id); p.Brushes.Add(part); }
            a.Door.IsLowered = true;
        }
        return p;
    }
}
