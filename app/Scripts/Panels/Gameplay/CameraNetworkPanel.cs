using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// The camera network workbench: each network of SCamNetwork cameras in ring order with its link problems, the camera
/// the CamNet gadget opens on, ordering and linking, the selected camera's HUD name and turn limits, and a monitor that
/// shows what a mercenary sees through it (110 degree view, turned within its limits), lit by the map's lights like the
/// Lit Preview view, or unlit.
/// </summary>
[Panel("Camera Network", DockSide.Workbench, Order = 8)]
public partial class CameraNetworkPanel : EditorPanel
{
    private Tree _tree = null!;
    private Control _empty = null!;
    private Control _scroll = null!;
    private VBoxContainer _camera = null!;
    private LineEdit _name = null!;
    private SpinBox _pitchBelow = null!, _pitchAbove = null!, _yawBelow = null!, _yawAbove = null!;
    private HSlider _lookPitch = null!, _lookYaw = null!;
    private CheckBox _lit = null!;
    private Camera3D _monitor = null!;
    private SubViewport _view = null!;
    private Label _monitorInfo = null!;
    private DeferredAction _update = null!;
    private bool _updating;
    private NetworkCamera? _current;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        WorkbenchUi.Action(bar, "Ring", "Link the selected cameras in selection order, the last back to the first", "gameplay.camnet.ring", Context);
        WorkbenchUi.Action(bar, "Line", "Link the selected cameras in selection order", "gameplay.camnet.line", Context);
        WorkbenchUi.Action(bar, "Take out", "Take the selected cameras out of their network, joining their neighbours", "gameplay.camnet.remove", Context);
        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        PanelUi.Columns(_tree, ("Camera", 120, true), ("Id", 80, true));
        _tree.ColumnTitlesVisible = false;
        _tree.SetColumnExpandRatio(0, 3);
        _tree.SetColumnExpandRatio(1, 2);
        _tree.ItemSelected += () =>
        {
            if (!_updating && _tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            {
                Context.Selection.Set([ObjectRef.Actor(id)]);
            }
        };
        _tree.ItemActivated += () => Context.Focus(Context.Selection.Items);
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "type-camera", "No camera networks",
            "Place SCamNetwork cameras, select them in the order the CamNet view steps through, then press Ring or Line.");

        VBoxContainer box = PanelUi.Scroll(this);
        _scroll = PanelUi.ScrollOf(box);
        _scroll.SizeFlagsStretchRatio = 2;
        _camera = new VBoxContainer();
        box.AddChild(_camera);
        PanelUi.Header(_camera, "Selected camera");
        var order = new HBoxContainer();
        _camera.AddChild(order);
        PanelUi.Segmented(order, [("Earlier", "Move the camera one place earlier in its network"), ("Later", "Move the camera one place later in its network")], -1,
            i => Reorder(i == 0 ? -1 : 1));
        WorkbenchUi.Spacer(order);
        PanelUi.Segmented(order, [("Previous", "Look through the previous camera (PrevCam)"), ("Next", "Look through the next camera (NextCam)")], -1,
            i => Step(c => i == 0 ? c.Prev : c.Next));
        HBoxContainer nameRow = PanelUi.Row(_camera, "HUD name", "CamName, shown to the mercenary in the CamNet view");
        _name = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextSubmitted += t => Edit(() => CameraNetworks.Rename(Context.Project, _current!.Id, t));
        nameRow.AddChild(_name);
        (_pitchBelow, _pitchAbove) = Limits("Pitch limit", "How far it tilts down (negative) and up from its placed pitch, in rotator units " +
            "(16384 = 90 degrees). The game limits it only when both ends are non-zero: a 0 at either end leaves it free.");
        (_yawBelow, _yawAbove) = Limits("Yaw limit", "How far it turns left (negative) and right from its placed yaw. The game limits it only " +
            "when both ends are non-zero.");

