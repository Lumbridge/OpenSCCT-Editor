using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Stairs;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>Dimension handles edit the authored staircase; no constituent brush is ever stretched.</summary>
public sealed class StairHandles
{
    private readonly ToolManager tools;
    public StairHandles(ToolManager manager)
    {
        tools = manager;
        tools.DragInterrupted += () => { _drag = null; _edited = false; };
    }
    private sealed record Handle(string Name, Vec3 At, Vec3 Axis, double Value, Action<StairDesign, double> Set);
    private sealed record Drag(Brush Start, Handle Handle, EditorViewport View, Vector2 Mouse, Vector2 Direction, double UnitsPerPixel, string Merge);
    private Drag? _drag;
    private bool _edited;
    private Brush? Selected => tools.Context.Selection.Items is { Count: 1 } refs ? tools.Context.Project.FindBrush(refs[0].Id) is { Stair: not null } b ? b : null : null;
    private static IEnumerable<Handle> Of(Brush b)
    {
        StairDesign s = b.Stair!; var last = StairGeometry.Steps(s)[^1]; Vec3 top = (last.C + last.D) / 2 + Vec3.UnitZ * s.Rise;
        yield return new("Width", new Vec3(0, s.Width / 2, 0), Vec3.UnitY, s.Width, (d, v) => { d.Width = v; d.LandingLength = Math.Max(d.LandingLength, v); });
        yield return new("Rise", top, Vec3.UnitZ, s.Rise, (d, v) => d.Rise = v);
        if (s.Layout is "straight" or "l" or "u") yield return new("Run", new Vec3(s.Run, 0, 0), Vec3.UnitX, s.Run, (d, v) => d.Run = v);
        if (s.Layout is "l" or "u") yield return new("Landing", new Vec3(s.Run / 2 + s.LandingLength, 0, s.Rise / 2), Vec3.UnitX, s.LandingLength, (d, v) => d.LandingLength = v);
        if (s.Layout is "curved" or "spiral") yield return new("Radius", new Vec3(0, -s.Width / 2, s.Rise / 2), -Vec3.UnitY, s.InnerRadius, (d, v) => d.InnerRadius = v);
    }
    public bool Input(EditorViewport view, InputEvent e)
    {
        if (_drag is { } drag)
        {
            if (e is InputEventMouseMotion motion)
            {
                double value = tools.SnapValue(drag.Handle.Value + (motion.Position - drag.Mouse).Dot(drag.Direction) * drag.UnitsPerPixel);
                if (drag.Handle.Name == "Width") value = tools.SnapValue(drag.Handle.Value + (motion.Position - drag.Mouse).Dot(drag.Direction) * drag.UnitsPerPixel * 2);
                try
                {
                    Brush next = drag.Start.Clone(); drag.Handle.Set(next.Stair!, value); StairGeometry.Rebuild(next);
                    Brush current = tools.Context.Project.FindBrush(next.Id)!;
                    tools.DragStep(() => tools.Context.Execute(new ReplaceObjectsCommand("Resize staircase " + drag.Handle.Name, drag.Merge).Brush(current.Clone(), next)));
                    _edited = true; tools.Context.SetStatus($"{drag.Handle.Name}: {value:0.##}");
                }
                catch (ArgumentException ex) { tools.Context.SetStatus(ex.Message); }
                view.Redraw(); return true;
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            { tools.Context.Session.Commands.EndMerge(); _drag = null; return true; }
            return true;
        }
        if (Selected is not { } owner || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return false;
        foreach (Handle h in Of(owner))
        {
            Vec3 world = owner.LocalToWorld(h.At);
            if (view.Cam.ToScreen(world) is not { } screen || screen.DistanceTo(press.Position) > 10) continue;
            if (view.Cam.ToScreen(owner.LocalToWorld(h.At + h.Axis * 100)) is not { } end || end.DistanceTo(screen) < 1) continue;
            _drag = new Drag(owner.Clone(), h, view, press.Position, (end - screen).Normalized(), 100 / end.DistanceTo(screen), Guid.NewGuid().ToString("N"));
            _edited = false; return true;
        }
        return false;
    }
    public bool Cancel()
    {
        if (_drag is null) return false;
        if (_edited) tools.Context.Session.Commands.Undo(); tools.Context.Session.Commands.EndMerge(); _drag = null; return true;
    }
    public void Draw(EditorViewport view, Control canvas)
    {
        if (Selected is not { } b) return;
        foreach (Handle h in Of(b)) if (view.Cam.ToScreen(b.LocalToWorld(h.At)) is { } screen)
        {
            canvas.DrawCircle(screen, 7, Colors.Orange); canvas.DrawCircle(screen, 7, Colors.Black, false, 1);
            canvas.DrawString(ThemeDB.FallbackFont, screen + new Vector2(10, -8), $"{h.Name} {h.Value:0.#}", fontSize: 13, modulate: Colors.Orange);
        }
    }
}
