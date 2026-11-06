using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The middle of the window: four views (perspective, top, front, side) over one shared scene, a single-view mode
/// (Ctrl+Shift+1..5, or maximise the active view), the View menu (render modes, show flags, framing) and the viewport
/// context menu. It keeps the scene in step with the map: a full rebuild when another map opens, an incremental sync
/// after edits (once per frame however many edits came in). It also serves the rest of the editor: the workspace
/// camera follows the perspective view, saved views and jumps move the cameras, and new objects go to the surface
/// under the centre of the active view (<see cref="EditorContext.PlacementPoint"/>).
/// </summary>
[ShellPart(Order = -20)]
public sealed partial class ViewportLayout : Node, IShellPart
{
    private readonly List<EditorViewport> _views = new();
    private EditorContext _ctx = null!;
    private ToolManager _tools = null!;
    private WorldScene _scene = null!;
    private VBoxContainer _rows = null!;
    private HBoxContainer _topRow = null!;
    private HBoxContainer _bottomRow = null!;
    private PopupMenu _context = null!;
    private readonly List<string> _contextIds = new();
    private bool _dirty;
    private bool _mountsDirty;
    private bool _selectionDirty;
    private EditorViewport? _contextView;
    private Vector2 _contextAt;
    private OpenScct.Editor.Core.Scripting.WorldGeometry? _geometry;
    private EditorSession? _cameraSession;

    public static ViewportLayout? Instance { get; private set; }

    public IReadOnlyList<EditorViewport> Views => _views;

    public EditorViewport Active { get; private set; } = null!;

    /// <summary>The single view shown, or null for all four.</summary>
    public ViewKind? Single { get; private set; }

    /// <summary>The world point under the cursor in the last view it moved over (grid snapped in 2D views).</summary>
    public Vec3? CursorWorld { get; private set; }

    /// <summary>Show the builder brush even when the brush tool is not active.</summary>
    public bool ShowBuilder { get; set; }

    public WorldScene Scene => _scene;

    /// <summary>An edit has landed but has not yet been synchronized into the drawn scene.</summary>
    public bool SceneSyncPending => _dirty;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        _tools = ToolManager.Instance!;
        Name = "ViewportLayout";
        window.AddChild(this);

        _scene = new WorldScene { Name = "WorldScene" };
        window.AddChild(_scene);
        // What View > Show hides is not picked either.
        Picking.IsShown = kind => kind switch
        {
            ObjectKind.Brush => _scene.ShowBrushes,
            ObjectKind.Actor => _scene.ShowActors,
            _ => _scene.ShowGes,
        };
        _scene.AddChild(new PickedFacesNode { Name = "PickedFaces" });

        _rows = new VBoxContainer { Name = "Views" };
        _rows.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _rows.AddThemeConstantOverride("separation", 2);
        window.ViewportArea.AddChild(_rows);
        _topRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _bottomRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _topRow.AddThemeConstantOverride("separation", 2);
        _bottomRow.AddThemeConstantOverride("separation", 2);
        _rows.AddChild(_topRow);
        _rows.AddChild(_bottomRow);
        foreach (ViewKind kind in new[] { ViewKind.Perspective, ViewKind.Top, ViewKind.Front, ViewKind.Side })
        {
            var view = new EditorViewport(kind);
            (kind is ViewKind.Perspective or ViewKind.Top ? _topRow : _bottomRow).AddChild(view);
            view.Activated += SetActive;
            view.CursorMoved += (_, p) => CursorWorld = p;
            view.ContextMenuRequested += ShowContextMenu;
            view.MaximizeToggled += v => SetSingle(Single is null ? v.Kind : null);
            view.CameraChanged += OnCameraChanged;
            string saved = AppSettings.GetString("views", kind.ToString(), "");
            if (Enum.TryParse(saved, out RenderMode mode))
            {
                view.Mode = mode;
            }

            _views.Add(view);
        }

        Active = _views[0];
        Active.IsActiveView = true;
        _context = new PopupMenu { Name = "ViewContextMenu" };
        _context.IdPressed += id => RunContext((int)id);
        MenuToggles.KeepOpen(_context, () => MenuToggles.SyncActions(_context, _contextIds, _ctx));
        window.AddChild(_context);

