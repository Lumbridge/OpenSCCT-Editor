using Godot;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// The surface tool (U): click picks the brush face (surface) under the cursor, Shift adds, Ctrl toggles, Alt+click
/// applies the Assets panel's material to it (to every picked face when it is one of them), double-click picks every
/// face of that brush. A surface of an imported map's baked world is picked for what it is and shown read-only.
/// Dragging a picked face extrudes the picked faces along their normals on the grid (one undo step; Escape puts them
/// back); with Neighbour Snap on, the grabbed face stops on nearby parallel faces first (Alt held inverts it). Hovering
/// a wall of a carved room shows a "+" under the cursor that adds a doorway, window, corridor, vent,
/// crawlway or room there (RE+). Picked faces are tinted in every view and edited in the Surface panel. Keys while a
/// view has focus: Ctrl+Shift+C copies the surface, Ctrl+Shift+V pastes it (Ctrl+Alt+Shift+V with its alignment), the
/// arrow keys pan the texture by one texel (Shift: 16), Escape clears the faces.
/// </summary>
public sealed class FaceTool : IViewportTool
{
    private const float DragSlop = 4;
    private const float BadgeRadius = 9;

    /// <summary>The badge sits this far over the floor, under the cursor.</summary>
    private const double BadgeHeight = 48;

    private static int s_dragCounter;
    private readonly ToolManager _tools;
    private Vector2? _press;
    private bool _altPress;
    private FaceHit? _pressHit;
    private FaceRef? _hover;
    private WallAnchor? _wall;
    private EditorViewport? _hoverView;
    private ExtrudeDrag? _drag;
    private int _panCounter;
    private bool _resolving;

    public FaceTool(ToolManager tools)
    {
        _tools = tools;
        tools.DragInterrupted += DropDrag;
        // Neighbour snap turned on or off mid-drag (Shift+G) shows at once.
        tools.Changed += () =>
        {
            if (_drag is { } drag && !_resolving)
            {
                _resolving = true;
                try
                {
                    UpdateDrag(drag, drag.Mouse, force: true);
                }
                finally
                {
                    _resolving = false;
                }
            }
        };
    }

    public string Id => "face";

    public string Title => "Surface";

    public string Description =>
        "Pick brush surfaces (faces) to edit their material, texture alignment and flags in the Surface panel. Shift adds, " +
        "Ctrl toggles, Alt+click applies the Assets panel's material, double-click takes the whole brush";

    public bool IsDragging => _drag is not null;

    public string Shortcut => "U";

    private static GeometrySelection Geo => GeometrySelection.Instance;

