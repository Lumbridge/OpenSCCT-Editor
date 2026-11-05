using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports;

/// <summary>
/// One view: a SubViewport with its own camera over the shared <see cref="WorldScene"/>, a 2D overlay for handles
/// and labels, and the camera controls. Perspective: hold the right mouse button to fly (mouse look, WASD, Q/E down
/// and up, Shift faster, wheel changes speed), middle drag pans, the wheel dollies. Orthographic: right or middle
/// drag pans, the wheel zooms at the cursor. A right click without dragging opens the context menu. Everything else
/// goes to the active tool.
/// </summary>
public sealed partial class EditorViewport : Control
{
    /// <summary>How far (screen pixels) the pointer may wander while the right button is down and still make a click
    /// that opens the context menu rather than a pan.</summary>
    private const float ClickSlop = 4;

    /// <summary>The same for mouse look in the perspective view. The pointer is captured there, so motion comes in
    /// raw mouse counts, which Windows' pointer speed does not scale, and on a 1600-3200 DPI mouse a steady click can
    /// move ten counts or more. Motion held back below this is applied in full once it is passed, so the cost is only
    /// a short dead zone at the start of a look (about 2.4 degrees).</summary>
    private const float LookClickSlop = 24;
    private readonly SubViewport _sub;
    private readonly SubViewportContainer _container;
    private readonly ViewportOverlay _overlay;
    private readonly ViewGrid _grid;
    private readonly ReferenceLayer _references;
    private readonly MenuButton _menu;
    private readonly StoreySlider _storeys = new() { Name = "Storeys", GrowHorizontal = GrowDirection.Begin };
    private bool _flying;
    private bool _panning;
    private bool _rightHeld;
    private Vector2 _rightPress;

    /// <summary>The right-button gesture became a drag (a pan, a look or a fly): its release opens no context menu.</summary>
    private bool _rightMoved;

    /// <summary>Motion while the right button is down, held back until it is more than a click's wobble.</summary>
    private Vector2 _rightPending;
    private Vector2 _restoreMouse;
    private RenderMode _mode;

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public EditorViewport()
        : this(ViewKind.Perspective)
    {
    }

    public EditorViewport(ViewKind kind)
    {
        Kind = kind;
        Name = kind.ToString();
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(120, 90);

        // Sized in rendered pixels and scaled back down (OnResized), so under a scaled UI the 3D image is drawn one
        // rendered pixel per screen pixel instead of being stretched.
        _container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_container);
        _sub = new SubViewport
        {
            Msaa3D = Viewport.Msaa.Msaa2X,
            HandleInputLocally = false,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            PhysicsObjectPicking = false,
        };
        _container.AddChild(_sub);
        var camera = new Camera3D { Current = true };
        _sub.AddChild(camera);
        Cam = new ViewCamera(kind, camera);
        _grid = new ViewGrid { Kind = kind, Name = "Grid" };
        _sub.AddChild(_grid);
        _references = new ReferenceLayer { Kind = kind, Name = "References" };
        _sub.AddChild(_references);

        _overlay = new ViewportOverlay(this) { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_overlay);

        _menu = new MenuButton
        {
            Flat = false,
            ThemeTypeVariation = "ViewLabel",
            FocusMode = FocusModeEnum.None,
            Position = new Vector2(ViewTags.Inset, ViewTags.Inset),
        };
        _menu.GetPopup().AboutToPopup += FillMenu;
        _menu.GetPopup().IdPressed += OnMenu;
        MenuToggles.KeepOpen(_menu.GetPopup(), SyncMenu);
        AddChild(_menu);

        // At the top right, sized by its content and growing to the left and down.
        _storeys.SetAnchorsPreset(LayoutPreset.TopRight);
        _storeys.OffsetRight = -ViewTags.Inset;
        _storeys.OffsetLeft = -ViewTags.Inset;
        _storeys.OffsetTop = ViewTags.Inset;
        AddChild(_storeys);