        RegisterActions();
        context.SessionReplaced += OnSessionReplaced;
        context.ProjectChanged += command =>
        {
            _dirty = true;
            _geometry = null;
            StoreyFilter.ObjectsChanged();
            // Explicit workspace camera edits (including undo/redo) move the view. Other workspace
            // commands restore an incidental camera snapshot: preserve the user's current view instead.
            if (ReferenceEquals(_cameraSession, _ctx.Session))
            {
                if (ChangesCamera(command))
                {
                    Workspace w = _ctx.Project.Workspace;
                    ViewFrom(w.CameraLocation, w.CameraRotation);
                }
                else
                {
                    StoreCamera();
                }
            }
        };
        context.SelectionChanged += () => _selectionDirty = true;
        // Camera wall mounts trace the traversal world, which is built in the background after the scene is drawn.
        Gameplay.GameplayState.Instance.Changed += () => _mountsDirty = true;
        StoreyFilter.Changed += OnStoreyChanged;
        context.FocusRequested += refs => FrameAll(ObjectGeometry.Bounds(_ctx.Project, refs));
        context.JumpRequested += p => _views.ForEach(v =>
        {
            v.Cam.LookAt(p);
            v.ApplyCamera();
        });
        context.ViewRequested += ViewFrom;
        context.PlacementPoint = ViewCentrePoint;
        _tools.Changed += OnToolsChanged;
        Documents.BeforeSave += StoreCamera;

