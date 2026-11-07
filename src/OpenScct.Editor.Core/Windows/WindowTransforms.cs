using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Windows;

public static class WindowTransforms
{
    public static void Resize(WindowDesign d, double width, double height)
    {
        double sy = width / d.Width, sz = height / d.Height;
        if (d.Shape == "circle") { sz = sy; height = width; }
        d.Outline = d.Outline.Select(p => new WindowPoint(p.Y * sy, p.Z * sz)).ToList();
        d.Width = width; d.Height = height; d.Validate();
    }
    /// <summary>Expose scale gizmo edits as authored dimensions; preserve circle aspect and mirror signs.</summary>
    public static void Follow(Actor before, Actor after)
    {
        if (after.Window is not { Binding: null } d) return;
        Vec3 start = before.DrawScale3D * before.DrawScale, end = after.DrawScale3D * after.DrawScale;
        if (start == end) return;
        double Ratio(int axis) => Math.Abs(start[axis]) < 1e-9 ? 1 : Math.Abs(end[axis] / start[axis]);
        double y = Ratio(1), z = Ratio(2), x = Ratio(0);
        if (d.Shape == "circle") { double factor = Math.Abs(y - 1) > 1e-8 ? y : z; y = z = factor; }
        Resize(d, d.Width * y, d.Height * z); d.Thickness *= x; d.FrameDepth *= x; d.CutDepth *= x;
        d.FrameWidth *= Math.Min(y, z); d.Clearance *= Math.Min(y, z); d.DepthOffset *= x;
        after.DrawScale = 1; after.DrawScale3D = new(Math.Sign(end.X), Math.Sign(end.Y), Math.Sign(end.Z));
        d.Validate(); WindowAssets.Prepare(after);
    }
}