    private MapProject Project => _tools.Context.Project;

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press when BadgeAt(view, press.Position) is { } wall:
                WallMenu(view, press.Position, wall);
                _press = null;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } dbl:
                if (FacePicking.Pick(view.Cam, Project, dbl.Position) is { } whole)
                {
                    IEnumerable<FaceRef> all = FaceEdit.AllFaces(Project, [whole.Face.Brush]);
                    if (dbl.ShiftPressed)
                    {
                        Geo.AddFaces(all);
                    }
                    else
                    {
                        Geo.SetFaces(all);
                    }

                    SurfaceCommands.Report();
                }

                _press = null;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                // Alt+click applies the material on release; Alt held from the press into a drag of a picked face
                // extrudes it with neighbour snap inverted instead.
                _press = press.Position;
                _altPress = press.AltPressed;
                _pressHit = FacePicking.Pick(view.Cam, Project, press.Position);
                return true;
            case InputEventMouseMotion motion when _drag is { } drag:
                if (drag.View == view)
                {
                    drag.Snap.AltHeld = motion.AltPressed || Godot.Input.IsKeyPressed(Godot.Key.Alt);
                    UpdateDrag(drag, motion.Position);
                }

                return true;
            case InputEventKey { Keycode: Godot.Key.Alt } alt when _drag is { } drag:
                // Holding Alt inverts neighbour snap for this drag: show it without waiting for the mouse to move.
                if (drag.Snap.AltHeld != alt.Pressed)
                {
                    drag.Snap.AltHeld = alt.Pressed;
                    UpdateDrag(drag, drag.Mouse, force: true);
                }

                return true;
            case InputEventMouseMotion motion when _press is { } start && _pressHit is { } grabbed && Geo.HasFace(grabbed.Face)
                                                   && !motion.ShiftPressed && !motion.IsCommandOrControlPressed()
                                                   && start.DistanceTo(motion.Position) > DragSlop:
                _drag = StartDrag(view, start, grabbed);
                _press = null;
                _drag.Snap.AltHeld = motion.AltPressed || Godot.Input.IsKeyPressed(Godot.Key.Alt);
                UpdateDrag(_drag, motion.Position);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _drag is not null:
                DropDrag();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _press is { } start:
                _press = null;
                if (start.DistanceTo(release.Position) <= DragSlop)
                {
                    if (_altPress)
                    {
                        ApplyCurrentMaterial(view, start);
                    }
                    else
                    {
                        Click(view, release);
                    }
                }

                return true;
            case InputEventMouseMotion motion:
                UpdateHover(view, motion.Position);
                return false;
            case InputEventKey { Pressed: true } key:
                return Key(key);
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        FacePicking.DrawFaces(view.Cam, canvas, Project, Geo.Faces, _hoverView == view ? _hover : null);
        if (view.Cam.IsOrtho && Geo.BakedSurface is { } baked)
        {
            FacePicking.DrawBaked(view.Cam, canvas, baked);
        }

        if (_drag is { } stuck)
        {
            stuck.Snap.Draw(view, canvas);
        }

        if (_drag is { } d && d.View == view && d.Readout.Length > 0)
        {
            ViewTags.BesideStack(canvas, d.Mouse, [d.Readout, .. d.SnapReadout]);
        }

        if (_drag is null && _hoverView == view && Badge(view) is { } badge)
        {
            canvas.DrawCircle(badge, BadgeRadius, new Color(EditorTheme.Accent, 0.9f));
            canvas.DrawLine(badge - new Vector2(5, 0), badge + new Vector2(5, 0), Colors.White, 2);
            canvas.DrawLine(badge - new Vector2(0, 5), badge + new Vector2(0, 5), Colors.White, 2);
        }
    }

    public void BuildOptions(HBoxContainer bar)
    {
        var label = new Label { Text = FacesText() };
        bar.AddChild(label);
        void Update() => label.Text = FacesText();
        Geo.Changed += Update;
        label.TreeExiting += () => Geo.Changed -= Update;
        ToolOptions.Command(bar, "Same material", "Pick every face with the picked face's material (Surface > Select)", () => SurfaceCommands.SelectMatching("material"));
        ToolOptions.Command(bar, "Coplanar", "Pick every face in the picked face's plane", () => SurfaceCommands.SelectMatching("coplanar"));
        ToolOptions.Command(bar, "Adjacent", "Pick every face connected to the picked ones (Surface > Select > Adjacent for walls, floors...)",
            () => SurfaceCommands.SelectAdjacent("all"));
        ToolOptions.Command(bar, "Whole brush", "Pick every face of the picked faces' brushes", SurfaceCommands.SelectAllFaces);
        ToolOptions.Command(bar, "Select brushes", "Select the brushes of the picked faces", SurfaceCommands.SelectBrushes);
        ToolOptions.Hint(bar, "Alt+click applies the Assets material", "Drag a picked face to extrude it (Alt while dragging flips Neighbour Snap)",
            "Click the + on a hovered wall to add to it");
    }

    public void Deactivate()
    {
        _hover = null;
        _wall = null;
        _hoverView = null;
    }

    public bool Cancel()
    {
        if (_drag is { } drag)
        {
            _drag = null;
            if (drag.Executed)
            {
                _tools.Context.Session.Commands.Undo();
            }

            _tools.Context.Session.Commands.EndMerge();
            Geo.SetFaces(drag.Faces);
            _tools.Context.SetStatus("Extrude cancelled");
            ViewportLayout.Instance?.RedrawAll();
            return true;
        }

        if (Geo.Faces.Count == 0 && Geo.BakedSurface is null)
        {
            return false;
        }

        Geo.SetFaces([]);
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    private void Click(EditorViewport view, InputEventMouseButton mods)
    {
        FaceHit? hit = FacePicking.Pick(view.Cam, Project, mods.Position);
        bool adding = mods.ShiftPressed || mods.IsCommandOrControlPressed();
        if (BakedNearer(view, mods.Position, hit) is { } baked)
        {
            // A baked surface is picked alone: Shift or Ctrl on one keeps the faces already picked.
            if (adding && Geo.Faces.Count > 0)
            {
                _tools.Context.SetStatus($"Baked surface ({baked.Material ?? "no material"}) not added: it is part of the world imported with the "
                    + $"map and is picked on its own. {PanelUi.Count(Geo.Faces.Count, "face")} still picked");
                return;
            }

            Geo.SetBakedSurface(baked);
            _tools.Context.SetStatus($"Baked surface ({baked.Material ?? "no material"}): read-only, part of the world imported with the map"
                + (adding ? " (picked on its own)" : ""));
            ViewportLayout.Instance?.RedrawAll();
            return;
        }

        if (hit is not { } h)
        {
            if (!mods.ShiftPressed && !mods.IsCommandOrControlPressed())
            {
                Geo.SetFaces([]);
            }
        }
        else if (mods.ShiftPressed)
        {
            Geo.AddFaces([h.Face]);
        }
        else if (mods.IsCommandOrControlPressed())
        {
            Geo.ToggleFace(h.Face);
        }
        else
        {
            Geo.SetFaces([h.Face]);
        }

        SurfaceCommands.Report();
        if (hit is { } picked && Project.FindBrush(picked.Face.Brush) is { } brush && BakedContent.SurfacesLocked(Project, brush))
        {
            _tools.Context.SetStatus($"Face {picked.Face}: read-only, part of the imported baked world: {SurfaceCommands.BuildRenderHint}");
        }

        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>The baked-world surface under the cursor when it is nearer than the brush face hit (a baked brush's face
    /// lies on its own baked surface, so the face wins a tie), or null.</summary>
    internal static BakedSurfacePick? BakedNearer(EditorViewport view, Vector2 mouse, FaceHit? hit)
    {
        if (ViewportLayout.Instance?.Scene is not { } scene)
        {
            return null;
        }

        (Vec3 o, Vec3 d) = view.Cam.Ray(mouse);
        double max = hit is { } h ? h.Distance - 2 : 1e7;
        return max > 0 ? scene.PickBaked(o, d, max) : null;
    }

    /// <summary>Alt+click: the Assets panel's material on the face under the cursor, or on every picked face when it is
    /// one of them.</summary>
    private void ApplyCurrentMaterial(EditorViewport view, Vector2 mouse)
    {
        FaceHit? face = FacePicking.Pick(view.Cam, Project, mouse);
        // The baked world drawn in front of a brush face hides it: the face behind is not what was clicked.
        if (BakedNearer(view, mouse, face) is not null)
        {
            _tools.Context.SetStatus($"That surface is read-only, part of the imported baked world: {SurfaceCommands.BuildRenderHint}");
            return;
        }

        if (face is not { } hit)
        {
            _tools.Context.SetStatus("Alt+click a brush face to apply the Assets panel's material to it");
            return;
        }

        SurfaceCommands.ApplyMaterial(Panels.AssetBrowserPanel.CurrentMaterial, Geo.HasFace(hit.Face) ? null : [hit.Face]);
    }

    private bool Key(InputEventKey key)
    {
        bool ctrl = key.IsCommandOrControlPressed();
        if (ctrl && key.ShiftPressed && key.Keycode == Godot.Key.C)
        {
            SurfaceCommands.Copy();
            return true;
        }

        if (ctrl && key.ShiftPressed && key.Keycode == Godot.Key.V)
        {
            SurfaceCommands.Paste(withMapping: key.AltPressed);
            return true;
        }

        if (ctrl || key.AltPressed || Geo.Faces.Count == 0)
        {
            return false;
        }

        int step = key.ShiftPressed ? 16 : 1;
        (int du, int dv) = key.Keycode switch
        {
            Godot.Key.Left => (-step, 0),
            Godot.Key.Right => (step, 0),
            Godot.Key.Up => (0, -step),
            Godot.Key.Down => (0, step),
            _ => (0, 0),
        };
        if (du == 0 && dv == 0)
        {
            return false;
        }

        // A held key (echo) keeps panning in the same undo step.
        if (!key.Echo)
        {
            _panCounter++;
        }

        SurfaceCommands.Run("Pan texture", (f, _) => TextureOps.Pan(f, du, dv), $"face-pan:{_panCounter}");
        return true;
    }

    // ---- hover and the wall "+" ----------------------------------------------------------------------------------

    private void UpdateHover(EditorViewport view, Vector2 mouse)
    {
        FaceHit? hit = FacePicking.Pick(view.Cam, Project, mouse);
        FaceRef? hover = hit?.Face;
        WallAnchor? wall = hit is { } h ? WallPieces.Anchor(Project, h.Face, h.Point) : null;
        if (hover != _hover || _hoverView != view || wall?.Point != _wall?.Point)
        {
            EditorViewport? old = _hoverView;
            _hover = hover;
            _wall = wall;
            _hoverView = view;
            if (old != view)
            {
                old?.Redraw();
            }

            view.Redraw();
        }
    }

    /// <summary>Where the "+" of the hovered wall is drawn in a view, or null.</summary>
    private Vector2? Badge(EditorViewport view) =>
        _wall is { } w ? view.Cam.ToScreen(w.Point + new Vec3(0, 0, BadgeHeight)) : null;

    private WallAnchor? BadgeAt(EditorViewport view, Vector2 mouse) =>
        _hoverView == view && _wall is { } w && Badge(view) is { } b && b.DistanceTo(mouse) <= BadgeRadius + 2 ? w : null;

    private void WallMenu(EditorViewport view, Vector2 at, WallAnchor wall)
    {
        var menu = new PopupMenu();
        foreach (string kind in WallPieces.Kinds)
        {
            menu.AddItem("Add " + WallPieces.Title(kind));
        }

        menu.IndexPressed += i => WallPieceCommands.Add(WallPieces.Kinds[(int)i], wall);
        menu.PopupHide += () => menu.QueueFree();
        view.AddChild(menu);
        menu.Position = PanelUi.ScreenPoint(view, at);
        menu.Popup();
    }

    // ---- drag extrude ------------------------------------------------------------------------------------------

    private ExtrudeDrag StartDrag(EditorViewport view, Vector2 mouse, FaceHit grabbed)
    {
        (Vec3 o, Vec3 d) = view.Cam.Ray(mouse);
        List<Brush> brushes = FaceEdit.Group(Project, Geo.Faces).Select(g => g.Brush.Clone()).ToList();

        // The grabbed face as it was at the start is the one that sticks; the brushes being extruded are not neighbours.
        SnapFace? face = brushes.FirstOrDefault(b => string.Equals(b.Id, grabbed.Face.Brush, StringComparison.OrdinalIgnoreCase)) is { } start
                         && BrushGeometry.Of(start).Faces is var faces && grabbed.Face.Face < faces.Count
            ? SnapFace.Of(ObjectRef.Brush(start.Id), grabbed.Face.Face, SnapSource.Brush, faces[grabbed.Face.Face].Vertices)
            : null;
        var snap = new NeighbourDrag(_tools, view, SnapUse.Resize, brushes.Select(b => b.Id), () => null);

        // How far the face can be pushed in before it reaches the far side of its brush (the extrude refuses that):
        // nothing at or past it is offered as a stick.
        double thickness = double.PositiveInfinity;
        if (face is not null && brushes.FirstOrDefault(b => string.Equals(b.Id, grabbed.Face.Brush, StringComparison.OrdinalIgnoreCase)) is { } own)
        {
            thickness = BrushGeometry.Of(own).Faces.SelectMany(f => f.Vertices).Max(v => Vec3.Dot(face.Normal, face.Center - v));
        }

        return new ExtrudeDrag(view, grabbed.Point, grabbed.Normal, $"face-extrude:{++s_dragCounter}", brushes, Geo.Faces.ToList(), snap)
        {
            T0 = DragMath.LineParam(grabbed.Point, grabbed.Normal, o, d),
            Grabbed = face,
            MinDistance = -thickness + 1,
        };
    }

    /// <summary>One step of the extrude at <paramref name="mouse"/>: a parallel neighbour face within the snap distance
    /// sets the distance, else the grid does (relative, as before). <paramref name="force"/> re-runs it in place.</summary>
    private void UpdateDrag(ExtrudeDrag drag, Vector2 mouse, bool force = false)
    {
        drag.Mouse = mouse;
        (Vec3 o, Vec3 d) = drag.View.Cam.Ray(mouse);
        double raw = DragMath.LineParam(drag.Grab, drag.Normal, o, d) - drag.T0;
        ValueSnap? stuck = drag.Grabbed is { } grabbed ? drag.Snap.Extrude(grabbed, raw, drag.MinDistance) : null;
        double distance = stuck?.Value ?? _tools.SnapValue(raw);
        if (Math.Abs(distance - drag.Applied) >= 1e-9)
        {
            drag.Applied = distance;
            bool applied = false;
            _tools.DragStep(() => applied = SurfaceCommands.Extrude(distance, drag.Brushes, drag.Faces, drag.Key));
            drag.Executed |= applied;
            drag.Refused = !applied;
        }

        if (drag.Refused)
        {
            // The extrude refused this distance (the face went through its brush): nothing stuck, whatever the solve said.
            drag.Snap.Release();
        }

        drag.Readout = drag.Refused
            ? [.. ViewTags.Value("Extrude", ViewTags.Number(distance)), new TagPart(" refused", EditorTheme.Warning)]
            : ViewTags.Value("Extrude", ViewTags.Number(distance));
        drag.SnapReadout = drag.Snap.Readout();
        bool wasStuck = drag.WasStuck;
        drag.WasStuck = drag.Snap.Stuck;
        if (!drag.Refused)
        {
            _tools.Context.SetStatus($"Extrude {ViewTags.Number(distance)}{drag.Snap.Status()}");
        }

        if (drag.Snap.Stuck || wasStuck || force)
        {
            // The faces it sticks to are drawn in every view (and wiped from them once it lets go).
            ViewportLayout.Instance?.RedrawAll();
        }
        else
        {
            drag.View.Redraw();
        }
    }

    /// <summary>Ends a drag where it is (mouse up, another edit came in, or another map opened).</summary>
    private void DropDrag()
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        _tools.Context.Session.Commands.EndMerge();
        ViewportLayout.Instance?.RedrawAll();
    }

    private static string FacesText() => Geo.Faces.Count switch
    {
        0 when Geo.BakedSurface is not null => "Baked surface",
        0 => "Click a surface",
        1 => $"Face {Geo.Faces[0]}",
        int n => PanelUi.Count(n, "face"),
    };

    /// <summary>A face drag in progress: every step extrudes from the brushes as they were when it started.</summary>
    private sealed class ExtrudeDrag(EditorViewport view, Vec3 grab, Vec3 normal, string key, List<Brush> brushes, List<FaceRef> faces,
        NeighbourDrag snap)
    {
        public EditorViewport View { get; } = view;

        /// <summary>Neighbour snap for this drag.</summary>
        public NeighbourDrag Snap { get; } = snap;

        /// <summary>The grabbed face as it was at the start (null when it has no area).</summary>
        public SnapFace? Grabbed { get; init; }

        /// <summary>The neighbour snap chips after the readout.</summary>
        public TagPart[][] SnapReadout { get; set; } = [];

        /// <summary>Stick distances at or below this push the face through its brush (not offered).</summary>
        public double MinDistance { get; init; } = double.NegativeInfinity;

        /// <summary>True when the extrude refused the last distance it was given.</summary>
        public bool Refused { get; set; }

        /// <summary>True when the last step stuck to something (its highlights are in every view).</summary>
        public bool WasStuck { get; set; }

        public Vec3 Grab { get; } = grab;

        public Vec3 Normal { get; } = normal;

        public string Key { get; } = key;

        public List<Brush> Brushes { get; } = brushes;

        public List<FaceRef> Faces { get; } = faces;

        public double T0 { get; init; }

        public double Applied { get; set; }

        public bool Executed { get; set; }

        /// <summary>What the drag has done so far, for the readout beside the cursor.</summary>
        public TagPart[] Readout { get; set; } = [];

        /// <summary>The readout as plain text (the status bar).</summary>
        public string Label => ViewTags.Plain(Readout);

        /// <summary>Where the cursor is now (the readout follows it).</summary>
        public Vector2 Mouse { get; set; }
    }
}