        string single = AppSettings.GetString("views", "single", "");
        SetSingle(Enum.TryParse(single, out ViewKind s) ? s : null);
        ModesChanged();
        OnSessionReplaced();
    }

    public override void _Process(double delta)
    {
        if (_mountsDirty)
        {
            _mountsDirty = false;
            _dirty |= _scene.MarkMountsChanged(_ctx.Project);
        }

        if (_dirty)
        {
            _dirty = false;
            _scene.Sync(_ctx.Project);
            _views.ForEach(v => v.RefreshReferences());
            _selectionDirty = true;
        }

        if (_selectionDirty)
        {
            _selectionDirty = false;
            _scene.ApplySelection(_ctx.Selection.Items);
            RedrawAll();
        }
    }

    public void RedrawAll() => _views.ForEach(v => v.Redraw());

    /// <summary>Redraws every view with the grid and field of view Preferences set now; <paramref name="colours"/> also
    /// draws the map's objects again in the current colours (slower: every brush, actor and GE is rebuilt).</summary>
    public void Restyle(bool colours)
    {
        if (colours)
        {
            SceneStyle.ResetMaterials();
            _scene.Redraw(_ctx.Project);
            _scene.ApplySelection(_ctx.Selection.Items);
            OnToolsChanged();
        }

        _views.ForEach(v => v.Restyle());
    }

    /// <summary>Another storey is shown: views hide what is outside it and the Top view cuts to its height.</summary>
    private void OnStoreyChanged()
    {
        Render.StoreyClip.Show(StoreyFilter.Current);
        _scene.ApplyVisibility(_ctx.Project);
        _views.ForEach(v => v.ApplyCamera());
        _selectionDirty = true;
    }

    /// <summary>Call after a view's render mode changed (persists it; the CSG preview runs only when needed).</summary>
    public void ModesChanged()
    {
        _scene.SurfacesNeeded = _views.Any(v => v.Visible && v.Mode is RenderMode.Unlit or RenderMode.Lit or RenderMode.LightmapDensity);
        _scene.LitNeeded = _views.Any(v => v.Visible && v.Mode is RenderMode.Lit or RenderMode.LightmapDensity);
        foreach (EditorViewport v in _views)
        {
            AppSettings.Set("views", v.Kind.ToString(), v.Mode.ToString());
        }
    }

    /// <summary>Shows one view alone, or all four (null).</summary>
    public void SetSingle(ViewKind? kind)
    {
        Single = kind;
        foreach (EditorViewport v in _views)
        {
            v.Visible = kind is null || v.Kind == kind;
        }

        _topRow.Visible = _topRow.GetChildren().OfType<EditorViewport>().Any(v => v.Visible);
        _bottomRow.Visible = _bottomRow.GetChildren().OfType<EditorViewport>().Any(v => v.Visible);
        if (kind is { } k)
        {
            SetActive(_views.First(v => v.Kind == k));
        }

        AppSettings.Set("views", "single", kind?.ToString() ?? "");
        ModesChanged();
    }

    /// <summary>Frames a box in every view (F, outliner double-click, Map Check "go to").</summary>
    public void FrameAll((Vec3 Min, Vec3 Max)? bounds)
    {
        if (bounds is not { } b)
        {
            return;
        }

        foreach (EditorViewport v in _views)
        {
            v.Cam.Frame(b.Min, b.Max);
            v.ApplyCamera();
        }
    }

    private void SetActive(EditorViewport view)
    {
        if (ReferenceEquals(Active, view))
        {
            return;
        }

        Active.IsActiveView = false;
        Active.Redraw();
        Active = view;
        view.IsActiveView = true;
        view.Redraw();
    }

    private void OnSessionReplaced()
    {
        _geometry = null;
        _cameraSession = null;
        _scene.Rebuild(_ctx.Project, _ctx.Session.Folder);
        _scene.ApplySelection(_ctx.Selection.Items);
        Workspace w = _ctx.Project.Workspace;
        EditorViewport persp = _views[0];
        persp.Cam.Location = w.CameraLocation;
        persp.Cam.Yaw = w.CameraRotation.Yaw;
        persp.Cam.Pitch = w.CameraRotation.Pitch;
        _cameraSession = _ctx.Session;
        persp.ApplyCamera();
        (Vec3 Min, Vec3 Max)? bounds = ObjectGeometry.MainBounds(_ctx.Project, _ctx.Project.AllRefs(), out int leftOut);
        if (leftOut > 0)
        {
            _ctx.Log($"The views show the main part of the map; {leftOut} {(leftOut == 1 ? "object lies" : "objects lie")} " +
                "far outside it. Frame All (Shift+F) shows everything.");
        }

        foreach (EditorViewport v in _views.Where(v => v.Cam.IsOrtho))
        {
            if (bounds is { } b)
            {
                v.Cam.Frame(b.Min, b.Max);
            }

            v.ApplyCamera();
        }

        OnToolsChanged();
    }

    private void OnToolsChanged()
    {
        bool showBuilder = ShowBuilder || _tools.Active is BrushTool;
        BuilderBrush b = _tools.Builder;
        _scene.SetBuilder(showBuilder ? b.DisplayShape() : null, b.Location, b.Rotation);
        _views.ForEach(v => v.ApplyCamera());
    }

    private void StoreCamera()
    {
        ViewCamera cam = _views[0].Cam;
        _ctx.Project.Workspace.CameraLocation = cam.Location;
        _ctx.Project.Workspace.CameraRotation = cam.Rotation;
    }

    private static bool ChangesCamera(IEditCommand command) => command switch
    {
        WorkspaceCommand workspace => workspace.ChangesCamera,
        CompositeCommand composite => composite.Parts.Any(ChangesCamera),
        _ => false,
    };

    /// <summary>Keeps the workspace camera (bookmarks, placement fallbacks, the saved view) on the perspective view.
    /// Until the open map's own camera has been applied, the old camera must not overwrite it.</summary>
    private void OnCameraChanged(EditorViewport view)
    {
        if (view.Kind == ViewKind.Perspective && ReferenceEquals(_cameraSession, _ctx.Session))
        {
            StoreCamera();
        }
    }

    /// <summary>Moves the perspective camera to a saved view and centres the 2D views on it (bookmarks).</summary>
    private void ViewFrom(Vec3 location, Rot rotation)
    {
        EditorViewport persp = _views[0];
        persp.Cam.Location = location;
        persp.Cam.Yaw = rotation.Yaw;
        persp.Cam.Pitch = rotation.Pitch;
        persp.ApplyCamera();
        foreach (EditorViewport v in _views.Where(v => v.Cam.IsOrtho))
        {
            v.Cam.LookAt(location);
            v.ApplyCamera();
        }
    }

    /// <summary>Where new objects go: the first surface (brushes or an imported map's baked collision) under the
    /// centre of the active view; with nothing there, a point in front of the perspective camera, or the 2D view's
    /// centre on its plane. With a storey shown, the point is on that storey (the Top view looks down from its top), so
    /// a new object never lands on a floor the filter hides.</summary>
    private Vec3 ViewCentrePoint() => OnShownStorey(SurfaceAtViewCentre());

    private static Vec3 OnShownStorey(Vec3 point) =>
        StoreyFilter.Current is { } storey && !storey.Contains(point.Z) ? point.With(2, storey.Level) : point;

    private Vec3 SurfaceAtViewCentre()
    {
        // The centre ray comes from the camera state itself, so it is right even before a view has a size.
        ViewCamera cam = (Active.Visible ? Active : _views[0]).Cam;
        Vec3 origin, dir;
        double reach;
        if (cam.IsOrtho)
        {
            // A 2D view looks along its depth axis (Top: down); cast from just outside the map's objects.
            int depth = ViewAxes.DepthAxis(cam.Kind);
            (Vec3 right, Vec3 up) = ViewAxes.For(cam.Kind)!.Value;
            dir = Vec3.Cross(right, up).Normalized();
            Vec3 onPlane = cam.Center.With(depth, 0);
            if (ObjectGeometry.Bounds(_ctx.Project, _ctx.Project.AllRefs()) is not { } bounds)
            {
                return onPlane;
            }

            double from = dir[depth] < 0 ? bounds.Max[depth] + 64 : bounds.Min[depth] - 64;
            reach = bounds.Max[depth] - bounds.Min[depth] + 128;
            if (cam.Kind == ViewKind.Top && StoreyFilter.Current is { } storey)
            {
                from = Math.Min(from, storey.Top - 1);
                reach = Math.Max(from - storey.Bottom, 1);
            }

            origin = onPlane.With(depth, from);
        }
        else
        {
            (origin, dir, reach) = (cam.Location, cam.Axes.Forward, 16384);
        }

        _geometry ??= new OpenScct.Editor.Core.Scripting.WorldGeometry(_ctx.Project, _ctx.Session.Folder);
        OpenScct.Editor.Core.Scripting.RayHit? hit = _geometry.Raycast(origin, dir, reach);
        // An imported map's baked world is solid outside its rooms, so a ray from out there hits at once: walk along
        // the ray until it starts in open space.
        const double Step = 64;
        for (double t = Step; hit is { Distance: < 1 } && t < reach; t += Step)
        {
            hit = _geometry.Raycast(origin + dir * t, dir, reach - t);
        }

        if (hit is { Distance: >= 1 } h)
        {
            return h.Point;
        }

        return cam.IsOrtho ? cam.Center.With(ViewAxes.DepthAxis(cam.Kind), 0) : cam.Location + dir * 512;
    }

    private void RegisterActions()
    {
        (string Id, string Title, ViewKind? Kind)[] layouts =
        [
            ("view.layout.quad", "4 Views", null),
            ("view.layout.perspective", "Perspective", ViewKind.Perspective),
            ("view.layout.top", "Top (XY)", ViewKind.Top),
            ("view.layout.front", "Front (XZ)", ViewKind.Front),
            ("view.layout.side", "Side (YZ)", ViewKind.Side),
        ];
        for (int i = 0; i < layouts.Length; i++)
        {
            ViewKind? kind = layouts[i].Kind;
            _ctx.AddAction(new EditorAction(layouts[i].Id, layouts[i].Title, "View/Layout", () => SetSingle(kind))
            {
                // Plain 1..9 jump to camera bookmarks and Ctrl+1..9 store them (RE+), so layouts take Ctrl+Shift.
                Shortcut = "Ctrl+Shift+" + (i + 1).ToString(CultureInfo.InvariantCulture),
                Order = 100 + i,
                RadioGroup = "view.layout",
                IsChecked = () => Single == kind,
            });
        }

        _ctx.AddAction(new EditorAction("view.maximize", "Maximize View", "View", () => SetSingle(Single is null ? Active.Kind : null))
        {
            Shortcut = "Ctrl+Space", Order = 220, IsChecked = () => Single is not null, Description = "The active view fills the view area",
        });

        (RenderMode Mode, string Title)[] modes =
        [
            (RenderMode.Wireframe, "Wireframe"),
            (RenderMode.BrushColors, "Brush Colours (CSG)"),
            (RenderMode.Unlit, "Unlit"),
            (RenderMode.Lit, "Lit Preview"),
            (RenderMode.LightmapDensity, "Lightmap Density"),
        ];
        for (int i = 0; i < modes.Length; i++)
        {
            RenderMode m = modes[i].Mode;
            _ctx.AddAction(new EditorAction("view.render." + m.ToString().ToLowerInvariant(), modes[i].Title, "View/Render Mode", () =>
            {
                Active.Mode = m;
                ModesChanged();
            })
            {
                Shortcut = "Alt+" + (i + 1).ToString(CultureInfo.InvariantCulture),
                Order = 100 + i,
                RadioGroup = "view.render",
                IsChecked = () => Active.Mode == m,
                Description = "For the active view",
            });
        }

        _ctx.AddAction(new EditorAction("view.frame", "Frame Selection", "View", () =>
            FrameAll(ObjectGeometry.Bounds(_ctx.Project, _ctx.Selection.Items)))
        {
            Shortcut = "F", Order = 200, IsEnabled = () => _ctx.Selection.Count > 0,
        });
        _ctx.AddAction(new EditorAction("view.frameAll", "Frame All", "View", () =>
            FrameAll(ObjectGeometry.Bounds(_ctx.Project, ObjectFilter.Pickable(_ctx.Project))))
        {
            Shortcut = "Shift+F", Order = 210,
        });
        _ctx.AddAction(new EditorAction("view.grid.show", "Show Grid", "View/Grid", () =>
        {
            bool show = !Active.ShowGrid;
            _views.ForEach(v =>
            {
                v.ShowGrid = show;
                v.ApplyCamera();
            });
        })
        {
            Order = 300, IsChecked = () => Active.ShowGrid,
        });

        AddShow("view.show.brushes", "Brushes", () => _scene.ShowBrushes, v => _scene.ShowBrushes = v, 100);
        AddShow("view.show.actors", "Actors", () => _scene.ShowActors, v => _scene.ShowActors = v, 110);
        _ctx.AddAction(new EditorAction("view.show.actorIcons", "Actor Icons", "View/Show", () =>
        {
            Render.ActorIconBatch.Shown = Preferences.ActorIcons = !Render.ActorIconBatch.Shown;
            _views.ForEach(v => v.ApplyCullMask());
            _scene.RefreshIcons();
        })
        {
            Order = 115, IsChecked = () => Render.ActorIconBatch.Shown, IsEnabled = () => _scene.ShowActors,
            Description = "In the perspective view, draw actors without a mesh (lights, starts, triggers…) as icons instead of boxes",
        });
        AddShow("view.show.ges", "Geometric Events", () => _scene.ShowGes, v => _scene.ShowGes = v, 120);
        AddShow("view.show.baked", "Baked World", () => _scene.ShowBaked, v => _scene.ShowBaked = v, 130);
        _ctx.AddAction(new EditorAction("view.show.offTriggerLights", "Trigger Lights That Start Off", "View/Show",
            () => _scene.ShowOffTriggerLights = !_scene.ShowOffTriggerLights)
        {
            Order = 135, IsChecked = () => _scene.ShowOffTriggerLights,
            Description = "Light the views with the trigger lights that start the round switched off, as if an event had turned them on",
        });
        _ctx.AddAction(new EditorAction("view.show.builder", "Builder Brush", "View/Show", () =>
        {
            ShowBuilder = !ShowBuilder;
            OnToolsChanged();
        })
        {
            Order = 140, IsChecked = () => ShowBuilder,
        });
        _ctx.AddAction(new EditorAction("view.refresh", "Redraw Everything", "View", () =>
        {
            _scene.Rebuild(_ctx.Project, _ctx.Session.Folder);
            _scene.ApplySelection(_ctx.Selection.Items);
        })
        {
            Order = 900,
        });
    }

    private void AddShow(string id, string title, Func<bool> get, Action<bool> set, int order) =>
        _ctx.AddAction(new EditorAction(id, title, "View/Show", () =>
        {
            set(!get());
            _scene.ApplyVisibility(_ctx.Project);
        })
        {
            Order = order, IsChecked = get,
        });

    /// <summary>The right-click menu: surface commands when faces are picked in face mode, selection commands when
    /// something is selected, then placing at the click. With nothing selected, the object under the click is
    /// selected first (as in the stock editor and RE+), so the menu acts on it; in face mode, the face under the
    /// click is picked unless it already is.</summary>
    private void ShowContextMenu(EditorViewport view, Vector2 at)
    {
        _contextView = view;
        _contextAt = at;
        CursorWorld = view.CursorPoint(at);
        bool faceMode = _tools.Active is FaceTool;
        GeometrySelection geo = GeometrySelection.Instance;
        if (faceMode)
        {
            // The clicked face is picked unless it already is, so a right click on one of several picked faces
            // keeps them all; a click on no face keeps whatever is picked.
            if (FacePicking.Pick(view.Cam, _ctx.Project, at) is { } face && !geo.HasFace(face.Face))
            {
                geo.SetFaces([face.Face]);
                SurfaceCommands.Report();
                RedrawAll();
            }
        }
        else if (_ctx.Selection.Count == 0 && Picking.Pick(view.Cam, _ctx.Project, at) is { } hit)
        {
            _ctx.Selection.Set([hit]);
        }

        _context.Clear();
        _contextIds.Clear();
        void Item(string id, string? title = null)
        {
            if (_ctx.FindAction(id) is not { } a)
            {
                return;
            }

            // A choice the selection already has shows as a checked radio item (the brush's CSG type and solidity).
            if (a.IsChecked is not null)
            {
                _context.AddRadioCheckItem(title ?? a.Title, _contextIds.Count);
                _context.SetItemChecked(_context.GetItemIndex(_contextIds.Count), a.IsChecked());
            }
            else
            {
                _context.AddItem(title ?? a.Title, _contextIds.Count);
            }

            int index = _context.GetItemIndex(_contextIds.Count);
            _context.SetItemDisabled(index, !a.IsEnabled());
            MenuKeys.Show(_context, index, _ctx, id);
            _contextIds.Add(id);
        }

        void Separator()
        {
            if (_context.ItemCount > 0)
            {
                _context.AddSeparator();
            }
        }

        // The surface tool leads with surface commands for the picked faces.
        if (faceMode && geo.Faces.Count > 0)
        {
            Item("surface.properties");
            Item("surface.applyMaterial");
            Separator();
            Item("surface.selectSameMaterial", "Select Same Material");
            Item("surface.selectCoplanar", "Select Coplanar");
            Item("surface.selectAdjacent", "Select Adjacent");
            Item("surface.selectAllFaces", "Select All Faces of Brush");
            Item("surface.selectBrush");
            Item("surface.clearFaces");
            Separator();
            Item("surface.copy");
            Item("surface.paste");
            Item("surface.pasteAlignment");
            Separator();
            Item("surface.alignWall", "Align to Wall");
            Item("surface.alignFloor", "Align to Floor");
            Item("surface.alignFit", "Fit Texture");
        }

        if (_ctx.Selection.Count > 0)
        {
            Separator();
            Item("view.frame");
            Item("edit.duplicate");
            Item("edit.copy");
            Item("edit.cut");
            Item("edit.delete");
            Item("edit.selectSameClass");
            if (_ctx.FindAction("emitter.saveSelection")?.IsEnabled() == true)
            {
                Item("emitter.saveSelection", "Save to Emitter Library…");
            }

            if (_ctx.Selection.Items.Any(r => r.Kind == ObjectKind.Brush))
            {
                Separator();
                Item("brush.toBuilder");
                Separator();
                Item("brush.makeAdd");
                Item("brush.makeSubtract");
                Separator();
                Item("brush.solid");
                Item("brush.semisolid");
                Item("brush.nonsolid");
                Separator();
                Item("brush.orderFirst", "Move to First in CSG Order");
                Item("brush.orderLast", "Move to Last in CSG Order");
            }
        }

        Separator();
        string placeClass = _tools.PlaceClass;
        _context.AddItem($"Place {placeClass[(placeClass.LastIndexOf('.') + 1)..]} Here", _contextIds.Count);
        _context.SetItemTooltip(_context.GetItemIndex(_contextIds.Count), $"Places a {placeClass} where you clicked (the place tool's class)");
        _contextIds.Add("ctx.placeHere");
        Item("brush.builderHere");
        Item("edit.pasteHere");
        Item("edit.paste");
        Separator();
        Item("edit.selectAll");
        Item("edit.selectNone");
        Item("view.frameAll");
        if (view.Kind == ViewKind.Perspective)
        {
            Separator();
            Item("build.playFromHere", "Play From Here");
            Item("build.playFromCamera", "Play From Camera");
        }

        _context.Position = PanelUi.ScreenPoint(view, at);
        _context.ResetSize();
        _context.Popup();
    }

    private void RunContext(int id)
    {
        if (id < 0 || id >= _contextIds.Count)
        {
            return;
        }

        if (_contextIds[id] == "ctx.placeHere")
        {
            if (_contextView is { } view && _tools.Tools.OfType<PlaceActorTool>().FirstOrDefault() is { } place)
            {
                place.PlaceAt(view, _contextAt);
            }

            return;
        }

        _ctx.Run(_contextIds[id]);
    }
}
