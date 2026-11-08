using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Doors;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

public sealed class DoorHandles
{
    private readonly ToolManager tools;
    private sealed record Drag(Actor Start, bool Width, EditorViewport View, DimensionDrag Sizing, string Merge);
    private Drag? drag;
    private bool edited;
    public bool IsDragging => drag is not null;
    public DoorHandles(ToolManager manager) { tools = manager; tools.DragInterrupted += () => { drag = null; edited = false; }; }
    private Actor? Selected => tools.Context.Selection.Items is { Count: 1 } refs && tools.Context.Project.FindActor(refs[0].Id) is { Door: not null } actor && MoverBase.ShownKey(actor) is null ? actor : null;
    private static IEnumerable<(Vec3 At, bool Width)> Handles(Actor actor)
    {
        var d = actor.Door!; yield return (new Vec3(0, d.Width / 2, 0), true);
        yield return (new Vec3(0, 0, d.Height / 2), false);
    }
    public bool Input(EditorViewport view, InputEvent e)
    {
        if (drag is { } active)
        {
            if (e is InputEventMouseMotion motion)
            {
                var start = active.Start; var d = start.Door!;
                double value = active.Sizing.Value(motion.Position, motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt), active.Width ? 48 : 64);
                try
                {
                    var next = start.Clone(); var closed = DoorGeometry.Closed(start);
                    next.Door!.Width = active.Width ? value : d.Width; next.Door.Height = active.Width ? d.Height : value;
                    Vec3 centre = closed.LocalToWorld(Vec3.Zero) + Vec3.UnitZ * ((next.Door.Height - d.Height) / 2);
                    DoorCommands.Configure(next, centre, closed.Rotation);
                    var current = tools.Context.Project.FindActor(next.Id)!;
                    if (next.Door!.ToJson() == current.Door!.ToJson()) return true;
                    tools.DragStep(() => tools.Context.Execute(new ReplaceObjectsCommand("Resize door", active.Merge).Actor(current.Clone(), next)));
                    edited = true; tools.Context.SetStatus($"Door: {next.Door!.Width:0.##} × {next.Door.Height:0.##}"); view.Redraw();
                }
                catch (ArgumentException ex) { active.Sizing.Snap.Release(); tools.Context.SetStatus(ex.Message); }
                return true;
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) { drag = null; tools.FinishDrag(); return true; }
            return true;
        }
        if (Selected is not { } actor || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return false;
        foreach (var h in Handles(actor))
        {
            Vec3 axis = h.Width ? Vec3.UnitY : Vec3.UnitZ;
            if (view.Cam.ToScreen(actor.LocalToWorld(h.At)) is not { } at || at.DistanceTo(press.Position) > 10 || view.Cam.ToScreen(actor.LocalToWorld(h.At + axis * 100)) is not { } end || at.DistanceTo(end) < 1) continue;
            Vec3 point = actor.LocalToWorld(h.At), unit = actor.LocalToWorld(h.At + axis) - point;
            var sizing = new DimensionDrag(tools, view, press.Position, point, unit, h.Width ? actor.Door!.Width : actor.Door!.Height, h.Width ? 2 : 1, ObjectRef.Actor(actor.Id), Math.Max(actor.Door!.Width, actor.Door.Height) / 2);
            drag = new(actor.Clone(), h.Width, view, sizing, Guid.NewGuid().ToString("N")); edited = false; return true;
        }
        return false;
    }
    public void Draw(EditorViewport view, Control canvas)
    {
        if (Selected is not { } actor) return;
        foreach (var h in Handles(actor)) if (view.Cam.ToScreen(actor.LocalToWorld(h.At)) is { } at)
        { canvas.DrawCircle(at, 7, Colors.Orange); canvas.DrawCircle(at, 7, Colors.Black, false, 1); canvas.DrawString(ThemeDB.FallbackFont, at + new Vector2(10, -8), $"{(h.Width ? "Width" : "Height")} {(h.Width ? actor.Door!.Width : actor.Door!.Height):0.#}", fontSize: 13, modulate: Colors.Orange); }
        drag?.Sizing.Snap.Draw(view, canvas);
    }
    public bool Cancel() { if (drag is null) return false; if (edited) tools.Context.Session.Commands.Undo(); tools.Context.Session.Commands.EndMerge(); drag = null; return true; }
}
