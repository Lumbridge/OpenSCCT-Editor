using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Gameplay;

namespace OpenScctEditor.Tools;

/// <summary>
/// Click and box selection, shared by the select tool and the transform tools. Click picks one object (Shift adds,
/// Ctrl toggles); dragging on empty space draws a box that selects what is fully inside it (actors by location).
/// Double-click frames the clicked object in every view.
/// </summary>
public sealed class ClickSelect
{
    private const float DragThreshold = 4;
    private readonly ToolManager _tools;
    private Vector2? _press;
    private Vector2 _current;
    private bool _boxing;
    private EditorViewport? _view;

    public ClickSelect(ToolManager tools) => _tools = tools;

    public bool IsBoxing => _boxing;

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } dbl:
                if (Picking.Pick(view.Cam, _tools.Context.Project, dbl.Position) is { } hit)
                {
                    _tools.Context.Selection.Set([hit]);
                    _tools.Context.Focus([hit]);
                }

                _press = null;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _press = press.Position;
                _current = press.Position;
                _boxing = false;
                _view = view;
                return true;
            case InputEventMouseMotion motion when _press is { } start && _view == view:
                _current = motion.Position;
                if (!_boxing && start.DistanceTo(_current) > DragThreshold)
                {
                    _boxing = true;
                }

                if (_boxing)
                {
                    view.Redraw();
                }

                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _press is { } start && _view == view:
                _press = null;
                if (_boxing)
                {
                    _boxing = false;
                    Apply(Picking.InRect(view.Cam, _tools.Context.Project, RectOf(start, release.Position)), release, box: true);
                    view.Redraw();
                }
                else
                {
                    ObjectRef? picked = Picking.Pick(view.Cam, _tools.Context.Project, release.Position);
                    Apply(picked is { } h ? [h] : [], release, box: false);
                }

                return true;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (_boxing && _press is { } start && _view == view)
        {
            Rect2 r = RectOf(start, _current);
            canvas.DrawRect(r, new Color(EditorTheme.Accent, 0.12f));
            canvas.DrawRect(r, EditorTheme.Accent, filled: false, width: 1);
        }
    }

    public bool Cancel()
    {
        if (!_boxing && _press is null)
        {
            return false;
        }

        _boxing = false;
        _press = null;
        _view?.Redraw();
        return true;
    }

    private void Apply(List<ObjectRef> hits, InputEventWithModifiers mods, bool box)
    {
        var sel = _tools.Context.Selection;
        if (mods.ShiftPressed)
        {
            sel.Add(hits);
        }
        else if (mods.IsCommandOrControlPressed())
        {
            if (box)
            {
                sel.Remove(hits);
            }
            else
            {
                hits.ForEach(sel.Toggle);
            }
        }
        else
        {
            sel.Set(hits);
        }

        _tools.Context.SetStatus(sel.Count == 0 ? "Nothing selected" : sel.Count == 1 ? $"Selected {sel.Primary}" : $"{sel.Count} selected");
    }

    private static Rect2 RectOf(Vector2 a, Vector2 b) => new Rect2(a, Vector2.Zero).Expand(b);
}

/// <summary>Click/box selection with adjustment handles for selected GEs and stairs.</summary>
public sealed class SelectTool : IViewportTool
{
    private readonly ClickSelect _select;
    private readonly ToolManager _tools;
    private readonly StairHandles _stairs;
    private readonly BrushSideHandles _sides;

    public SelectTool(ToolManager tools) { _tools = tools; _select = new ClickSelect(tools); _stairs = new StairHandles(tools); _sides = new BrushSideHandles(tools); }
    private GeDrawTool? GeHandles => _tools.Tools.OfType<GeDrawTool>().FirstOrDefault();

    public string Id => "select";

    public string Title => "Select";

    public string Shortcut => "S";

    public void Activate() => GeHandles?.Activate();

    public bool Input(EditorViewport view, InputEvent e) => _stairs.Input(view, e) || _sides.Input(view, e) || GeHandles?.InputSelected(view, e) == true || _select.Input(view, e);

    public void Draw(EditorViewport view, Control canvas) { _select.Draw(view, canvas); GeHandles?.DrawSelected(view, canvas); _stairs.Draw(view, canvas); _sides.Draw(view, canvas); }

    public bool IsDragging => _stairs.IsDragging || _sides.IsDragging || GeHandles?.IsDragging == true;
    public bool Cancel() => _stairs.Cancel() || _sides.Cancel() || GeHandles?.Cancel() == true || _select.Cancel();
    public void Deactivate() { _stairs.Cancel(); _sides.Cancel(); _select.Cancel(); GeHandles?.Deactivate(); }
}
