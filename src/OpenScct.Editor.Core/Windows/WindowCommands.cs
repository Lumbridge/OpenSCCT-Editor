using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Windows;

public static class WindowCommands
{
    public static Actor Create(MapProject p, WindowDesign design, Vec3 location, Rot rotation = default)
    {
        var d = design.Clone(); d.Validate();
        var a = new Actor(new FreshIds(p).Next("Window"), "SBase.SVitre") { Window = d, Location = location, Rotation = rotation };
        WindowAssets.Prepare(a);
        return a;
    }
    public static AddObjectsCommand Place(MapProject p, WindowDesign d, Vec3 at, Rot rotation = default) => new("Add Window", actors: [Create(p, d, at, rotation)]);
    public static ReplaceObjectsCommand Edit(MapProject p, string id, Action<WindowDesign> edit, string? merge = null)
    {
        Actor before = p.FindActor(id) ?? throw new ArgumentException("Select a window first"); var after = before.Clone();
        if (after.Window is null) throw new ArgumentException("This glass uses its original mesh; resize it with the Scale tool");
        edit(after.Window); after.Window.Validate(); WindowAssets.Prepare(after);
        return new ReplaceObjectsCommand("Edit window", merge).Actor(before.Clone(), after);
    }
    public static Actor Fit(MapProject p, WindowOpening opening)
    {
        var d = new WindowDesign { Outline = [.. opening.Outline], Width = opening.Outline.Max(v => v.Y) - opening.Outline.Min(v => v.Y), Height = opening.Outline.Max(v => v.Z) - opening.Outline.Min(v => v.Z) };
        Actor a = Create(p, d, opening.Centre, opening.Rotation);
        Bind(p, a, opening.Sources); return a;
    }
    public static void Bind(MapProject p, Actor a, string[] sources)
    {
        var t = WindowFit.Transform(p, sources[0]) ?? throw new ArgumentException("The frame no longer exists");
        a.Window!.FrameCollisionSources = [.. sources];
        var outline = WindowGeometry.Outline(a.Window!);
        Vec3[] control = [a.Location, a.LocalToWorld(Vec3.UnitX), a.LocalToWorld(Vec3.UnitY), a.LocalToWorld(Vec3.UnitZ), .. outline.Select(v => a.LocalToWorld(new Vec3(0, v.Y, v.Z)))];
        a.Window!.Binding = new WindowBinding { Sources = sources, GeometryKey = WindowFit.GeometryKey(p, sources), Points = control.Select(t.ToLocal).Select(v => new[] { v.X, v.Y, v.Z }).ToArray(),
            SourcePoints = sources.Select(id => { var s = WindowFit.Transform(p, id)!.Value; return new[] { Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ }.Select(s.ToWorld).Select(t.ToLocal).Select(v => new[] { v.X, v.Y, v.Z }).ToArray(); }).ToArray() };
    }
    public static void Remap(IEnumerable<Actor> actors, IReadOnlyDictionary<string, string> renamed)
    {
        foreach (var a in actors)
        {
            if (a.Window is { } d) d.FrameCollisionSources = d.FrameCollisionSources.Where(renamed.ContainsKey).Select(id => renamed[id]).ToArray();
            if (a.Window?.Binding is not { } binding) continue;
            if (binding.Sources.All(renamed.ContainsKey)) binding.Sources = binding.Sources.Select(id => renamed[id]).ToArray();
            else a.Window.Binding = null;
        }
    }
}

/// <summary>Linked panes follow source edits as part of the original history entry, including merged drags.</summary>
public sealed class WindowDependentCommand(IEditCommand inner) : IEditCommand
{
    private List<IEditCommand>? effects;
    public string Name => inner.Name;
    public void Do(MapProject p)
    {
        inner.Do(p);
        if (effects is not null) { foreach (var e in effects) e.Do(p); return; }
        effects = [];
        foreach (var a in p.Actors.Where(a => a.Window?.Binding is not null).ToList())
        {
            var b = a.Window!.Binding!;
            if (b.Sources.Any(id => WindowFit.Transform(p, id) is null))
            {
                var delete = new DeleteObjectsCommand([ObjectRef.Actor(a.Id)]); delete.Do(p); effects.Add(delete); continue;
            }
            var after = a.Clone(); var d = after.Window!; var t = WindowFit.Transform(p, b.Sources[0])!.Value;
            var q = b.Points.Select(v => t.ToWorld(new Vec3(v[0], v[1], v[2]))).ToArray();
            Vec3 normal = Vec3.Cross(q[2] - q[0], q[3] - q[0]).Normalized();
            if (normal.Length < .5) { d.Binding!.NeedsRefit = true; }
            else
            {
                double normalScale = (q[1] - q[0]).Length;
                double normalRatio = b.NormalScale > 1e-9 ? normalScale / b.NormalScale : 1;
                d.Thickness *= normalRatio; d.DepthOffset *= normalRatio; d.Binding!.NormalScale = normalScale;
                after.Location = q[0]; after.Rotation = Orientation.FromAxes(normal, q[3] - q[0]); after.DrawScale = 1; after.DrawScale3D = Vec3.One; after.PrePivot = Vec3.Zero;
                d.Outline = WindowGeometry.Positive(q.Skip(4).Select(v => after.Rotation.Unrotate(v - after.Location)).Select(v => new WindowPoint(v.Y, v.Z)));
                d.Width = d.Outline.Max(v => v.Y) - d.Outline.Min(v => v.Y); d.Height = d.Outline.Max(v => v.Z) - d.Outline.Min(v => v.Z);
                d.Binding!.NeedsRefit = WindowFit.GeometryKey(p, b.Sources) != b.GeometryKey;
                if (b.SourcePoints.Length == b.Sources.Length)
                {
                    for (int source = 0; source < b.Sources.Length; source++)
                    {
                        var other = WindowFit.Transform(p, b.Sources[source])!.Value;
                        Vec3[] axes = [Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ];
                        for (int axis = 0; axis < Math.Min(4, b.SourcePoints[source].Length); axis++)
                        { var point = b.SourcePoints[source][axis]; if ((other.ToWorld(axes[axis]) - t.ToWorld(new Vec3(point[0], point[1], point[2]))).Length > .1) d.Binding.NeedsRefit = true; }
                    }
                }
            }
            if (ModelJson.ActorToJson(a).ToString() == ModelJson.ActorToJson(after).ToString()) continue;
            try { d.Validate(); WindowAssets.Prepare(after); }
            catch (ArgumentException) { after = a.Clone(); after.Window!.Binding!.NeedsRefit = true; }
            var edit = new ReplaceObjectsCommand("Keep glass fitted").Actor(a.Clone(), after); edit.Do(p); effects.Add(edit);
        }
    }
    public void Undo(MapProject p) { if (effects is not null) for (int i = effects.Count - 1; i >= 0; i--) effects[i].Undo(p); inner.Undo(p); }
    public bool TryMerge(IEditCommand next)
    {
        if (next is not WindowDependentCommand n || !inner.TryMerge(n.Inner)) return false;
        effects ??= []; if (n.effects is not null) effects.AddRange(n.effects); return true;
    }
    public IEditCommand Inner => inner;
}