        Mode = kind == ViewKind.Perspective ? RenderMode.Unlit : RenderMode.Wireframe;
        if (kind != ViewKind.Perspective)
        {
            Cam.UnitsPerPixel = 4;
        }
    }

    public ViewKind Kind { get; }

    public ViewCamera Cam { get; }

    /// <summary>The view's 3D frame, without the overlay (screenshots read it).</summary>
    public SubViewport Sub => _sub;

    /// <summary>Raised when the camera moves or zooms (the layout persists views and refreshes the grid).</summary>
    public event Action<EditorViewport>? CameraChanged;

    /// <summary>Raised when the view is clicked or hovered, so it becomes the active view.</summary>
    public event Action<EditorViewport>? Activated;

    /// <summary>Raised when the cursor moves over the view, with the world point under it (or null).</summary>
    public event Action<EditorViewport, Vec3?>? CursorMoved;

    /// <summary>Raised for a right click without drag (the layout shows the context menu there).</summary>
    public event Action<EditorViewport, Vector2>? ContextMenuRequested;

    /// <summary>Raised when the user asks to maximise or restore this view from its menu.</summary>
    public event Action<EditorViewport>? MaximizeToggled;

    /// <summary>Camera fly speed in units per second (persists per session).</summary>
    public static double FlySpeed { get; set; } = 900;

    /// <summary>The view the user is working in (the last one the pointer entered). Only it shows the storey slider,
    /// unless a storey is picked, so four views do not repeat the same control.</summary>
    public bool IsActiveView
    {
        get => _active;
        set
        {
            _active = value;
            _storeys.InActiveView = value;
        }
    }

    private bool _active;

    /// <summary>The highest a tag drawn over the view may start: just below the view's own label.</summary>
    public float TagTop => _menu.Position.Y + _menu.Size.Y + EditorTheme.SpaceXs;

    /// <summary>The view's label (its menu) at the top left, which world labels keep clear of.</summary>
    public Rect2 LabelRect => _menu.GetRect();

    /// <summary>The storey slider's box at the top right, which tags keep clear of (empty while it is hidden).</summary>
    public Rect2 StoreysRect => _storeys.IsVisibleInTree() ? _storeys.GetRect() : default;

    public bool ShowGrid { get; set; } = true;

    public RenderMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            ApplyCullMask();
            Cam.Camera.Environment = MakeEnvironment(value);
            UpdateMenuText();
        }
    }

    /// <summary>Sets what the view's camera draws from its render mode and View > Show > Actor Icons.</summary>
    public void ApplyCullMask() => Cam.Camera.CullMask = VisualLayers.ForView(Kind, _mode, Render.ActorIconBatch.Shown);

    /// <summary>Asks the overlay to redraw (tools call this when their handles change).</summary>
    public void Redraw() => _overlay.QueueRedraw();

    public override void _Ready()
    {
        Resized += OnResized;
        // A UI scale change can leave the view's size in UI pixels unchanged while its rendered size changes.
        GetViewport().SizeChanged += OnResized;
        MouseEntered += () => Activated?.Invoke(this);
        OnResized();
    }

    public void ApplyCamera()
    {
        Cam.Clip = Kind == ViewKind.Top && StoreyFilter.Current is { } s ? (s.Bottom, s.Top) : null;
        Cam.Apply();
        _grid.Refresh(Cam, ToolManager.Instance?.GridSize ?? 16, ShowGrid);
        RefreshReferences();
        _overlay.QueueRedraw();
        CameraChanged?.Invoke(this);
    }

    /// <summary>Redraws the reference images when the map's references changed (cheap when they did not).</summary>
    public void RefreshReferences()
    {
        if (Cam.IsOrtho)
        {
            Core.EditorContext ctx = Core.EditorContext.Instance;
            _references.Refresh(Cam, ctx.Project.Workspace.References, ctx.Session.Folder);
        }
    }

    /// <summary>Takes new colours and field of view from Preferences.</summary>
    public void Restyle()
    {
        Cam.Camera.Environment = MakeEnvironment(_mode);
        _grid.Invalidate();
        ApplyCamera();
    }

    public override void _Process(double delta)
    {
        if (!_flying)
        {
            return;
        }

        var move = Vec3.Zero;
        (Vec3 f, Vec3 r, Vec3 _) = Cam.Axes;
        if (Input.IsPhysicalKeyPressed(Key.W))
        {
            move += f;
        }

        if (Input.IsPhysicalKeyPressed(Key.S))
        {
            move -= f;
        }

        if (Input.IsPhysicalKeyPressed(Key.D))
        {
            move += r;
        }

        if (Input.IsPhysicalKeyPressed(Key.A))
        {
            move -= r;
        }

        if (Input.IsPhysicalKeyPressed(Key.E))
        {
            move += Vec3.UnitZ;
        }

        if (Input.IsPhysicalKeyPressed(Key.Q))
        {
            move -= Vec3.UnitZ;
        }

        if (move.LengthSquared > 0)
        {
            _rightMoved = true;
            double speed = FlySpeed * (Input.IsKeyPressed(Key.Shift) ? 4 : 1);
            Cam.Location += move.Normalized() * (speed * delta);
            ApplyCamera();
        }
    }

    public override void _Input(InputEvent e)
    {
        // While flying the mouse is captured: look around from raw motion, whatever control is under it. A hand moves
        // the mouse a little during any click, so the look starts only once the pointer has really moved; until then a
        // release is a right click.
        if (_flying && e is InputEventMouseMotion motion)
        {
            GetViewport().SetInputAsHandled();
            if (DragStarted(motion.Relative, LookClickSlop) is { } look)
            {
                Cam.Yaw += look.X * 18;
                Cam.Pitch = Math.Clamp(Cam.Pitch - look.Y * 18, -16000, 16000);
                ApplyCamera();
            }
        }
        else if (_flying && e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
        {
            StopFlying();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } rp:
                GrabFocus();
                Activated?.Invoke(this);
                _rightHeld = true;
                _rightPress = rp.Position;
                _rightMoved = false;
                _rightPending = Vector2.Zero;
                if (Cam.IsOrtho)
                {
                    _panning = true;
                }
                else
                {
                    StartFlying();
                }

                AcceptEvent();
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } rr:
                _panning = false;
                _rightHeld = false;
                if (!_rightMoved)
                {
                    ContextMenuRequested?.Invoke(this, rr.Position);
                }

                AcceptEvent();
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _panning = mb.Pressed;
                AcceptEvent();
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
                Wheel(wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1, wheel.Position);
                AcceptEvent();
                return;
            case InputEventMouseMotion motion when _panning:
                // A right-button pan waits out a click's wobble, as the look does; a middle-button pan starts at once.
                if (!_rightHeld)
                {
                    Pan(motion.Relative);
                }
                else if (DragStarted(motion.Relative, ClickSlop) is { } pan)
                {
                    Pan(pan);
                }

                AcceptEvent();
                return;
            case InputEventKey when _flying:
                // Flying owns WASD/QE and Shift; keep them away from the shortcut router.
                AcceptEvent();
                return;
            case InputEventKey { Pressed: true, Keycode: Key.Escape } when ToolManager.Instance?.Active.Cancel() == true:
                AcceptEvent();
                return;
        }

        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            GrabFocus();
            Activated?.Invoke(this);
        }

        if (e is InputEventMouseMotion mm)
        {
            CursorMoved?.Invoke(this, CursorPoint(mm.Position));
        }

        if (ToolManager.Instance?.Active is { } tool && tool.Input(this, e))
        {
            AcceptEvent();
        }
    }

    /// <summary>The pointer left the view: the active tool drops its hover preview.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && ToolManager.Instance?.Active is { } tool)
        {
            tool.PointerLeft(this);
        }
    }

    /// <summary>Assets dragged over the view (a mesh shows its ghost where it would land): <see cref="AssetDropTarget"/>.</summary>
    public override bool _CanDropData(Vector2 atPosition, Variant data) => AssetDropTarget.Instance?.CanDrop(this, atPosition, data) ?? false;

    public override void _DropData(Vector2 atPosition, Variant data) => AssetDropTarget.Instance?.Drop(this, atPosition, data);

    /// <summary>The world point under a screen point: the surface hit in perspective, the view plane in 2D (at depth 0,
    /// or in the Top view at the floor of the storey shown, so a drop lands on the storey being worked on).</summary>
    public Vec3? CursorPoint(Vector2 screen)
    {
        if (ToolManager.Instance is not { } tools)
        {
            return null;
        }

        if (Cam.IsOrtho)
        {
            int depth = ViewAxes.DepthAxis(Kind);
            double plane = Kind == ViewKind.Top && StoreyFilter.Current is { } storey ? storey.Level : 0;
            return tools.Snap(Cam.PlanePoint(screen, plane), [.. new[] { 0, 1, 2 }.Where(a => a != depth)]);
        }

        return Picking.Surface(Cam, tools.Context.Project, screen)?.Point;
    }

    /// <summary>Adds right-button motion to the gesture. Returns the motion to apply: nothing while the gesture is still
    /// within <paramref name="slop"/> of where it started (a click), everything held back once it leaves it, then each
    /// motion as it comes.</summary>
    private Vector2? DragStarted(Vector2 relative, float slop)
    {
        if (_rightMoved)
        {
            return relative;
        }

        _rightPending += relative;
        if (_rightPending.Length() < slop)
        {
            return null;
        }

        _rightMoved = true;
        return _rightPending;
    }

    private void StartFlying()
    {
        _flying = true;
        ShortcutRouter.Suspended = true;
        _restoreMouse = EditorPointer.Global(this);
        EditorPointer.Mode = Input.MouseModeEnum.Captured;
    }

    private void StopFlying()
    {
        _flying = false;
        _rightHeld = false;
        ShortcutRouter.Suspended = false;
        EditorPointer.Mode = Input.MouseModeEnum.Visible;
        EditorPointer.Warp(this, _restoreMouse);
        if (!_rightMoved)
        {
            ContextMenuRequested?.Invoke(this, _rightPress);
        }
    }

    private void Wheel(int direction, Vector2 at)
    {
        if (Cam.IsOrtho)
        {
            Vec3 before = Cam.PlanePoint(at, 0);
            Cam.UnitsPerPixel = Math.Clamp(Cam.UnitsPerPixel * (direction > 0 ? 1 / 1.2 : 1.2), 0.02, 512);
            Cam.Apply();
            // Keep the point under the cursor fixed while zooming.
            Vec3 after = Cam.PlanePoint(at, 0);
            Cam.Center += before - after;
        }
        else if (_flying)
        {
            _rightMoved = true;
            FlySpeed = Math.Clamp(FlySpeed * (direction > 0 ? 1.25 : 0.8), 50, 20000);
            ToolManager.Instance?.Context.SetStatus($"Camera speed {FlySpeed:0}");
        }
        else
        {
            Cam.Location += Cam.Axes.Forward * (direction * FlySpeed * 0.25);
        }

        ApplyCamera();
    }

    private void Pan(Vector2 relative)
    {
        if (Cam.IsOrtho)
        {
            (Vec3 right, Vec3 up) = ViewAxes.For(Kind)!.Value;
            Cam.Center -= (right * relative.X - up * relative.Y) * Cam.UnitsPerPixel;
        }
        else
        {
            (Vec3 _, Vec3 r, Vec3 u) = Cam.Axes;
            double scale = FlySpeed / 400;
            Cam.Location += (u * relative.Y - r * relative.X) * scale;
        }

        ApplyCamera();
    }

    private void OnResized()
    {
        float scale = GetViewport().GetFinalTransform().Scale.X;
        _container.Scale = Vector2.One / scale;
        _container.Size = (Size * scale).Round();
        Cam.PixelScale = scale;
        if (Kind == ViewKind.Perspective)
        {
            Render.ActorIconBatch.SetPixelScale(scale);
        }

        Cam.Resize(Size);
        ApplyCamera();
    }

    /// <summary>The background and ambient light of a view in <paramref name="mode"/> (the camera monitor shares it).</summary>
    internal static Godot.Environment MakeEnvironment(RenderMode mode) => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = mode is RenderMode.Unlit or RenderMode.Lit or RenderMode.LightmapDensity ? SceneStyle.PerspectiveBackground : SceneStyle.OrthoBackground,
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(1, 1, 1),
        AmbientLightEnergy = mode == RenderMode.Lit ? 0.12f : 1f,
        TonemapMode = Godot.Environment.ToneMapper.Linear,
    };

    private void UpdateMenuText()
    {
        string mode = _mode switch
        {
            RenderMode.Wireframe => "Wireframe",
            RenderMode.BrushColors => "Brush colours",
            RenderMode.Unlit => "Unlit",
            RenderMode.LightmapDensity => "Lightmap density",
            _ => "Lit",
        };
        _menu.Text = $"{Kind} · {mode}";
    }

    private void FillMenu()
    {
        PopupMenu p = _menu.GetPopup();
        p.Clear();
        EditorContext? ctx = ToolManager.Instance?.Context;
        void Keys(int id, string actionId)
        {
            if (ctx is not null)
            {
                MenuKeys.Show(p, p.GetItemIndex(id), ctx, actionId);
            }
        }

        foreach (RenderMode m in Enum.GetValues<RenderMode>())
        {
            p.AddRadioCheckItem(ModeTitle(m), (int)m);
        }

        p.AddSeparator();
        p.AddCheckItem("Grid", 100);
        p.AddSeparator();
        p.AddCheckItem("Maximize View", 101);
        p.AddItem("Frame Selection", 102);
        SyncMenu();
        foreach (RenderMode m in Enum.GetValues<RenderMode>())
        {
            Keys((int)m, "view.render." + m.ToString().ToLowerInvariant());
        }

        Keys(101, "view.maximize");
        Keys(102, "view.frame");
    }

    /// <summary>The view menu's marks and Frame Selection's state, set when it opens and again after each toggle it
    /// stays open for.</summary>
    private void SyncMenu()
    {
        PopupMenu p = _menu.GetPopup();
        foreach (RenderMode m in Enum.GetValues<RenderMode>())
        {
            p.SetItemChecked(p.GetItemIndex((int)m), m == _mode);
        }

        p.SetItemChecked(p.GetItemIndex(100), ShowGrid);
        p.SetItemChecked(p.GetItemIndex(101), ViewportLayout.Instance?.Single is not null);
        p.SetItemDisabled(p.GetItemIndex(102), ToolManager.Instance?.Context.Selection.Count is null or 0);
    }

    /// <summary>A render mode as the view menu lists it (Title Case).</summary>
    private static string ModeTitle(RenderMode m) => m switch
    {
        RenderMode.BrushColors => "Brush Colours",
        RenderMode.LightmapDensity => "Lightmap Density",
        _ => m.ToString(),
    };

    private void OnMenu(long id)
    {
        switch (id)
        {
            case < 100:
                Mode = (RenderMode)id;
                ViewportLayout.Instance?.ModesChanged();
                break;
            case 100:
                ShowGrid = !ShowGrid;
                ApplyCamera();
                break;
            case 101:
                MaximizeToggled?.Invoke(this);
                break;
            case 102:
                Activated?.Invoke(this);
                ToolManager.Instance?.Context.Run("view.frame");
                break;
        }
    }
}