        PanelUi.Header(_camera, "Monitor");
        var container = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(0, 150), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _camera.AddChild(container);
        // The monitor shares the editor's 3D world, so it shows the map as edited, lit as the Lit Preview view lights it.
        _view = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible, HandleInputLocally = false, GuiDisableInput = true };
        container.AddChild(_view);
        _monitor = new Camera3D
        {
            Current = true,
            KeepAspect = Camera3D.KeepAspectEnum.Width,
            Fov = (float)CameraNetworks.FieldOfView,
            Near = 0.25f,
        };
        _view.AddChild(_monitor);
        _lit = new CheckBox { Text = "Lit by the map's lights", ButtonPressed = true, TooltipText = "Off: unlit, to see into the dark corners" };
        _lit.Toggled += Light;
        _camera.AddChild(_lit);
        Light(true);
        _lookYaw = LookSlider("Turn", "Turn the camera within its yaw limits, as the mercenary does");
        _lookPitch = LookSlider("Tilt", "Tilt the camera within its pitch limits");
        _monitorInfo = PanelUi.Caption(_camera, "");

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += _update.Queue;
    }

    public override void Refresh() => _update.Queue();

    private void Light(bool lit)
    {
        RenderMode mode = lit ? RenderMode.Lit : RenderMode.Unlit;
        _monitor.CullMask = VisualLayers.ForMode(mode);
        _monitor.Environment = EditorViewport.MakeEnvironment(mode);
    }

    private (SpinBox, SpinBox) Limits(string name, string tip)
    {
        HBoxContainer row = PanelUi.Row(_camera, name, tip);
        SpinBox Spin(string end)
        {
            SpinBox s = PanelUi.Spin(row, -32768, 32768, 256, 0, $"{name}, {end} (rotator units)");
            s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            s.ValueChanged += _ => SetLimits();
            return s;
        }

        return (Spin("below"), Spin("above"));
    }

    private HSlider LookSlider(string name, string tip)
    {
        HBoxContainer row = PanelUi.Row(_camera, name, tip);
        var s = new HSlider { MinValue = -1, MaxValue = 1, Step = 0.01, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        s.ValueChanged += _ => Aim();
        row.AddChild(s);
        return s;
    }

    private void Rebuild()
    {
        _updating = true;
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        string? entry = CameraNetworks.EntryCamera(Context.Project);
        List<CameraNetwork> networks = CameraNetworks.Networks(Context.Project);
        string? selected = WorkbenchPart.Selected(Context, CameraNetworks.IsNetworkCamera).FirstOrDefault()?.Id;
        _current = null;
        _empty.Visible = networks.Count == 0;

        for (int n = 0; n < networks.Count; n++)
        {
            CameraNetwork net = networks[n];
            TreeItem head = Row(root, $"Network {n + 1}", $"{PanelUi.Count(net.Cameras.Count, "camera")} · {(net.Loop ? "ring" : "line")}", "");
            head.SetCustomColor(1, EditorTheme.TextDim);
            foreach (string problem in net.Problems)
            {
                TreeItem warning = Row(head, problem, "", "");
                warning.SetIcon(0, PanelIcons.Severity(Severity.Warning));
            }

            for (int i = 0; i < net.Cameras.Count; i++)
            {
                NetworkCamera c = net.Cameras[i];
                bool isEntry = string.Equals(c.Id, entry, StringComparison.OrdinalIgnoreCase);
                // The second column names what is special about the camera in a word or two; the tooltip spells it out
                // and gives the actor id.
                string roles = string.Join(" · ", new[] { isEntry ? "Entry" : null, c.First ? "First" : null }.OfType<string>());
                string why = string.Join("\n", new[]
                {
                    isEntry ? "Entry: CamNet opens on this camera" : null,
                    c.First ? "First: the camera its network starts from" : null,
                }.OfType<string>());
                TreeItem item = Row(head, $"{i + 1}. {c.Name}", roles, c.Id);
                item.SetCustomColor(1, EditorTheme.TextDim);
                item.SetTooltipText(0, $"{c.Name} ({c.Id})");
                item.SetTooltipText(1, why.Length > 0 ? $"{c.Id}\n{why}" : c.Id);
                if (string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase))
                {
                    item.Select(0);
                    _current = c;
                }
            }
        }

        _camera.Visible = _scroll.Visible = _current is not null;
        if (_current is { } cam)
        {
            _name.Text = cam.Name;
            _pitchBelow.SetValueNoSignal(cam.PitchLimits.Min);
            _pitchAbove.SetValueNoSignal(cam.PitchLimits.Max);
            _yawBelow.SetValueNoSignal(cam.YawLimits.Min);
            _yawAbove.SetValueNoSignal(cam.YawLimits.Max);
            _lookPitch.SetValueNoSignal(0);
            _lookYaw.SetValueNoSignal(0);
            Aim();
        }

        _updating = false;
    }

    private TreeItem Row(TreeItem parent, string text, string detail, string id)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetText(1, detail);
        // The cells clip in a narrow dock: their tooltips have the whole text.
        item.SetTooltipText(0, detail.Length > 0 ? $"{text}: {detail}" : text);
        item.SetTooltipText(1, detail);
        item.SetMetadata(0, id);
        return item;
    }

    /// <summary>Points the monitor through the current camera, turned by the look sliders within its limits.</summary>
    private void Aim()
    {
        if (_current is not { } c)
        {
            return;
        }

        // A free camera turns half way round either side, the furthest a slider end can mean.
        static int Reach(double v, bool limited, (int Min, int Max) l) => (int)(v * (!limited ? 32768 : v < 0 ? Math.Abs(l.Min) : Math.Abs(l.Max)));
        int pitch = Reach(_lookPitch.Value, c.PitchLimited, c.PitchLimits);
        int yaw = Reach(_lookYaw.Value, c.YawLimited, c.YawLimits);
        Rot look = CameraNetworks.Look(c, pitch, yaw);
        (Vec3 f, Vec3 r, Vec3 u) = look.Axes();
        _monitor.GlobalTransform = new Transform3D(new Basis(Space.DirToGodot(r), Space.DirToGodot(u), -Space.DirToGodot(f)), Space.ToGodot(c.Eye));
        string free = (c.PitchLimited, c.YawLimited) switch
        {
            (true, true) => "",
            (false, false) => " It turns freely (no limit applies).",
            (false, _) => " It tilts freely (no pitch limit applies).",
            _ => " It turns freely left and right (no yaw limit applies).",
        };
        _monitorInfo.Text = $"{c.Name} ({c.Id}): what a mercenary sees, {CameraNetworks.FieldOfView:0}\u00b0 wide.{free}";
    }

    private void SetLimits()
    {
        if (_updating || _current is not { } c)
        {
            return;
        }

        (int, int) pitch = ((int)_pitchBelow.Value, (int)_pitchAbove.Value), yaw = ((int)_yawBelow.Value, (int)_yawAbove.Value);
        if (pitch != c.PitchLimits || yaw != c.YawLimits)
        {
            // Steps of the spin boxes in a row make one undo step.
            Edit(() => CameraNetworks.SetLimits(Context.Project, c.Id, pitch, yaw, mergeKey: $"camnet.limits.{c.Id}"));
        }
    }

    private void Reorder(int by)
    {
        if (_current is not { } c || CameraNetworks.Networks(Context.Project).FirstOrDefault(n => n.Cameras.Any(x => x.Id == c.Id)) is not { } net)
        {
            return;
        }

        List<string> ids = net.Cameras.Select(x => x.Id).ToList();
        int i = ids.IndexOf(c.Id), j = i + by;
        if (j < 0 || j >= ids.Count)
        {
            return;
        }

        (ids[i], ids[j]) = (ids[j], ids[i]);
        Edit(() => CameraNetworks.Link(Context.Project, ids, net.Loop));
    }

    private void Step(Func<NetworkCamera, string?> next)
    {
        if (_current is { } c && next(c) is { } id && Context.Project.FindActor(id) is not null)
        {
            Context.Selection.Set([ObjectRef.Actor(id)]);
        }
    }

    private void Edit(Func<OpenScct.Editor.Core.Commands.IEditCommand> edit)
    {
        try
        {
            Context.Execute(edit());
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }
}
