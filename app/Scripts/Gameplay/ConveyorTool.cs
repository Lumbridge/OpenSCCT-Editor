using Godot;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The conveyor tool (K). Drawing: click points on floors to draw a native conveyor's path (each point lifted for the
/// chosen look), Enter to finish, a click on the first point to close a loop, Backspace to take the last point back and
/// Escape to drop the drawing. Corners take the toolbar's radius. Editing the shown conveyors' points (the selected
/// ones, else every visible one): drag a point to move it, click an open conveyor's end to draw on from it,
/// Shift+click a point or the path to start a branch there, Ctrl+click the path to insert a point, Alt+click a point to
/// delete it and R over a point to give it the toolbar's radius. Each shown conveyor has a width handle at the edge of
/// its strip and a speed arrow at the middle of its path: drag them to set its width and speed (dragging the arrow
/// back past the middle reverses the belt). New conveyors take the toolbar's look, width and speed; the Inspector edits
/// everything later. While the tool is active the shown conveyors run as in the game: arrows and items move (items
/// queue and pass on to the conveyor their Next names), and in the textured views the belt's textures slide and an
/// escalator's steps travel. The run starts over after an edit to where the conveyors lie or what they carry; a change
/// of speed, running or width (the handles, the Inspector) carries on from where each belt is.
/// </summary>
public sealed class ConveyorTool : IViewportTool
{
    private const string Section = "tools";

    /// <summary>A click this close (flat) to the first point closes the loop.</summary>
    private const double CloseDistance = 32;

    /// <summary>How near (pixels) the cursor must be to a point or the path to pick it.</summary>
    private const float PickPixels = 8;

    /// <summary>How far (pixels) the mouse moves before a press on a point becomes a drag.</summary>
    private const float DragPixels = 4;

    /// <summary>Seconds between frames of the moving preview.</summary>
    private const double FrameTime = 1.0 / 30;

    /// <summary>The speed arrow is as long as the belt moves in this many seconds.</summary>
    private const double SpeedArrowSeconds = 0.5;

    private const int MinWidth = 16;
    private const int MaxWidth = 2048;
    private const int MaxSpeed = 2000;

    private readonly ToolManager _tools;
    private readonly GameplayState _state;
    private readonly List<ConveyorPoint> _points = [];
    private readonly List<ConveyorItemPlace> _items = [];
    private EditorViewport? _view;
    private Vector2 _mouse;
    private Vec3? _hover;

    /// <summary>The conveyor being drawn on from one of its ends; <c>Reversed</c> when drawing from its start.</summary>
    private (string Id, bool Reversed)? _extending;

    private PointDrag? _drag;
    private SizeDrag? _sizeDrag;
    private int _dragCount;

    /// <summary>The running preview: the conveyors as the game creates them, their items, and when the run started.</summary>
    private List<(Actor Actor, Conveyor Conveyor)> _conveyors = [];
    private ConveyorItemFlow? _flow;
    private int _shapeKey;
    private bool _dirty = true;
    private ulong _runStarted;
    private double _animatedTick = -1;

    /// <summary>Views with a preview frame already due, so other redraws do not start more timers.</summary>
    private readonly HashSet<EditorViewport> _due = [];

    public ConveyorTool(ToolManager tools, GameplayState state)
    {
        _tools = tools;
        _state = state;
        // The preview reads the map again only after it changed (or an item mesh arrived), not on every draw.
        tools.Context.ProjectChanged += _ => _dirty = true;
        tools.Context.SessionReplaced += () => _dirty = true;
        ConveyorOverlay.ItemSizesChanged += () => _dirty = true;
    }

    public string Id => "conveyor";

    public string Title => "Draw Conveyor";

    public string Shortcut => "K";

    private static EConveyorLook Look
    {
        get => (EConveyorLook)Math.Clamp(AppSettings.GetInt(Section, "conveyor_look", 0), 0, ConveyorDesign.Looks.Count - 1);
        set => AppSettings.Set(Section, "conveyor_look", (int)value);
    }

