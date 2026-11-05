using Godot;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Panels;

/// <summary>
/// Surface properties of the faces picked with the surface tool (U): picking more (same material, coplanar, adjacent,
/// the whole brush), material (typed path, one the map already uses, or the Assets panel's), texture pan, rotation and
/// scale as typed numbers with nudge buttons, alignment, flips, copy/paste surface, poly flags and lightmap scale. The
/// boxes show the last picked face; edits apply to every picked face as one undo step, and dragging a number box is
/// one step too. Faces the kept baked world holds, and baked-world surfaces picked on a map with no brushes there, are
/// shown read-only with a note saying why.
/// </summary>
[Panel("Surface", DockSide.Right, Order = 2)]
public partial class SurfacePanel : EditorPanel
{
    private static readonly int[] PanSteps = [1, 4, 16, 64];

    private readonly Dictionary<int, CheckBox> _flags = new();
    private Label _title = null!;
    private Label _info = null!;
    private LineEdit _material = null!;
    private PopupMenu _materials = null!;
    private SpinBox _panU = null!, _panV = null!, _rotation = null!, _scaleU = null!, _scaleV = null!, _lightmap = null!;
    private OptionButton _panStep = null!;
    private Control _body = null!;
    private Control _edit = null!;
    private Control _select = null!;
    private Control _empty = null!;
    private PanelContainer _readOnly = null!;
    private Label _readOnlyText = null!;
    private Button _fromAssets = null!;
    private Button _buildRender = null!;
    private Button? _emptyTool;
    private bool _toolsHooked;
    private DeferredAction _update = null!;
    private bool _setting;
    private int _gesture;

    private static GeometrySelection Geo => GeometrySelection.Instance;

    protected override void Build()
    {
        _update = new DeferredAction(ShowFace);
        _empty = PanelUi.EmptyState(this, "surface", "No surfaces picked",
            "Click a surface with the surface tool (U) to edit its material, texture and flags here. Shift adds, Ctrl toggles, "
            + "Alt+click applies the Assets panel's material, double\u2011click takes the whole brush.",
            "Surface tool", () => Context.Run("tools.face"));
        _emptyTool = _empty.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault();
        _title = PanelUi.Title(this, "");
        VBoxContainer box = PanelUi.Scroll(this);
        _body = PanelUi.ScrollOf(box);

        (_readOnly, _readOnlyText) = WorkbenchUi.Callout(box, Severity.Info);
        _buildRender = PanelUi.Button(box, "Build render from brushes…",
            "Draw and build the map's world from its brushes instead of the imported render, so surface edits show (asks first)",
            () => SurfaceCommands.BuildRenderFromBrushes(this));
        var select = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        select.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        box.AddChild(select);
        _select = select;
        BuildSelect(select);

        var edit = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        box.AddChild(edit);
        _edit = edit;
        BuildMaterial(edit);
        BuildMapping(edit);
        BuildAlignment(edit);
        BuildFlags(edit);

        PanelUi.Header(edit, "Lighting");
        HBoxContainer lm = PanelUi.Row(edit, "Lightmap scale", "Lightmap texel size (UE2 default 32)");
        _lightmap = Spin(lm, 1, 1024, 1, "Lightmap texel size (UE2 default 32)", v => SurfaceCommands.Run("Lightmap scale", FaceEdit.SetLightMapScale(v), Key("lightmap")));
        _info = PanelUi.Caption(box, "");

        Geo.Changed += () =>
        {
            _gesture++;
            _update.Queue();
        };
        Context.ProjectChanged += _ => _update.Queue();
        AssetBrowserPanel.CurrentMaterialChanged += ShowCurrentMaterial;
        TreeExiting += () => AssetBrowserPanel.CurrentMaterialChanged -= ShowCurrentMaterial;
        HookTools();

        ShowCurrentMaterial();
    }

    /// <summary>More faces from the picked ones: the stock Select Surfaces commands most used.</summary>
    private static void BuildSelect(VBoxContainer box)
    {
        PanelUi.Header(box, "Select");
        GridContainer grid = ButtonGrid(box);
        GridButton(grid, "Same material", "Every face of the map with this material", () => SurfaceCommands.SelectMatching("material"));
        GridButton(grid, "Coplanar", "Every face of the map in this plane", () => SurfaceCommands.SelectMatching("coplanar"));
        GridButton(grid, "Adjacent", "Every face connected to the picked ones (Surface > Select for walls, floors, coplanars)",
            () => SurfaceCommands.SelectAdjacent("all"));
        GridButton(grid, "Whole brush", "Every face of the picked faces' brushes", SurfaceCommands.SelectAllFaces);
        GridButton(grid, "Brushes", "Select the brushes of the picked faces", SurfaceCommands.SelectBrushes);
    }

