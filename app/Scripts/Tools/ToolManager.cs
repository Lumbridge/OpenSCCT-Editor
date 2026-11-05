using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>Which kind of drag asks for neighbour snap (each can be turned off on its own).</summary>
public enum SnapUse
{
    Move,
    Resize,
    Rotate,
}

/// <summary>
/// The active viewport tool and the shared tool state: grid, rotation and neighbour snapping, the class to place, the GE
/// kind to draw and the builder brush. Registers the Tools menu (Q/W/E/R...), the grid actions (G, [ and ]) and
/// neighbour snap (Shift+G).
/// </summary>
[ShellPart(Order = -30)]
public sealed class ToolManager : IShellPart
{
    public static readonly double[] GridSizes = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];
    public static readonly int[] RotationGrids = [512, 1024, 2048, 4096, 8192, 16384];

    private readonly List<IViewportTool> _tools = new();
    private EditorContext _ctx = null!;
    private HBoxContainer? _options;
    private bool _dragStep;

    public static ToolManager? Instance { get; private set; }

    public IViewportTool Active { get; private set; } = null!;

    public IReadOnlyList<IViewportTool> Tools => _tools;

    public EditorContext Context => _ctx;

    public BuilderBrush Builder { get; } = new();

    /// <summary>Raised when the tool or a tool setting changes (views redraw, toolbar refreshes).</summary>
    public event Action? Changed;

    /// <summary>Raised when the map changes outside a <see cref="DragStep"/> (undo, a script, the grid keys) or another
    /// map opens. Tools end a running drag where it is, so a later Escape never undoes someone else's edit.</summary>
    public event Action? DragInterrupted;

    public bool GridSnap
    {
        get => AppSettings.GetBool("tools", "grid_snap", true);
        set
        {
            AppSettings.Set("tools", "grid_snap", value);
            Changed?.Invoke();
        }
    }

    public bool RotationSnap
    {
        get => AppSettings.GetBool("tools", "rotation_snap", true);
        set
        {
            AppSettings.Set("tools", "rotation_snap", value);
            Changed?.Invoke();
        }
    }

    /// <summary>Neighbour snap: moving, resizing and turning objects sticks their faces to nearby neighbour faces
    /// (<see cref="NeighbourDrag"/>). Per user, like grid snap; off until turned on.</summary>
    public bool NeighbourSnap
    {
        get => AppSettings.GetBool("tools", "neighbour_snap", false);
        set
        {
            AppSettings.Set("tools", "neighbour_snap", value);
            if (value)
            {
                NeighbourDrag.PrepareBaked(_ctx);
            }

            Changed?.Invoke();
        }
    }

    /// <summary>The distance (screen pixels) within which a face sticks to a neighbour's.</summary>
    public double NeighbourSnapPx
    {
        get => Math.Clamp(AppSettings.GetDouble("tools", "neighbour_snap_px", 8), MinNeighbourPx, MaxNeighbourPx);
        set
        {
            AppSettings.Set("tools", "neighbour_snap_px", Math.Clamp(value, MinNeighbourPx, MaxNeighbourPx));
            Changed?.Invoke();
        }
    }

    /// <summary>The menu (and toolbar drop-down) holding the neighbour snap distance and what it applies to; the toggle
    /// itself is View > Grid > Neighbour Snap.</summary>
    public const string NeighbourMenu = "View/Neighbour Snap Options";

    public const double MinNeighbourPx = 2;
    public const double MaxNeighbourPx = 40;

    /// <summary>The snap distances the drop-down offers.</summary>
    public static readonly int[] NeighbourDistances = [4, 8, 12, 16, 24];

    /// <summary>Which drags stick to neighbours, and what counts as one (each a per-user setting).</summary>
    public bool NeighbourSnapMove { get => Setting("neighbour_snap_move", true); set => Set("neighbour_snap_move", value); }

    public bool NeighbourSnapResize { get => Setting("neighbour_snap_resize", true); set => Set("neighbour_snap_resize", value); }

    public bool NeighbourSnapRotate { get => Setting("neighbour_snap_rotate", true); set => Set("neighbour_snap_rotate", value); }

    public bool NeighbourSnapMeshes { get => Setting("neighbour_snap_meshes", true); set => Set("neighbour_snap_meshes", value); }

    public bool NeighbourSnapActors { get => Setting("neighbour_snap_actors", false); set => Set("neighbour_snap_actors", value); }

    /// <summary>Volumes and other brush-shaped actors count as neighbours (by their own faces). Off by default.</summary>
    public bool NeighbourSnapVolumes { get => Setting("neighbour_snap_volumes", false); set => Set("neighbour_snap_volumes", value); }

    /// <summary>Faces on the same plane beside the moving face stick too ("in line": tops level with a nearby box), not
    /// only faces it touches. Off by default: on a busy map they are everywhere.</summary>
    public bool NeighbourSnapInLine { get => Setting("neighbour_snap_inline", false); set => Set("neighbour_snap_inline", value); }

    public bool NeighbourSnapBaked
    {
        get => Setting("neighbour_snap_baked", true);
        set
        {
            Set("neighbour_snap_baked", value);
            if (value && NeighbourSnap)
            {
                NeighbourDrag.PrepareBaked(_ctx);
            }
        }
    }

    /// <summary>True when a drag of this kind sticks to neighbours: the toggle, inverted while Alt is held, and the
    /// kind's own setting.</summary>
    public bool NeighbourSnapFor(SnapUse use, bool altHeld) =>
        (NeighbourSnap ^ altHeld) && use switch
        {
            SnapUse.Move => NeighbourSnapMove,
            SnapUse.Resize => NeighbourSnapResize,
            _ => NeighbourSnapRotate,
        };

    /// <summary>The snap grid in units (kept in the map's workspace, like the stock editor's per-map grid).</summary>
    public double GridSize
    {
        get => _ctx.Project.Workspace.GridSize > 0 ? _ctx.Project.Workspace.GridSize : 16;
        set
        {
            double size = Math.Clamp(value, 1, 4096);
            if (size != _ctx.Project.Workspace.GridSize)
            {
                _ctx.Execute(WorkspaceEdits.Grid(size));
            }

            Changed?.Invoke();
        }
    }

    /// <summary>The rotation snap in rotator units (1024 = 5.625 degrees, 8192 = 45, 16384 = 90).</summary>
    public int RotationGrid
    {
        get => _ctx.Project.Workspace.RotationGrid > 0 ? _ctx.Project.Workspace.RotationGrid : 1024;
        set
        {
            int units = Math.Clamp(value, 1, 32768);
            if (units != _ctx.Project.Workspace.RotationGrid)
            {
                _ctx.Execute(WorkspaceEdits.RotationGrid(units));
            }

            Changed?.Invoke();
        }
    }

    /// <summary>The class the place tool puts down (<c>Package.Class</c> or a short name the catalog knows).</summary>
    public string PlaceClass
    {
        get => AppSettings.GetString("tools", "place_class", "Engine.PlayerStart");
        set
        {
            AppSettings.Set("tools", "place_class", value);
            Changed?.Invoke();
        }
    }

    /// <summary>The GE kind the GE tool draws (LG, ZL, PIPE, LADDER, HOH, POLE, FENCE).</summary>
    public string GeKind
    {
        get => AppSettings.GetString("tools", "ge_kind", GeKinds.LedgeGrab);
        set
        {
            AppSettings.Set("tools", "ge_kind", value);
            Changed?.Invoke();
        }
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        _tools.Add(new SelectTool(this));
        _tools.Add(new TransformTool(this, TransformMode.Move));
        _tools.Add(new TransformTool(this, TransformMode.Rotate));
        _tools.Add(new TransformTool(this, TransformMode.Scale));
        _tools.Add(new PlaceActorTool(this));
        _tools.Add(new BrushTool(this));
        _tools.Add(new FaceTool(this));
        _tools.Add(new VertexTool(this));
        _tools.Add(new ClipTool(this));
        _tools.Add(new MeasureTool(this));
        Active = _tools[1];
        RegisterActions();
        Builder.Body = () => OpenScct.Editor.Core.Validation.DesignNumbers.Of(_ctx.Project);
        Builder.Changed += () => Changed?.Invoke();
        Builder.Reshaped += () =>
        {
            // Deferred: the change may come from a field of the options bar that is about to be replaced.
            if (Active is BrushTool)
            {
                EditorLifetime.Defer(RebuildOptions);
            }
        };
        context.ProjectChanged += _ =>
        {
            if (!_dragStep)
            {
                DragInterrupted?.Invoke();
            }
        };
        context.SessionReplaced += () =>
        {
            // Pending drags, clip and measure points belong to the old map.
            DragInterrupted?.Invoke();
            Active.Cancel();
            if (NeighbourSnap)
            {
                NeighbourDrag.PrepareBaked(_ctx);
            }

            Changed?.Invoke();
        };
    }

    /// <summary>Runs one edit of a tool's drag (edits made here do not raise <see cref="DragInterrupted"/>).</summary>
    public void DragStep(Action edit)
    {
        _dragStep = true;
        try
        {
            edit();
        }
        finally
        {
            _dragStep = false;
        }
    }

    /// <summary>Adds a tool from a feature's shell part (attached after this one); a tool with the id of one already
    /// here replaces it.</summary>
    public void AddTool(IViewportTool tool)
    {
        int at = _tools.FindIndex(t => t.Id == tool.Id);
        if (at < 0)
        {
            _tools.Add(tool);
            at = _tools.Count - 1;
        }
        else
        {
            Active = ReferenceEquals(Active, _tools[at]) ? tool : Active;
            _tools[at] = tool;
        }

        RegisterTool(tool, 100 + at);
    }

    /// <summary>Where tools put their toolbar options (set by the toolbar).</summary>
    public void SetOptionsBar(HBoxContainer bar)
    {
        _options = bar;
        RebuildOptions();
    }

    public void Activate(string id)
    {
        IViewportTool? tool = _tools.Find(t => t.Id == id);
        if (tool is null || ReferenceEquals(tool, Active))
        {
            return;
        }

        Active.Cancel();
        Active.Deactivate();
        Active = tool;
        tool.Activate();
        RebuildOptions();
        _ctx.SetStatus(tool.Title);
        Changed?.Invoke();
    }

    /// <summary>Tells views and the toolbar that tool state changed (tools call this after edits to their state).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Snaps a point to the grid on the given axes (all when null), when grid snap is on.</summary>
    public Vec3 Snap(Vec3 p, IReadOnlyList<int>? axes = null)
    {
        if (!GridSnap)
        {
            return p;
        }

        double g = GridSize;
        foreach (int axis in axes ?? [0, 1, 2])
        {
            p = p.With(axis, Math.Round(p[axis] / g) * g);
        }

        return p;
    }

    public double SnapValue(double v) => GridSnap ? Math.Round(v / GridSize) * GridSize : v;

    public int SnapRotation(double units) =>
        (int)Math.Round(RotationSnap ? Math.Round(units / RotationGrid) * RotationGrid : units);

    private void RebuildOptions()
    {
        if (_options is null)
        {
            return;
        }

        foreach (Node c in _options.GetChildren())
        {
            _options.RemoveChild(c);
            c.QueueFree();
        }

        Active.BuildOptions(_options);
    }

    private void RegisterActions()
    {
        for (int i = 0; i < _tools.Count; i++)
        {
            RegisterTool(_tools[i], 100 + i);
        }

        _ctx.AddAction(new EditorAction("view.grid.snap", "Grid Snap", "View/Grid", () => GridSnap = !GridSnap)
        {
            Shortcut = "G", Order = 100, IsChecked = () => GridSnap,
        });
        _ctx.AddAction(new EditorAction("view.grid.smaller", "Smaller Grid", "View/Grid", () => StepGrid(-1)) { Shortcut = "[", Order = 110 });
        _ctx.AddAction(new EditorAction("view.grid.larger", "Larger Grid", "View/Grid", () => StepGrid(1)) { Shortcut = "]", Order = 120 });
        _ctx.AddAction(new EditorAction("view.rotsnap", "Rotation Snap", "View/Grid", () => RotationSnap = !RotationSnap)
        {
            Order = 200, IsChecked = () => RotationSnap,
        });
        _ctx.AddAction(new EditorAction("view.neighbourSnap", "Neighbour Snap", "View/Grid", ToggleNeighbourSnap)
        {
            Shortcut = "Shift+G", Order = 300, IsChecked = () => NeighbourSnap,
            Description = "Faces stick to nearby faces while moving, resizing and rotating",
        });
        RegisterNeighbourActions();
        foreach (double size in GridSizes)
        {
            double s = size;
            string label = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction("view.grid.size." + label, label, "View/Grid Size", () => GridSize = s)
            {
                Order = 100 + (int)Math.Log2(s), RadioGroup = "view.grid.size", IsChecked = () => Math.Abs(GridSize - s) < 1e-9,
            });
        }

        foreach (int units in RotationGrids)
        {
            int u = units;
            string degrees = Rot.ToDegrees(u).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction("view.rotgrid." + u.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{degrees}° ({u})", "View/Rotation Snap Size", () => RotationGrid = u)
            {
                Order = 100 + (int)Math.Log2(u), RadioGroup = "view.rotgrid", IsChecked = () => RotationGrid == u,
            });
        }
    }

    private void ToggleNeighbourSnap()
    {
        NeighbourSnap = !NeighbourSnap;
        _ctx.SetStatus(NeighbourSnap
            ? "Neighbour snap on · press Alt during a drag to skip it"
            : "Neighbour snap off · press Alt during a drag to use it");
    }

    /// <summary>The neighbour snap drop-down (View/Neighbour Snap Options): the distance, then what it applies to.</summary>
    private void RegisterNeighbourActions()
    {
        const string menu = NeighbourMenu;
        foreach (int px in NeighbourDistances)
        {
            int d = px;
            string label = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction("view.nsnap.dist." + label, label + " px", menu, () => NeighbourSnapPx = d)
            {
                Order = 100 + d, RadioGroup = "view.nsnap.dist", IsChecked = () => Math.Abs(NeighbourSnapPx - d) < 1e-9,
                Description = "How close, on screen, a face must come to a neighbour's face to stick to it",
            });
        }

        void Check(string id, string title, int order, string help, Func<bool> get, Action<bool> set) =>
            _ctx.AddAction(new EditorAction("view.nsnap." + id, title, menu, () => set(!get()))
            {
                Order = order, IsChecked = get, Description = help,
            });

        Check("move", "Move", 200, "Moves stick to neighbours (when Neighbour Snap is on)", () => NeighbourSnapMove, v => NeighbourSnapMove = v);
        Check("resize", "Resize", 210, "Scaling, extruding faces and sizing the builder brush stick to neighbours", () => NeighbourSnapResize,
            v => NeighbourSnapResize = v);
        Check("rotate", "Rotate", 220, "Turns stop where a face lines up with a nearby face", () => NeighbourSnapRotate, v => NeighbourSnapRotate = v);
        Check("inline", "In Line", 290, "Faces on the same plane beside the moving face stick too, not only faces it touches", () => NeighbourSnapInLine,
            v => NeighbourSnapInLine = v);
        Check("baked", "Baked world", 300, "An imported map's baked collision counts as a neighbour (touching only)", () => NeighbourSnapBaked,
            v => NeighbourSnapBaked = v);
    }

    private bool Setting(string key, bool fallback) => AppSettings.GetBool("tools", key, fallback);

    private void Set(string key, bool value)
    {
        AppSettings.Set("tools", key, value);
        Changed?.Invoke();
    }

    private void RegisterTool(IViewportTool t, int order) =>
        _ctx.AddAction(new EditorAction("tools." + t.Id, t.Title, "Tools", () => Activate(t.Id))
        {
            Shortcut = t.Shortcut,
            Description = t.Description,
            Order = order,
            RadioGroup = "tools",
            IsChecked = () => ReferenceEquals(Active, t),
        });

    /// <summary>Moves to the next smaller or larger power-of-two grid.</summary>
    private void StepGrid(int direction)
    {
        double g = GridSize;
        double next = direction > 0
            ? GridSizes.FirstOrDefault(s => s > g + 1e-9, GridSizes[^1])
            : GridSizes.LastOrDefault(s => s < g - 1e-9, GridSizes[0]);
        GridSize = next;
        _ctx.SetStatus($"Grid {next}");
    }
}
