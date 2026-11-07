using WorldGeometry = OpenScct.Editor.Core.Scripting.WorldGeometry;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// The Placement menu (RE+ placement tools): drop to floor (End), align to the floor's slope (Shift+End), drop to
/// ceiling (Ctrl+End), snap to grid, mirror, copies along an axis, replace class, and select same class / material /
/// Tag. Drops cast against the brushes and an imported map's baked collision.
/// </summary>
[ShellPart(Order = -23)]
public sealed class PlacementActions : IShellPart
{
    private EditorContext _ctx = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        bool HasActors() => context.Selection.Items.Any(r => r.Kind == ObjectKind.Actor);
        bool HasAny() => context.Selection.Count > 0;

        Add("place.dropToFloor", "Drop to Floor", 100, () => Drop(ceiling: false, align: false), HasActors, "End");
        Add("place.alignToFloor", "Drop and Align to Slope", 110, () => Drop(ceiling: false, align: true), HasActors, "Shift+End");
        Add("place.dropToCeiling", "Drop to Ceiling", 120, () => Drop(ceiling: true, align: false), HasActors, "Ctrl+End");
        Add("place.snapToGrid", "Snap to Grid", 200, SnapToGrid, HasAny, null, "Snap pivots to the grid");
        for (int axis = 0; axis < 3; axis++)
        {
            int a = axis;
            string n = "XYZ"[axis].ToString();
            Add("place.mirror" + n, "Mirror " + n, 300 + axis, () => Mirror(a), HasAny, null, $"Mirror the selection across its centre along {n}");
            Add("place.array" + n, $"Copies along {n}", 400 + axis, () => Array(a), HasAny, null,
                $"{ArrayCount} copies, each a selection width (or a grid step) further along +{n}");
        }

        Add("place.replaceClass", "Replace Class with Place Class", 500, ReplaceClass, HasActors, null,
            "Change the selected actors to the class the place tool uses (pick it in the Classes panel); properties the new class lacks are dropped");
        Add("place.selectSameMaterial", "Select Same Material", 600, SelectSameMaterial,
            () => GeometrySelection.Instance.Faces.Count > 0 || context.Selection.Items.Any(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)), null,
            "Brushes using the picked face's (or first selected brush's) material");
        Add("place.selectSameTag", "Select Same Tag", 610, SelectSameTag, HasActors, null, "Actors with the primary actor's Tag");
    }

    /// <summary>How many copies the array actions make.</summary>
    public static int ArrayCount
    {
        get => Math.Clamp(AppSettings.GetInt("tools", "array_count", 3), 1, 1000);
        set => AppSettings.Set("tools", "array_count", value);
    }

    private void Add(string id, string title, int order, Action run, Func<bool> enabled, string? shortcut, string? description = null) =>
        _ctx.AddAction(new EditorAction(id, title, "Placement", run)
        {
            Order = order,
            IsEnabled = enabled,
            Shortcut = shortcut,
            Description = description,
        });

    private void Drop(bool ceiling, bool align)
    {
        MapProject p = _ctx.Project;
        var world = new WorldGeometry(p, _ctx.Session.Folder);
        var cmd = new ReplaceObjectsCommand(ceiling ? "Drop to ceiling" : align ? "Drop and align to slope" : "Drop to floor");
        var missed = new List<string>();
        foreach (ObjectRef r in _ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor))
        {
            if (p.FindActor(r.Id) is not { } a)
            {
                continue;
            }

            double half = ActorPlacement.HalfHeight(a, fallback: ObjectGeometry.ActorHalfExtent(a, ActorLook.For(a)).Z);
            if (ActorPlacement.Drop(world, a.Location, half, ceiling) is not { } hit)
            {
                missed.Add(a.Id);
                continue;
            }

            Actor after = a.Clone();
            after.Location = hit.Location;
            if (align)
            {
                after.Rotation = Orientation.AlignUp(a.Rotation, hit.Normal);
            }

            MoverBase.Follow(a, after);
            cmd.Actor(a.Clone(), after);
        }

        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
        }

        _ctx.SetStatus(missed.Count == 0 ? cmd.Name : $"{cmd.Name}: nothing {(ceiling ? "above" : "below")} {string.Join(", ", missed)}");
    }

    private void SnapToGrid()
    {
        double grid = ToolManager.Instance?.GridSize ?? 16;
        Run(SelectionEdit.SnapToGrid(_ctx.Project, _ctx.Selection.Items, grid));
    }

    private void Mirror(int axis)
    {
        Vec3 pivot = SelectionEdit.PivotCentre(_ctx.Project, _ctx.Selection.Items);
        Run(SelectionEdit.Mirror(_ctx.Project, _ctx.Selection.Items, axis, pivot));
    }

    private void Array(int axis)
    {
        double width = ObjectGeometry.Bounds(_ctx.Project, _ctx.Selection.Items) is { } b ? b.Max[axis] - b.Min[axis] : 0;
        double step = width > 1 ? width : ToolManager.Instance?.GridSize ?? 16;
        var cmd = SelectionEdit.Array(_ctx.Project, _ctx.Selection.Items.ToList(), ArrayCount, Vec3.Zero.With(axis, step), out Finding? note);
        _ctx.Execute(cmd);
        if (note is not null)
        {
            _ctx.Log(note.Message, note.Severity);
        }

        _ctx.Selection.Add(cmd.Added);
        _ctx.SetStatus($"{cmd.Name}, step {step:0.##}");
    }

    private void ReplaceClass()
    {
        string cls = ToolManager.Instance?.PlaceClass ?? "";
        try
        {
            Run(SelectionEdit.ReplaceClass(_ctx.Project, _ctx.Selection.Items, cls));
        }
        catch (ArgumentException ex)
        {
            _ctx.Log(ex.Message, Severity.Error);
            _ctx.SetStatus(ex.Message);
        }
    }

    private void SelectSameMaterial()
    {
        MapProject p = _ctx.Project;
        string? material = GeometrySelection.Instance.PrimaryFace is { } f && FaceEdit.Find(p, f) is { } face
            ? face.Material
            : _ctx.Selection.Items.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)).Select(r => p.FindBrush(r.Id)).OfType<Brush>()
                .SelectMany(b => b.Shape.Faces).Select(x => x.Material).FirstOrDefault();
        _ctx.Selection.Set(p.Brushes
            .Where(b => ObjectFilter.IsPickable(p, b.Editor) && b.Shape.Faces.Any(x => string.Equals(x.Material, material, StringComparison.OrdinalIgnoreCase)))
            .Select(b => ObjectRef.ForBrush(b)));
        _ctx.SetStatus($"{_ctx.Selection.Count} brushes use {material ?? "the default material"}");
    }

    private void SelectSameTag()
    {
        if (_ctx.Selection.Items.LastOrDefault(r => r.Kind == ObjectKind.Actor) is not { Id: { } id } || _ctx.Project.FindActor(id) is not { } a)
        {
            return;
        }

        string tag = a.Tag;
        _ctx.Selection.Set(_ctx.Project.Actors
            .Where(x => ObjectFilter.IsPickable(_ctx.Project, x.Editor) && string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .Select(x => ObjectRef.Actor(x.Id)));
        _ctx.SetStatus($"{_ctx.Selection.Count} actors tagged {tag}");
    }

    private void Run(ReplaceObjectsCommand cmd)
    {
        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
            _ctx.SetStatus(cmd.Name);
        }
    }
}