    private static EConveyorWalls Walls
    {
        get => (EConveyorWalls)Math.Clamp(AppSettings.GetInt(Section, "conveyor_walls", (int)EConveyorWalls.CW_Auto), 0,
            ConveyorDesign.WallNames.Count - 1);
        set => AppSettings.Set(Section, "conveyor_walls", (int)value);
    }

    private static int Width
    {
        get => AppSettings.GetInt(Section, "conveyor_width", 120);
        set => AppSettings.Set(Section, "conveyor_width", value);
    }

    private static int Speed
    {
        get => AppSettings.GetInt(Section, "conveyor_speed", 150);
        set => AppSettings.Set(Section, "conveyor_speed", value);
    }

    private static int Radius
    {
        get => AppSettings.GetInt(Section, "conveyor_radius", 0);
        set => AppSettings.Set(Section, "conveyor_radius", value);
    }

    /// <summary>Uu of travel per texture repeat for Drive Selected Meshes.</summary>
    public static int ScrollTile
    {
        get => AppSettings.GetInt(Section, "conveyor_tile", 128);
        set => AppSettings.Set(Section, "conveyor_tile", value);
    }

    /// <summary>
    /// Builds the collision world in the background, so the first click does not wait for it, and takes over the
    /// overlay's still items for the conveyors this tool animates.
    /// </summary>
    public void Activate()
    {
        _state.Prewarm();
        ConveyorOverlay.Animated = a => Shown().Contains(a);
        ViewOverlays.Invalidate();
    }

