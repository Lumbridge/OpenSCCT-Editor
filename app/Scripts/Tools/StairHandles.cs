using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Stairs;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>Dimension and clearance handles edit the authored staircase with grid and neighbour snapping.</summary>
public sealed class StairHandles
{
    private readonly ToolManager tools;
    public StairHandles(ToolManager manager) { tools = manager; tools.DragInterrupted += () => { _drag = null; _edited = false; }; }
    private sealed record Handle(string Name, Vec3 At, Vec3 Axis, double Value, Action<StairDesign, double> Set,
        double Multiplier = 1, double Minimum = 0, bool Clearance = false);
    private sealed record Drag(Brush Start, Handle Handle, EditorViewport View, DimensionDrag Sizing, string Merge);
    private Drag? _drag;
    private bool _edited;
    private Brush? _clearanceOwner;
    private StairGeometry.Generated? _clearance;
    private Brush? Selected => tools.Context.Selection.Items is { Count: 1 } refs ? tools.Context.Project.FindBrush(refs[0].Id) is { Stair: not null } b ? b : null : null;
    private static IEnumerable<Handle> Of(Brush b)
    {
        StairDesign s = b.Stair!; var steps = StairGeometry.Steps(s); var last = steps[^1];
        Vec3 end = (last.C + last.D) / 2, forward = (last.C + last.D - last.A - last.B).Normalized();
        yield return new("Width", new(0, s.Width / 2, 0), Vec3.UnitY, s.Width, (d, v) => { d.Width = v; d.LandingLength = Math.Max(d.LandingLength, v); }, 2, 32);
        yield return new("Rise", end + Vec3.UnitZ * s.Rise, Vec3.UnitZ, s.Rise, (d, v) => d.Rise = v, Minimum: 1);
        if (s.Layout is "straight" or "l" or "u")
        {
            double share = s.Layout == "straight" ? 1 : (s.Count / 2) / (double)s.Count;
            yield return new("Run", new(s.Run * share, 0, 0), Vec3.UnitX, s.Run, (d, v) => d.Run = v, 1 / share, 32);
        }
        if (s.Layout is "l" or "u") yield return new("Landing", new(s.Run * (s.Count / 2) / s.Count + s.LandingLength, 0, s.Rise / 2), Vec3.UnitX, s.LandingLength, (d, v) => d.LandingLength = Math.Max(d.Width, v), Minimum: s.Width);
        if (s.Layout is "curved" or "spiral")
        {
            double angle = Math.Min(s.TurnDegrees / 2, 180) * Math.PI / 180, sign = s.Left ? -1 : 1;
            Vec3 derivative = new(Math.Sin(angle), sign * (1 - Math.Cos(angle)), 0);
            Vec3 at = new(s.InnerRadius * Math.Sin(angle), sign * (s.InnerRadius + s.Width / 2 - s.InnerRadius * Math.Cos(angle)), s.Rise / 2);
            yield return new("Radius", at, derivative.Normalized(), s.InnerRadius, (d, v) => d.InnerRadius = v, 1 / derivative.Length, 16);
        }
        if (!s.Carve) yield break;
        yield return new("Headroom", end + Vec3.UnitZ * (s.Rise + s.Headroom), Vec3.UnitZ, s.Headroom, (d, v) => d.Headroom = v, Minimum: 1, Clearance: true);
        yield return new("Clearance sides", new(0, s.Width / 2 + s.ClearanceMargin, s.Headroom / 2), Vec3.UnitY, s.ClearanceMargin, (d, v) => d.ClearanceMargin = v, Clearance: true);
        yield return new("Clearance end", end + forward * s.ClearanceEnd + Vec3.UnitZ * (s.Rise + s.Headroom), forward, s.ClearanceEnd, (d, v) => d.ClearanceEnd = v, Clearance: true);
        var first = steps[0]; Vec3 start = (first.A + first.B) / 2, back = -(first.C + first.D - first.A - first.B).Normalized();
        yield return new("Clearance start", start + back * s.ClearanceStart + Vec3.UnitZ * (first.Top + s.Headroom), back, s.ClearanceStart, (d, v) => d.ClearanceStart = v, Clearance: true);
    }
    private static IEnumerable<(Handle Handle, Vector2 Screen, Vector2 Origin)> ScreenHandles(Brush owner, EditorViewport view)
    {
        var used = new List<Vector2>();
        foreach (Handle h in Of(owner))
        {
            if (view.Cam.ToScreen(owner.LocalToWorld(h.At)) is not { } origin) continue;
            Vector2 screen = origin;
            if (h.Name == "Clearance end" && view.Cam.ToScreen(owner.LocalToWorld(h.At + h.Axis * 100)) is { } end && end.DistanceTo(origin) > 1)
                screen += (end - origin).Normalized() * 24;
            int n = 0;
            while (used.Any(p => p.DistanceTo(screen) < 18) && n++ < 8) screen += new Vector2(18, -18);
            used.Add(screen); yield return (h, screen, origin);
        }
    }
    public bool Input(EditorViewport view, InputEvent e)
    {
        if (_drag is { } drag)
        {
            if (e is InputEventMouseMotion motion)
            {
                double value = drag.Sizing.Value(motion.Position, motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt), drag.Handle.Minimum);
                if (Math.Abs(value - drag.Handle.Value) < 1e-7 && !_edited) return true;
                try
                {
                    Brush current = tools.Context.Project.FindBrush(drag.Start.Id)!;
                    Brush next = drag.Start.Clone(); drag.Handle.Set(next.Stair!, value);
                    if (next.Stair!.ToJson() == current.Stair!.ToJson()) return true;
                    if (drag.Handle.Clearance) next.Stair.Validate();
                    else StairGeometry.Rebuild(next);
                    tools.DragStep(() => tools.Context.Execute(new ReplaceObjectsCommand("Resize staircase " + drag.Handle.Name, drag.Merge).Brush(current.Clone(), next)));
                    _edited = true; tools.Context.SetStatus($"{drag.Handle.Name}: {value:0.##}{drag.Sizing.Snap.Status()}");
                }
                catch (ArgumentException ex) { drag.Sizing.Snap.Release(); tools.Context.SetStatus(ex.Message); }
                view.Redraw(); return true;
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            { _drag = null; tools.FinishDrag(); return true; }
            return true;
        }
        if (Selected is not { } owner || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return false;
        foreach (var shown in ScreenHandles(owner, view))
        {
            Handle h = shown.Handle;
            Vec3 world = owner.LocalToWorld(h.At), unit = owner.LocalToWorld(h.At + h.Axis) - world;
            if (shown.Screen.DistanceTo(press.Position) > 10) continue;
            Vector2 screen = shown.Origin;
            if (view.Cam.ToScreen(world + unit * 100) is not { } end || end.DistanceTo(screen) < 1) continue;
            var sizing = new DimensionDrag(tools, view, press.Position, world, unit, h.Value, h.Multiplier, ObjectRef.Stair(owner.Id), owner.Stair!.Width / 2);
            _drag = new(owner.Clone(), h, view, sizing, Guid.NewGuid().ToString("N"));
            _edited = false; return true;
        }
        return false;
    }
    public bool Cancel()
    {
        if (_drag is null) return false;
        if (_edited) tools.Context.Session.Commands.Undo(); tools.Context.Session.Commands.EndMerge(); _drag = null; return true;
    }
    public bool IsDragging => _drag is not null;
    public void Draw(EditorViewport view, Control canvas)
    {
        if (Selected is not { } b) return;
        if (b.Stair!.Carve)
        {
            if (!ReferenceEquals(b, _clearanceOwner)) { _clearanceOwner = b; _clearance = StairGeometry.Generate(b); }
            foreach (var face in _clearance!.Cuts.SelectMany(c => c.WorldFaces()).Where(f => f.Normal().Z > .1))
                for (int i = 0; i < face.Vertices.Count; i++)
                    if (view.Cam.ToScreen(face.Vertices[i]) is { } a && view.Cam.ToScreen(face.Vertices[(i + 1) % face.Vertices.Count]) is { } c) canvas.DrawLine(a, c, Colors.MediumPurple, 1);
        }
        foreach (var shown in ScreenHandles(b, view))
        {
            Handle h = shown.Handle; Vector2 screen = shown.Screen;
            var colour = h.Clearance ? Colors.MediumPurple : Colors.Orange;
            if (screen.DistanceTo(shown.Origin) > 1) canvas.DrawLine(shown.Origin, screen, colour, 1);
            canvas.DrawCircle(screen, 7, colour); canvas.DrawCircle(screen, 7, Colors.Black, false, 1);
            canvas.DrawString(ThemeDB.FallbackFont, screen + new Vector2(10, -8), $"{h.Name} {h.Value:0.#}", fontSize: 13, modulate: colour);
        }
        _drag?.Sizing.Snap.Draw(view, canvas);
    }
}
