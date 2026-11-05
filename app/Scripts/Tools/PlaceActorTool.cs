using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Places an actor of the chosen class where you click (P): on the surface under the cursor in the perspective view
/// (standing on floors, off walls), on the view plane at the selection's depth in the 2D views. Armed by a placeables
/// palette tile (cameras, lasers, objectives...: <see cref="ToolManager.PlaceFromPalette"/>), it goes by the palette's
/// rule instead (<see cref="PlaceableLanding"/>): a faint copy shows where it would go as the pointer moves, and the
/// click also makes the links the selection asks for and says what still needs wiring. A class typed or picked in the
/// tool places the plain way. The new actor is selected, so W/E/R adjust it at once.
/// </summary>
public sealed class PlaceActorTool : IViewportTool
{
    private readonly ToolManager _tools;
    private EditorViewport? _view;
    private Vector2 _mouse;
    private (Placeable Entry, PlaceableSpot Spot)? _ghost;

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

    /// <summary>The palette entry of the class to place when a palette tile armed the tool, else null.</summary>
    private Placeable? Palette => _tools.PlaceFromPalette ? Placeables.Find(ResolveClass(_tools.PlaceClass), EditorServices.Classes) : null;

    /// <summary>Builds the collision world in the background, so the first hover of a palette class does not wait.</summary>
    public void Activate()
    {
        if (Palette is not null)
        {
            GameplayState.Instance.Prewarm();
        }
    }

    public void Deactivate() => SetGhost(null, null);

    public bool Cancel()
    {
        SetGhost(null, null);
        return false;
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            _view = view;
            _mouse = motion.Position;
            if (Palette is { } entry)
            {
                SetGhost(entry, PlaceableLanding.Find(view, motion.Position, entry, wait: false).Spot);
            }
            else
            {
                SetGhost(null, null);
            }

            return false;
        }

        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press)
        {
            return false;
        }

        PlaceAt(view, press.Position);
        return true;
    }

    /// <summary>The pointer left the view: the preview goes until it is back over one.</summary>
    public void PointerLeft(EditorViewport view)
    {
        if (view == _view)
        {
            SetGhost(null, null);
        }
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (view == _view && Palette is { } entry)
        {
            SetGhost(entry, PlaceableLanding.Find(view, _mouse, entry, wait: false).Spot);
        }
        if (view == _view && _ghost is { } g)
        {
            PlaceableLanding.Draw(view.Cam, canvas, g.Entry, g.Spot);
            ViewTags.Beside(canvas, _mouse, [new TagPart($"Click to place: {g.Entry.Title} on {g.Spot.On}", EditorTheme.Text)]);
        }
    }

    /// <summary>Moves the hover preview of a palette class: a faint copy of its mesh, and its mark in the overlay.</summary>
    private void SetGhost(Placeable? entry, PlaceableSpot? spot)
    {
        (Placeable, PlaceableSpot)? next = entry is not null && spot is { } s ? (entry, s) : null;
        if (next == _ghost)
        {
            return;
        }

        _ghost = next;
        PlaceableLanding.ShowGhost(entry, spot);
        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>Places an actor of <see cref="ToolManager.PlaceClass"/> at a point of a view (also the context
    /// menu's "Place ... Here").</summary>
    public void PlaceAt(EditorViewport view, Vector2 screen)
    {
        if (Palette is { } entry)
        {
            (PlaceableSpot? spot, string why) = PlaceableLanding.Find(view, screen, entry, wait: true);
            if (spot is { } at)
            {
                PlaceableLanding.Place(_tools.Context, entry, at, view.Kind);
            }
            else
            {
                _tools.Context.SetStatus("Not placed: " + why);
            }

            return;
        }

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
        if (Palette is { } palette)
        {
            bar.AddChild(new TextureRect
            {
                Texture = ActorIcons.For(Placeables.Kind(palette, EditorServices.Classes), EditorTheme.Text),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                TooltipText = $"{palette.Title} ({palette.Class}), from the placeables palette",
                MouseFilter = Control.MouseFilterEnum.Pass,
            });
        }

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
            bool palette = _tools.PlaceFromPalette;
            _tools.PlaceClass = ResolveClass(t);
            edit.Text = _tools.PlaceClass;
            edit.ReleaseFocus();
            if (palette)
            {
                SetGhost(null, null);
                _tools.RefreshOptions();
            }
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
                bool palette = _tools.PlaceFromPalette;
                _tools.PlaceClass = name;
                edit.Text = name;
                if (palette)
                {
                    SetGhost(null, null);
                    _tools.RefreshOptions();
                }
            });
            bar.AddChild(pick);
        }

        if (Palette is { } entry)
        {
            ToolOptions.Action(bar, _tools.Context, PlaceablesActions.Open, "Palette").Icon = Icons.Get("palette", EditorTheme.Text);
            string wiring = PlaceableWiring.Hint(entry, EditorServices.Classes);
            if (wiring.Length > 0)
            {
                ToolOptions.Hint(bar, $"Click in a view to place {entry.InSentence}", wiring, "Shift: free placement; W/E/R adjust after placing");
            }
            else
            {
                ToolOptions.Hint(bar, $"Click in a view to place {entry.InSentence}", "Shift: free placement; W/E/R adjust after placing");
            }
            return;
        }

        ToolOptions.Hint(bar, "Click in a view to place one", "The new actor is selected, so W, E and R adjust it");
    }
}