    public void Deactivate()
    {
        Reset();
        ConveyorOverlay.Animated = null;
        ConveyorLook.Still();
        _flow = null;
        _animatedTick = -1;
        ViewOverlays.Invalidate();
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _view = view;
                _mouse = press.Position;
                if (_points.Count > 0 || !Press(view, press))
                {
                    Click(PointAt(view, press.Position, Look, _points.Count > 0 ? _points[0].At : null));
                }

                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _sizeDrag is not null:
                _sizeDrag = null;
                _dirty = true;
                _tools.Context.Session.Commands.EndMerge();
                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _drag is not null:
                Release();
                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseMotion motion:
                _view = view;
                _mouse = motion.Position;
                if (_sizeDrag is not null)
                {
                    SizeDragTo(view, motion.Position);
                    return true;
                }

                if (_drag is not null)
                {
                    DragTo(view, motion.Position);
                    return true;
                }

                _hover = _points.Count > 0 && _state.WorldIfBuilt is not null
                    ? PointAt(view, motion.Position, Look, _points[0].At)
                    : null;
                view.Redraw();
                return _points.Count > 0;
            case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } when _points.Count > 0:
                Finish(loop: false);
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.Backspace } when _points.Count > 0:
                _points.RemoveAt(_points.Count - 1);
                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.R } when _points.Count == 0 && _view == view && HandleAt(view, _mouse) is { } h:
                SetRadius(h);
                return true;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        var batch = new OverlayBatch();
        double tick = Run();
        List<(Actor Actor, Conveyor Conveyor)> running = Running();
        var shownConveyors = new HashSet<Conveyor>(running.Select(p => p.Conveyor), ReferenceEqualityComparer.Instance);
        bool moving = false;
        foreach ((Actor a, Conveyor c) in running)
        {
            ConveyorPath path = c.WorldPath;
            if (path.Length <= 0f)
            {
                continue;
            }

            ConveyorOverlay.Arrows(batch, path, c, c.PhaseAt(tick), SceneStyle.Hover);
            moving |= c.SurfaceSpeed != 0;
            if (_points.Count == 0)
            {
                foreach (ConveyorPoint p in ConveyorDesign.WorldPoints(a))
                {
                    batch.Dot(p.At, SceneStyle.Hover, 5);
                }

                foreach (SizeHandle h in SizeHandles(a, c))
                {
                    if (h.Speed)
                    {
                        batch.Arrow(h.Middle, h.At, ConveyorOverlay.Colour, 2.5f);
                    }
                    else
                    {
                        batch.Line(h.Middle, h.At, ConveyorOverlay.Colour, 1, dashed: true);
                    }

                    batch.Dot(h.At, ConveyorOverlay.Colour, 5);
                }
            }
        }

        _flow!.AdvanceTo((int)Math.Ceiling(tick));
        _flow.Place(tick, _items);
        ConveyorOverlay.Items(batch, _items, origin => shownConveyors.Contains(origin) ? SceneStyle.Hover : null);
        if (tick != _animatedTick && ViewportLayout.Instance is { } layout)
        {
            // Once a frame, not once a view: every view shows the same textured look.
            _animatedTick = tick;
            ConveyorLook.Animate(running, tick, layout.Scene.ActorRoot);
        }

        string? hint = null;
        if (_points.Count > 0)
        {
            List<ConveyorPoint> pending = [.. _points];
            if (_hover is { } h)
            {
                pending.Add(new ConveyorPoint(h, 0));
            }

            batch.Polyline(pending.Select(p => p.At).ToList(), ConveyorOverlay.Colour, 2);
            foreach (ConveyorPoint p in _points)
            {
                batch.Dot(p.At, ConveyorOverlay.Colour, 4);
            }

            ConveyorPath path = ConveyorDesign.WorldPath(Draft(pending, loop: false));
            if (path.Length > 0f)
            {
                ConveyorOverlay.Strip(batch, path, Width * 0.5f, ConveyorOverlay.Colour, 1.5f);
            }

            string what = _extending is { } x ? $"extending {x.Id}" : $"{ConveyorDesign.Looks[(int)Look]} conveyor";
            hint = $"{what}, {path.Length:0} long, {_points.Count} points · Enter finishes, "
                + "click the first point to close a loop, Backspace undoes a point";
        }
        else if (view == _view && HandleAt(view, _mouse) is { } h)
        {
            hint = $"{h.Id} point {h.Index + 1} · drag moves{(h.End ? " · click draws on from this end" : "")} · Shift+click branches · "
                + "Alt+click deletes · R sets the toolbar radius";
        }
        else if (view == _view && (_sizeDrag?.Handle ?? SizeHandleAt(view, _mouse)) is { } size &&
            running.Find(p => p.Actor.Id == size.Id).Conveyor is { } sized)
        {
            hint = size.Speed
                ? $"{size.Id} speed {sized.Speed:0} u/s · drag sets it, back past the middle reverses"
                : $"{size.Id} width {sized.Width:0} · drag sets it";
        }

        batch.Draw(view.Cam, canvas);
        if (hint is not null && view == _view)
        {
            ViewTags.Beside(canvas, _mouse, [new TagPart(hint, EditorTheme.Text)]);
        }

        if (moving && _due.Add(view))
        {
            // Keep the preview moving while the tool is active. A timer, not a deferred call: one queued from a draw
            // runs in the same frame and the frame never ends.
            view.GetTree().CreateTimer(FrameTime).Timeout += () =>
            {
                _due.Remove(view);
                if (_tools.Active == this && GodotObject.IsInstanceValid(view))
                {
                    view.Redraw();
                }
            };
        }
    }

    public bool Cancel()
    {
        if (_points.Count == 0)
        {
            return false;
        }

        Reset();
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    public void BuildOptions(HBoxContainer bar)
    {
        AddChoice(bar, "Look", ConveyorDesign.Looks, (int)Look, i => Look = (EConveyorLook)i);
        AddChoice(bar, "Walls", ConveyorDesign.WallNames, (int)Walls, i => Walls = (EConveyorWalls)i);
        AddNumber(bar, "Width", "Belt width", Width, 16, 2048, "u", v => Width = v);
        AddNumber(bar, "Speed", "Belt speed (negative runs it backwards)", Speed, -2000, 2000, "u/s", v => Speed = v, EditorTheme.NumberFieldWidth);
        AddNumber(bar, "Radius", "Corner radius (R over a point sets that point's own)", Radius, 0, 4096, "u", v => Radius = v);
        AddNumber(bar, "Tile", "Length the belt texture repeats over", ScrollTile, 1, 8192, "u", v => ScrollTile = v);
        ToolOptions.Hint(
            bar,
            "Click floor points", "Enter finishes", "First point: loop", "Drag points, the width dot and the speed arrow",
            "Click an end: draw on", "Shift: branch",
            "Ctrl: insert", "Alt: delete", "R: radius", "Esc cancels");
    }

    private static void AddChoice(HBoxContainer bar, string label, IReadOnlyList<string> names, int selected, Action<int> set)
    {
        bar.AddChild(new Label { Text = label, ThemeTypeVariation = "Dim" });
        OptionButton box = PanelUi.Options(bar, $"Conveyor {label.ToLowerInvariant()}");
        box.CustomMinimumSize = new Vector2(EditorTheme.NumberFieldWidth, 0);
        for (int i = 0; i < names.Count; i++)
        {
            box.AddItem(names[i], i);
        }

        box.Select(selected);
        box.ItemSelected += i => set((int)i);
    }

    private static void AddNumber(HBoxContainer bar, string label, string tooltip, int value, int min, int max, string unit, Action<int> set, int width = EditorTheme.SpinMinWidth)
    {
        bar.AddChild(new Label { Text = label, ThemeTypeVariation = "Dim", TooltipText = tooltip, MouseFilter = Control.MouseFilterEnum.Pass });
        SpinBox box = PanelUi.Spin(bar, min, max, 1, value, tooltip, width);
        box.Suffix = unit;
        box.FocusMode = Control.FocusModeEnum.Click;
        box.ValueChanged += v => set((int)v);
    }

    /// <summary>The conveyors to animate and edit: the selected ones, else every visible one.</summary>
    private List<Actor> Shown()
    {
        MapProject project = _tools.Context.Project;
        List<Actor> selected = _tools.Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
            .Select(r => project.FindActor(r.Id)).OfType<Actor>().Where(ConveyorDesign.Is).ToList();
        return selected.Count > 0 ? selected : project.Actors.Where(a => ConveyorDesign.Is(a) && ObjectFilter.IsVisible(project, a.Editor)).ToList();
    }

    /// <summary>
    /// Where a click puts a point: on the surface under the cursor, lifted for <paramref name="look"/>. In a flat view
    /// the depth the view cannot show is <paramref name="anchor"/>'s.
    /// </summary>
    private Vec3 PointAt(EditorViewport view, Vector2 mouse, EConveyorLook look, Vec3? anchor)
    {
        int depth = ViewAxes.DepthAxis(view.Cam.Kind);
        double? fixedDepth = depth >= 0 && anchor is { } at ? at[depth] : null;
        GameplayHit hit = GameplayPicking.Point(_tools, _state.World, view.Cam, mouse, fixedDepth);
        bool floor = hit.Normal is { Z: > 0.7 };
        return floor ? hit.Point + new Vec3(0, 0, ConveyorDesign.SurfaceLift(look)) : hit.Point;
    }

    private void Click(Vec3 at)
    {
        if (_points.Count >= 3 && Flat(at - _points[0].At) <= CloseDistance)
        {
            Finish(loop: true);
            return;
        }

        // Ends of an open path are never rounded; the radius applies to the corners in between.
        _points.Add(new ConveyorPoint(at, Radius));
        _tools.Context.SetStatus($"Conveyor point {_points.Count} at {at}");
    }

    private void Finish(bool loop)
    {
        if (_points.Count < 2)
        {
            _tools.Context.SetStatus("A conveyor needs two points or more");
            return;
        }

        if (_extending is { } x)
        {
            List<ConveyorPoint> points = x.Reversed ? Enumerable.Reverse(_points).ToList() : [.. _points];
            Reset();
            Edit(x.Id, $"Extend {x.Id}", a =>
            {
                a.Props.Set("Path", ConveyorDesign.PathValue(a, points));
                if (loop)
                {
                    a.Props.Set("bLoop", SvJsonNode.Bool(true));
                }
            });
            _tools.Context.SetStatus($"{x.Id} now has {points.Count} points{(loop ? ", a loop" : "")}");
            return;
        }

        Actor actor = Draft(_points, loop);
        actor.Id = _tools.Context.Project.NewId("Conveyor");
        Reset();
        var cmd = new AddObjectsCommand($"Add {actor.Id}", actors: [actor]);
        _tools.Context.Execute(cmd);
        _tools.Context.Selection.Set(cmd.Added);
        float length = ConveyorDesign.WorldPath(actor).Length;
        _tools.Context.SetStatus($"Added {actor.Id}: {ConveyorDesign.Looks[(int)Look]}, {length:0} long at {Speed} u/s{(loop ? ", a loop" : "")}");
        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>A conveyor actor for points drawn so far, with the toolbar's settings (only what differs from the class).</summary>
    private static Actor Draft(IReadOnlyList<ConveyorPoint> points, bool loop)
    {
        var actor = new Actor("", ConveyorDesign.ClassName) { Location = points[0].At };
        var defaults = new Conveyor();
        actor.Props.Set("Path", ConveyorDesign.PathValue(actor, points));
        if (loop)
        {
            actor.Props.Set("bLoop", SvJsonNode.Bool(true));
        }

        if (Look != defaults.Look)
        {
            actor.Props.Set("Look", SvJsonNode.Integer((int)Look));
        }

        if (Walls != defaults.Walls)
        {
            actor.Props.Set("Walls", SvJsonNode.Integer((int)Walls));
        }

        if (Width != defaults.Width)
        {
            actor.Props.Set("Width", SvJsonNode.Float(Width));
        }

        if (Speed != defaults.Speed)
        {
            actor.Props.Set("Speed", SvJsonNode.Float(Speed));
        }

        return actor;
    }

    // ---- point editing --------------------------------------------------------------------------------------------

    /// <summary>A point of a shown conveyor; <c>End</c> marks either end of an open one.</summary>
    private readonly record struct PointHandle(string Id, int Index, Vec3 At, bool End);

    /// <summary>A place on a shown conveyor's path, between points <c>Index - 1</c> and <c>Index</c>.</summary>
    private readonly record struct PathSpot(string Id, int Index, Vec3 At);

    private sealed record PointDrag(PointHandle Handle, Vector2 From, string MergeKey)
    {
        public bool Moved { get; set; }
    }

    /// <summary>A press with nothing being drawn, on a point or (with Shift or Ctrl) the path; false when it hit neither.</summary>
    private bool Press(EditorViewport view, InputEventMouseButton press)
    {
        if (!press.AltPressed && !press.ShiftPressed && !press.CtrlPressed && HandleAt(view, press.Position) is null &&
            SizeHandleAt(view, press.Position) is { } size)
        {
            _sizeDrag = new SizeDrag(size, $"conveyor.{(size.Speed ? "speed" : "width")}:{++_dragCount}");
            return true;
        }

        if (HandleAt(view, press.Position) is { } h)
        {
            if (press.AltPressed)
            {
                DeletePoint(h);
            }
            else if (press.ShiftPressed)
            {
                Branch(h.At);
            }
            else
            {
                _drag = new PointDrag(h, press.Position, $"conveyor.point:{++_dragCount}");
            }

            return true;
        }

        if ((press.CtrlPressed || press.ShiftPressed) && SpotAt(view, press.Position) is { } s)
        {
            if (press.CtrlPressed)
            {
                InsertPoint(s);
            }
            else
            {
                Branch(s.At);
            }

            return true;
        }

        return false;
    }

    /// <summary>The shown conveyor point nearest the cursor within <see cref="PickPixels"/>.</summary>
    private PointHandle? HandleAt(EditorViewport view, Vector2 mouse)
    {
        PointHandle? best = null;
        float bestDistance = PickPixels;
        foreach (Actor a in Shown())
        {
            IReadOnlyList<ConveyorPoint> points = ConveyorDesign.WorldPoints(a);
            bool loop = ConveyorDesign.Read(a).bLoop;
            for (int i = 0; i < points.Count; i++)
            {
                if (view.Cam.ToScreen(points[i].At) is { } at && at.DistanceTo(mouse) <= bestDistance)
                {
                    bestDistance = at.DistanceTo(mouse);
                    best = new PointHandle(a.Id, i, points[i].At, !loop && (i == 0 || i == points.Count - 1));
                }
            }
        }

        return best;
    }

    /// <summary>The place on a shown conveyor's path (its straight legs between points) nearest the cursor.</summary>
    private PathSpot? SpotAt(EditorViewport view, Vector2 mouse)
    {
        PathSpot? best = null;
        float bestDistance = PickPixels;
        foreach (Actor a in Shown())
        {
            IReadOnlyList<ConveyorPoint> points = ConveyorDesign.WorldPoints(a);
            int legs = ConveyorDesign.Read(a).bLoop ? points.Count : points.Count - 1;
            for (int i = 0; i < legs; i++)
            {
                Vec3 p0 = points[i].At, p1 = points[(i + 1) % points.Count].At;
                if (view.Cam.ToScreen(p0) is not { } s0 || view.Cam.ToScreen(p1) is not { } s1)
                {
                    continue;
                }

                Vector2 leg = s1 - s0;
                float t = leg.LengthSquared() > 0 ? Math.Clamp((mouse - s0).Dot(leg) / leg.LengthSquared(), 0, 1) : 0;
                float distance = (s0 + leg * t).DistanceTo(mouse);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new PathSpot(a.Id, i + 1, p0 + (p1 - p0) * t);
                }
            }
        }

        return best;
    }

    private void DragTo(EditorViewport view, Vector2 mouse)
    {
        PointDrag d = _drag!;
        if (!d.Moved && mouse.DistanceTo(d.From) < DragPixels)
        {
            return;
        }

        d.Moved = true;
        if (_tools.Context.Project.FindActor(d.Handle.Id) is not { } actor)
        {
            return;
        }

        Vec3 at = PointAt(view, mouse, ConveyorDesign.Read(actor).Look, d.Handle.At);
        EditPoints(d.Handle.Id, $"Move {d.Handle.Id} point {d.Handle.Index + 1}", points => points[d.Handle.Index] = points[d.Handle.Index] with { At = at },
            d.MergeKey);
        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>The end of a press on a point: a drag ends its undo step; a click on an open end draws on from it.</summary>
    private void Release()
    {
        PointDrag d = _drag!;
        _drag = null;
        if (d.Moved)
        {
            _tools.Context.Session.Commands.EndMerge();
            return;
        }

        if (d.Handle.End && _tools.Context.Project.FindActor(d.Handle.Id) is { } actor)
        {
            List<ConveyorPoint> points = [.. ConveyorDesign.WorldPoints(actor)];
            bool reversed = d.Handle.Index == 0;
            if (reversed)
            {
                points.Reverse();
            }

            _points.Clear();
            _points.AddRange(points);
            _extending = (d.Handle.Id, reversed);
            _tools.Context.SetStatus($"Drawing on from {(reversed ? "the start" : "the end")} of {d.Handle.Id}: Enter finishes, Esc drops it");
        }
    }

    /// <summary>Starts a new conveyor at <paramref name="at"/>, another conveyor's point or a place on its path.</summary>
    private void Branch(Vec3 at)
    {
        _points.Clear();
        _points.Add(new ConveyorPoint(at, 0));
        _extending = null;
        _tools.Context.SetStatus($"Branch from {at}: click on to draw, Enter finishes");
    }

    private void InsertPoint(PathSpot s) =>
        EditPoints(s.Id, $"Insert {s.Id} point {s.Index + 1}", points => points.Insert(s.Index, new ConveyorPoint(s.At, Radius)));

    private void DeletePoint(PointHandle h)
    {
        if (_tools.Context.Project.FindActor(h.Id) is not { } actor || ConveyorDesign.WorldPoints(actor).Count <= 2)
        {
            _tools.Context.SetStatus("A conveyor keeps two points or more");
            return;
        }

        EditPoints(h.Id, $"Delete {h.Id} point {h.Index + 1}", points => points.RemoveAt(h.Index));
    }

    private void SetRadius(PointHandle h)
    {
        EditPoints(h.Id, $"Round {h.Id} point {h.Index + 1}", points => points[h.Index] = points[h.Index] with { Radius = Radius });
        _tools.Context.SetStatus($"{h.Id} point {h.Index + 1}: radius {Radius}");
    }

    /// <summary>One undo step changing a conveyor's points (world space); drags pass a merge key so a drag is one step.</summary>
    private void EditPoints(string id, string name, Action<List<ConveyorPoint>> change, string? mergeKey = null) =>
        Edit(id, name, a =>
        {
            List<ConveyorPoint> points = [.. ConveyorDesign.WorldPoints(a)];
            change(points);
            a.Props.Set("Path", ConveyorDesign.PathValue(a, points));
        }, mergeKey);

    private void Edit(string id, string name, Action<Actor> change, string? mergeKey = null)
    {
        ReplaceObjectsCommand cmd = ReplaceObjectsCommand.Edit(_tools.Context.Project, [ObjectRef.Actor(id)], name, actor: change, mergeKey: mergeKey);
        if (!cmd.IsEmpty)
        {
            _tools.Context.Execute(cmd);
        }

        ViewportLayout.Instance?.RedrawAll();
    }

    private static double Flat(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    private void Reset()
    {
        _points.Clear();
        _hover = null;
        _extending = null;
        _drag = null;
        _sizeDrag = null;
    }

    // ---- the running preview --------------------------------------------------------------------------------------

    /// <summary>Brings the preview up to the map when it changed and returns the server tick the preview is at.</summary>
    private double Run()
    {
        if (_flow is null || _dirty)
        {
            _dirty = false;
            Refresh();
        }

        return (Time.GetTicksMsec() - _runStarted) / 1000.0 * Sim.Hz;
    }

    /// <summary>
    /// Reads the conveyors again. A change to where they lie or what they carry runs the preview from tick 0 (items
    /// cannot run backwards); so does a width change, once its drag ends (the width decides where items hand on). A
    /// change of speed or running keeps the belts and their items, each belt's phase carrying on from where it is, as
    /// the game changes a belt (<see cref="Conveyor.SetRunning"/>).
    /// </summary>
    private void Refresh()
    {
        List<(Actor Actor, Conveyor Conveyor)> read = ConveyorDesign.ReadAll(_tools.Context.Project.Actors);
        int shape = ShapeKey(read);
        bool sameIds = read.Count == _conveyors.Count && read.Zip(_conveyors).All(p => p.First.Actor.Id == p.Second.Actor.Id);
        if (_flow is null || !sameIds || (shape != _shapeKey && _sizeDrag is null))
        {
            _shapeKey = shape;
            _conveyors = read;
            _flow = ConveyorOverlay.Flow(_conveyors);
            _runStarted = Time.GetTicksMsec();
            _animatedTick = -1;
            return;
        }

        int tick = _flow.Tick;
        for (int i = 0; i < read.Count; i++)
        {
            Conveyor now = read[i].Conveyor, belt = _conveyors[i].Conveyor;
            if (now.SurfaceSpeed != belt.SurfaceSpeed)
            {
                belt.PhaseAtChange = (float)belt.PhaseAt(tick);
                belt.SpeedBeforeChange = belt.SurfaceSpeed;
                belt.ChangeTick = tick;
            }

            belt.bOn = now.bOn;
            belt.Speed = now.Speed;
            belt.Width = now.Width;
            read[i] = (read[i].Actor, belt);
        }

        _conveyors = read;
    }

    /// <summary>Everything about the conveyors but their speed and running state.</summary>
    private static int ShapeKey(List<(Actor Actor, Conveyor Conveyor)> conveyors)
    {
        var key = new HashCode();
        foreach ((Actor a, _) in conveyors)
        {
            key.Add(a.Id);
            key.Add(a.Location);
            key.Add(a.Rotation);
            foreach ((string name, SvJsonNode value) in a.Props)
            {
                if (name is not ("Speed" or "bOn"))
                {
                    key.Add(name);
                    key.Add(value.ToString());
                }
            }
        }

        return key.ToHashCode();
    }

    /// <summary>The running conveyors this tool shows and edits (<see cref="Shown"/>).</summary>
    private List<(Actor Actor, Conveyor Conveyor)> Running()
    {
        HashSet<Actor> shown = [.. Shown()];
        return _conveyors.Where(p => shown.Contains(p.Actor)).ToList();
    }

    // ---- width and speed handles ----------------------------------------------------------------------------------

    /// <summary>
    /// A conveyor's width or speed handle at the middle of its path: <c>At</c> is the strip's right edge, or the tip of
    /// an arrow as long as the belt moves in <see cref="SpeedArrowSeconds"/>. <c>Along</c> and <c>Across</c> are the
    /// path's direction and its flat right there.
    /// </summary>
    private readonly record struct SizeHandle(string Id, bool Speed, Vec3 Middle, Vec3 Along, Vec3 Across, Vec3 At);

    private sealed record SizeDrag(SizeHandle Handle, string MergeKey);

    private static IEnumerable<SizeHandle> SizeHandles(Actor a, Conveyor c)
    {
        ConveyorPath path = c.WorldPath;
        if (path.Length <= 0f)
        {
            yield break;
        }

        path.Evaluate(path.Length / 2, out FVector p, out FVector t);
        Vec3 middle = ConveyorDesign.V(p), along = ConveyorDesign.V(t).Normalized(), across = new Vec3(-t.Y, t.X, 0).Normalized();
        yield return new SizeHandle(a.Id, false, middle, along, across, middle + across * c.HalfWidth);
        yield return new SizeHandle(a.Id, true, middle, along, across, middle + along * (c.Speed * SpeedArrowSeconds));
    }

    /// <summary>The shown conveyors' width or speed handle nearest the cursor within <see cref="PickPixels"/>.</summary>
    private SizeHandle? SizeHandleAt(EditorViewport view, Vector2 mouse)
    {
        Run();
        SizeHandle? best = null;
        float bestDistance = PickPixels;
        foreach ((Actor a, Conveyor c) in Running())
        {
            foreach (SizeHandle h in SizeHandles(a, c))
            {
                if (view.Cam.ToScreen(h.At) is { } at && at.DistanceTo(mouse) <= bestDistance)
                {
                    bestDistance = at.DistanceTo(mouse);
                    best = h;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Sets the dragged handle's width or speed from where the cursor is along the handle's line, met by the plane
    /// through the line that faces the view most. One undo step per drag.
    /// </summary>
    private void SizeDragTo(EditorViewport view, Vector2 mouse)
    {
        SizeDrag d = _sizeDrag!;
        SizeHandle h = d.Handle;
        Vec3 axis = h.Speed ? h.Along : h.Across;
        (Vec3 origin, Vec3 ray) = view.Cam.Ray(mouse);
        Vec3 normal = ray - axis * Vec3.Dot(ray, axis);
        double facing = Vec3.Dot(normal, ray);
        if (normal.LengthSquared < 1e-8 || Math.Abs(facing) < 1e-6)
        {
            return;
        }

        Vec3 hit = origin + ray * (Vec3.Dot(normal, h.Middle - origin) / facing);
        double along = Vec3.Dot(hit - h.Middle, axis);
        if (h.Speed)
        {
            int speed = (int)Math.Clamp(Math.Round(along / SpeedArrowSeconds), -MaxSpeed, MaxSpeed);
            Edit(h.Id, $"Set {h.Id} speed", a => a.Props.Set("Speed", SvJsonNode.Float(speed)), d.MergeKey);
            _tools.Context.SetStatus($"{h.Id}: {speed} u/s");
        }
        else
        {
            int width = (int)Math.Clamp(Math.Round(2 * Math.Abs(along)), MinWidth, MaxWidth);
            Edit(h.Id, $"Set {h.Id} width", a => a.Props.Set("Width", SvJsonNode.Float(width)), d.MergeKey);
            _tools.Context.SetStatus($"{h.Id}: {width} wide");
        }
    }
}