    public override void Refresh() => _update.Queue();

    private void BuildMaterial(VBoxContainer box)
    {
        PanelUi.Header(box, "Material");
        // The path on a row of its own, so the material's own name (its last part) is never cut off by the buttons.
        _material = new LineEdit { PlaceholderText = "Package.Group.Name", SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        _material.TextSubmitted += ApplyMaterial;
        box.AddChild(_material);
        _material.TooltipText = "The material of the picked faces (Package.Group.Name); Enter applies it";
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        box.AddChild(row);
        PanelUi.Button(row, "Apply", "Set the typed material on the picked faces", () => ApplyMaterial(_material.Text)).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _fromAssets = PanelUi.Button(row, "From Assets", "", () => SurfaceCommands.ApplyMaterial(AssetBrowserPanel.CurrentMaterial));
        _fromAssets.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _materials = new PopupMenu();
        _materials.IndexPressed += i => ApplyMaterial(_materials.GetItemMetadata((int)i).AsString());
        AddChild(_materials);
        Button used = PanelUi.Button(row, "Used", "Materials this map already uses", () =>
        {
            _materials.Clear();
            foreach ((string? material, int faces) in FaceEdit.MaterialsUsed(Context.Project).Take(60))
            {
                _materials.AddItem($"{material ?? "(default)"}   ×{faces}");
                _materials.SetItemMetadata(_materials.ItemCount - 1, material ?? "");
            }

            PanelUi.PopupAtMouse(this, _materials);
        });
        used.Icon = Icons.Get("chevron-down", EditorTheme.TextDim);
        used.IconAlignment = HorizontalAlignment.Right;
        used.SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private void BuildMapping(VBoxContainer box)
    {
        PanelUi.Header(box, "Texture");
        HBoxContainer pan = PanelUi.Row(box, "Pan U / V", "Texture offset in texels");
        _panU = Spin(pan, -65536, 65536, 1, "Pan U (texels)", v => SurfaceCommands.Run("Pan texture", FaceEdit.SetPan((int)v, null), Key("panU")));
        _panV = Spin(pan, -65536, 65536, 1, "Pan V (texels)", v => SurfaceCommands.Run("Pan texture", FaceEdit.SetPan(null, (int)v), Key("panV")));

        HBoxContainer nudge = PanelUi.Row(box, "Nudge", "Pan by the chosen step");
        _panStep = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Pan step (texels)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (int s in PanSteps)
        {
            _panStep.AddItem(s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _panStep.Selected = 2;
        nudge.AddChild(_panStep);
        // The four arrows sit together as one group, which also keeps the row inside a narrow dock.
        var arrows = new HBoxContainer();
        arrows.AddThemeConstantOverride("separation", 0);
        nudge.AddChild(arrows);
        PanelUi.IconButton(arrows, "arrow-left", "Pan U down", () => Nudge(-1, 0));
        PanelUi.IconButton(arrows, "arrow-right", "Pan U up", () => Nudge(1, 0));
        PanelUi.IconButton(arrows, "arrow-up", "Pan V down", () => Nudge(0, -1));
        PanelUi.IconButton(arrows, "arrow-down", "Pan V up", () => Nudge(0, 1));

        HBoxContainer rot = PanelUi.Row(box, "Rotation", "Degrees from wall/floor alignment");
        _rotation = Spin(rot, -180, 180, 0.5, "Degrees from wall/floor alignment", v => SurfaceCommands.Run("Rotate texture", FaceEdit.SetRotation(v), Key("rotation")));
        _rotation.Suffix = "°";
        GridContainer turn = ButtonGrid(PanelUi.Row(box, "Turn", "Rotate by steps"));
        foreach (double d in new[] { -90.0, -45, -15, 15, 45, 90 })
        {
            double deg = d;
            string text = (deg > 0 ? "+" : "\u2212") + Math.Abs(deg).ToString(System.Globalization.CultureInfo.InvariantCulture) + "°";
            GridButton(turn, text, $"Rotate {text}", () => SurfaceCommands.Run($"Rotate texture {deg}°", (f, t) => TextureOps.Rotate(f, t, deg)));
        }

        HBoxContainer scale = PanelUi.Row(box, "Scale U / V", "World units per texel (2 = texture twice as big)");
        // A fine step keeps typed and shown scales exact (a SpinBox snaps to min + k * step); the arrows step 1/16.
        _scaleU = Spin(scale, 0.01, 64, 0.0001, "Scale U (units per texel)", v => SurfaceCommands.Run("Scale texture", FaceEdit.SetScale(v, null), Key("scaleU")), arrowStep: 0.0625);
        _scaleV = Spin(scale, 0.01, 64, 0.0001, "Scale V (units per texel)", v => SurfaceCommands.Run("Scale texture", FaceEdit.SetScale(null, v), Key("scaleV")), arrowStep: 0.0625);
        // Two-column rows under the U / V fields, so their buttons line up with the fields above.
        GridContainer steps = ButtonGrid(PanelUi.Row(box, "Scale by", "Scale the texture by a factor"), columns: 2);
        GridButton(steps, "×½", "Texture half as big", () => SurfaceCommands.Run("Scale texture x0.5", (f, _) => TextureOps.Scale(f, 0.5, 0.5)));
        GridButton(steps, "×2", "Texture twice as big", () => SurfaceCommands.Run("Scale texture x2", (f, _) => TextureOps.Scale(f, 2, 2)));
        GridContainer flip = ButtonGrid(PanelUi.Row(box, "Flip", "Mirror the texture"), columns: 2);
        GridButton(flip, "U", "Mirror the texture along U", () => SurfaceCommands.Run("Flip U", (f, _) => TextureOps.FlipU(f)));
        GridButton(flip, "V", "Mirror the texture along V", () => SurfaceCommands.Run("Flip V", (f, _) => TextureOps.FlipV(f)));
    }

    /// <summary>A grid of equal buttons filling a row's value side, up to <paramref name="columns"/> to a row.</summary>
    private static GridContainer ButtonGrid(Control row, int columns = 3)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXs);
        grid.AddThemeConstantOverride("v_separation", EditorTheme.SpaceXs);
        row.AddChild(grid);
        FitColumns(grid, columns);
        return grid;
    }

    /// <summary>Keeps <paramref name="grid"/> at up to <paramref name="max"/> columns (at least two), one fewer while its
    /// widest button would be cut at the grid's width.</summary>
    private static void FitColumns(GridContainer grid, int max)
    {
        grid.Resized += () =>
        {
            float widest = grid.GetChildren().OfType<Button>().Select(NaturalWidth).DefaultIfEmpty(0).Max();
            float gap = grid.GetThemeConstant("h_separation");
            int columns = Math.Clamp((int)((grid.Size.X + gap) / (widest + gap)), Math.Min(2, max), max);
            if (grid.Columns != columns)
            {
                grid.Columns = columns;
            }
        };
    }

    /// <summary>The width a button needs to show its whole text (and a check box its box).</summary>
    private static float NaturalWidth(Button b)
    {
        float text = b.GetThemeFont("font").GetStringSize(b.Text, HorizontalAlignment.Left, -1, b.GetThemeFontSize("font_size")).X;
        float box = b is CheckBox ? b.GetThemeIcon("unchecked").GetWidth() + b.GetThemeConstant("h_separation") : 0;
        return text + box + b.GetThemeStylebox("normal").GetMinimumSize().X;
    }

    private static Button GridButton(GridContainer grid, string text, string tooltip, Action pressed)
    {
        Button b = PanelUi.Button(grid, text, tooltip, pressed);
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.ClipText = true;
        b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return b;
    }

    private void BuildAlignment(VBoxContainer box)
    {
        PanelUi.Header(box, "Align");
        GridContainer grid = ButtonGrid(box);
        foreach ((string label, string action, string tip) in new[]
                 {
                     ("Default", "surface.alignDefault", "Box mapping along the face's main axis (stock default)"),
                     ("Floor", "surface.alignFloor", "Straight down the Z axis"),
                     ("Wall", "surface.alignWall", "Level along the wall, upright"),
                     ("Planar", "surface.alignFace", "Along the face's first edge"),
                     ("Fit", "surface.alignFit", "Stretch the texture over the face once"),
                     ("Cylinder", "surface.alignCylinder", "Wrap round the brush like a label"),
                 })
        {
            string id = action;
            GridButton(grid, label, tip, () => Context.Run(id));
        }

        // One row, each button as wide as its text needs at least.
        PanelUi.Header(box, "Copy and paste");
        var clip = new HBoxContainer();
        clip.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        box.AddChild(clip);
        foreach ((string text, string tip, Action pressed) in new (string, string, Action)[]
                 {
                     ("Copy", "Copy the last picked face's surface (Ctrl+Shift+C)", SurfaceCommands.Copy),
                     ("Paste", "Paste material, flags and lightmap scale (Ctrl+Shift+V)", () => SurfaceCommands.Paste(false)),
                     ("Paste aligned", "Paste with the copied alignment too (Ctrl+Alt+Shift+V)", () => SurfaceCommands.Paste(true)),
                 })
        {
            Button b = PanelUi.Button(clip, text, tip, pressed);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.Name = text.Replace(" ", "", StringComparison.Ordinal);
        }
    }

    private void BuildFlags(VBoxContainer box)
    {
        // The Inspector's flag names in its order, three to a row (two in a narrow dock).
        PanelUi.Header(box, "Flags");
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXs);
        box.AddChild(grid);
        FitColumns(grid, 3);
        foreach ((string name, int bit) in PolyFlagNames.All)
        {
            int b = bit;
            string help = SurfaceFlags.All.FirstOrDefault(s => s.Bit == bit).Help ?? name;
            var check = new CheckBox
            {
                Text = name, TooltipText = $"{name}: {help} (PolyFlags 0x{bit:X8})", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            check.Toggled += on =>
            {
                if (!_setting)
                {
                    SurfaceCommands.Run($"{(on ? "Set" : "Clear")} {name}", on ? FaceEdit.SetFlags(b, 0) : FaceEdit.SetFlags(0, b));
                }
            };
            _flags[b] = check;
            grid.AddChild(check);
        }
    }

    /// <summary>Follows tool changes (the empty state's button opens the surface tool, so it hides while that tool is on).
    /// The tool manager can come after the docks: then the first update hooks it.</summary>
    private void HookTools()
    {
        if (_toolsHooked || ToolManager.Instance is not { } tools)
        {
            return;
        }

        _toolsHooked = true;
        tools.Changed += _update.Queue;
        TreeExiting += () => tools.Changed -= _update.Queue;
    }

    /// <summary>The Assets panel's material on the From Assets button's tooltip (disabled while there is none).</summary>
    private void ShowCurrentMaterial()
    {
        string? m = AssetBrowserPanel.CurrentMaterial;
        _fromAssets.Disabled = m is null || _edit.GetMeta("locked", false).AsBool();
        _fromAssets.TooltipText = m is null
            ? "Pick a material or texture in the Assets panel to apply it here (or Alt+click a face with it)"
            : $"Set the Assets panel's material on the picked faces: {m} (Alt+click a face does too)";
    }

    /// <summary>Fills the boxes from the last picked face.</summary>
    private void ShowFace()
    {
        HookTools();
        MapProject p = Context.Project;
        FaceRef? primary = Geo.PrimaryFace;
        BrushFace? face = primary is { } f0 ? FaceEdit.Find(p, f0) : null;
        int n = Geo.Faces.Count;
        if (face is null && Geo.BakedSurface is { } baked)
        {
            ShowBaked(baked);
            return;
        }

        _title.Text = n == 0 ? "" : n == 1 ? $"Face {Geo.Faces[0]}" : $"{PanelUi.Count(n, "face")} · showing {primary}";
        _title.TooltipText = _title.Text;
        bool shown = face is not null;
        _empty.Visible = !shown;
        if (_emptyTool is not null)
        {
            // The button opens the tool; with the tool already on, the hint says what to do.
            _emptyTool.Visible = ToolManager.Instance?.Active is not FaceTool;
        }

        _title.Visible = shown;
        _body.Visible = shown;
        if (face is null || primary is not { } f || p.FindBrush(f.Brush) is not { } brush)
        {
            _info.Text = "";
            return;
        }

        int locked = Geo.Faces.Count(SurfaceCommands.IsLocked);
        _readOnly.Visible = locked > 0;
        _buildRender.Visible = locked > 0;
        _readOnlyText.Text = locked == 0 ? ""
            : locked == n ? $"Read-only: {(n == 1 ? "this face is" : "these faces are")} {BakedContent.LockedSurfaceNote}."
            : $"{locked} of the {n} picked faces are {BakedContent.LockedSurfaceNote}. Edits change only the other {n - locked}.";
        _edit.Visible = true;
        _select.Visible = true;
        SetEditable(_edit, locked < n);

        SurfaceInfo info = SurfaceInfo.Of(face, ShapeTransform.For(brush));
        _setting = true;
        if (!_material.HasFocus())
        {
            _material.Text = face.Material ?? "";
        }

        SetSpin(_panU, info.PanU);
        SetSpin(_panV, info.PanV);
        SetSpin(_rotation, info.Rotation);
        SetSpin(_scaleU, info.ScaleU);
        SetSpin(_scaleV, info.ScaleV);
        SetSpin(_lightmap, face.LightMapScale);
        foreach ((int bit, CheckBox check) in _flags)
        {
            check.ButtonPressed = (face.Flags & bit) != 0;
        }

        _setting = false;
        Vec3 nrm = info.Normal;
        _info.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Normal {nrm.X:0.##}, {nrm.Y:0.##}, {nrm.Z:0.##} · area {info.Area / 10000:0.##} m² · {brush.Id} ({brush.Oper.ToString().ToLowerInvariant()})");
    }

    /// <summary>A picked baked-world surface: what it is, read-only.</summary>
    private void ShowBaked(BakedSurfacePick baked)
    {
        _title.Text = "Baked surface";
        _title.TooltipText = _title.Text;
        _empty.Visible = false;
        _title.Visible = true;
        _body.Visible = true;
        _edit.Visible = false;
        _select.Visible = false;
        _readOnly.Visible = true;
        _buildRender.Visible = false;
        _readOnlyText.Text = "Read-only: this surface is part of the world imported with the map, not of a brush. "
            + "Brush faces are the surfaces you can edit here.";
        Vec3 n = baked.Normal;
        _info.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Material {baked.Material ?? "(none)"} · normal {n.X:0.##}, {n.Y:0.##}, {n.Z:0.##} · {baked.Triangles.Count / 3} triangles");
    }

    /// <summary>Turns the edit controls on or off (read-only faces), keeping their values readable.</summary>
    private void SetEditable(Control root, bool editable)
    {
        root.SetMeta("locked", !editable);
        foreach (Node node in root.FindChildren("*", owned: false))
        {
            switch (node)
            {
                case SpinBox s:
                    s.Editable = editable;
                    break;
                case LineEdit l:
                    l.Editable = editable;
                    break;
                case BaseButton b:
                    b.Disabled = !editable;
                    break;
            }
        }

        // Copy reads the face, so it stays on.
        foreach (Node node in root.FindChildren("Copy", owned: false))
        {
            if (node is BaseButton copy)
            {
                copy.Disabled = false;
            }
        }

        ShowCurrentMaterial();
    }

    private void ApplyMaterial(string text)
    {
        SurfaceCommands.Run("Set material", FaceEdit.SetMaterial(text));
        _material.ReleaseFocus();
    }

    private void Nudge(int du, int dv)
    {
        int step = PanSteps[Math.Clamp(_panStep.Selected, 0, PanSteps.Length - 1)];
        SurfaceCommands.Run("Pan texture", (f, _) => TextureOps.Pan(f, du * step, dv * step));
    }

    /// <summary>The merge key of a field: edits of one field in one gesture (a drag, or typing) fold into one step.</summary>
    private string Key(string field) => $"surface:{field}:{_gesture}";

    private SpinBox Spin(HBoxContainer row, double min, double max, double step, string tooltip, Action<double> changed, double arrowStep = 0)
    {
        SpinBox s = PanelUi.Spin(row, min, max, step, min > 0 ? min : 0, tooltip, EditorTheme.SpinCompactWidth);
        s.CustomArrowStep = arrowStep;
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        s.ValueChanged += v =>
        {
            if (!_setting)
            {
                changed(v);
            }
        };
        // A new press or leaving the box starts a new undo step.
        s.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: false })
            {
                EndGesture();
            }
        };
        s.GetLineEdit().FocusExited += EndGesture;
        return s;
    }

    private void EndGesture()
    {
        _gesture++;
        Context.Session.Commands.EndMerge();
    }

    private static void SetSpin(SpinBox s, double v)
    {
        if (!s.GetLineEdit().HasFocus())
        {
            s.SetValueNoSignal(v);
        }
    }
}
