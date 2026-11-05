using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Places an actor of the chosen class where you click (P): on the surface under the cursor in the perspective view
/// (standing on floors, off walls), on the view plane at the selection's depth in the 2D views. The new actor is
/// selected, so W/E/R adjust it at once.
/// </summary>
public sealed class PlaceActorTool : IViewportTool
{
    private readonly ToolManager _tools;

    public PlaceActorTool(ToolManager tools) => _tools = tools;

    public string Id => "place";

    public string Title => "Place Actor";

    public string Shortcut => "P";

    /// <summary>The full class name to place: the catalog's name for the typed one, else the text as given
    /// (with an Engine. package when none is given).</summary>
    public static string ResolveClass(string typed)
    {
        string t = typed.Trim();
        if (EditorServices.Classes?.Find(t) is { } info)
        {
            return info.Name;
        }

        return t.Contains('.') ? t : "Engine." + t;
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press)
        {
            return false;
        }

        PlaceAt(view, press.Position);
        return true;
    }

    /// <summary>Places an actor of <see cref="ToolManager.PlaceClass"/> at a point of a view (also the context
    /// menu's "Place ... Here").</summary>
    public void PlaceAt(EditorViewport view, Vector2 screen)
    {
        string cls = ResolveClass(_tools.PlaceClass);
        if (EditorServices.Classes is { } catalog && catalog.Find(cls) is { } info && (info.Abstract || !info.Placeable))
        {
            _tools.Context.Log($"{info.Name} cannot be placed (abstract or not placeable).", Severity.Warning);
            return;
        }

        var actor = new Actor("", cls);
        ActorLook look = ActorLook.For(actor);
        actor.Id = new FreshIds(_tools.Context.Project).Next(actor.ShortClass);
        actor.Location = Placement.Point(_tools, view.Cam, screen, n => Placement.HalfAlong(look.HalfExtent, n));
        if (!view.Cam.IsOrtho)
        {
            // Face the way the camera looks, on the rotation grid.
            actor.Rotation = new Rot(0, _tools.SnapRotation(view.Cam.Yaw), 0).Normalized();
        }

        MoverBase.Place(actor);
        NewLights.Place(actor);

        var cmd = new AddObjectsCommand($"Place {actor.ShortClass}", actors: [actor]);
        _tools.Context.Execute(cmd);
        _tools.Context.Selection.Set(cmd.Added);
        _tools.Context.SetStatus($"Placed {actor.Id} at {actor.Location}");
    }

    public void BuildOptions(HBoxContainer bar)
    {
        ToolOptions.Name(bar, "Class");
        var edit = new LineEdit
        {
            Text = _tools.PlaceClass,
            CustomMinimumSize = new Vector2(200, 0),
            TooltipText = "Class to place: type a name and press Enter, or pick one from Classes",
            SelectAllOnFocus = true,
        };
        edit.TextSubmitted += t =>
        {
            _tools.PlaceClass = ResolveClass(t);
            edit.Text = _tools.PlaceClass;
            edit.ReleaseFocus();
        };
        bar.AddChild(edit);

        if (EditorServices.Classes is { } catalog)
        {
            List<string> names = catalog.All.Where(c => c.Placeable && !c.Abstract).Select(c => c.Name).ToList();
            var pick = new Button
            {
                Text = "Classes",
                TooltipText = "Pick the class to place from a searchable list",
                Icon = Icons.Get("chevron-down", EditorTheme.TextDim),
                IconAlignment = HorizontalAlignment.Right,
                FocusMode = Control.FocusModeEnum.None,
            };
            pick.Pressed += () => ClassPicker.Open(pick, names, _tools.PlaceClass, name =>
            {
                _tools.PlaceClass = name;
                edit.Text = name;
            });
            bar.AddChild(pick);
        }

        ToolOptions.Hint(bar, "Click in a view to place one", "The new actor is selected, so W, E and R adjust it");
    }
}
