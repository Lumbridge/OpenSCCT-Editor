using System.Globalization;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// Reference images (RE+ Map Design's references): a floor plan, sketch or screenshot drawn behind the map in the Top,
/// Front or Side view. Each has an origin (the world point under its centre; in the Top view its height picks the
/// storey it belongs to), a scale in units per pixel (set the image's width in units, or calibrate it from two marks
/// measured on it) and an opacity. Images are told apart by name, so one file can be laid more than once (the same
/// plan on two floors). Saved with the map's workspace through the <c>reference</c> change op, one undo step per change.
/// </summary>
[Panel("References", DockSide.Workbench, Order = 11)]
public partial class ReferencesPanel : EditorPanel
{
    private static readonly string[] s_views = ["top", "front", "side"];
    private Tree _tree = null!;
    private Control _empty = null!;
    private VBoxContainer _edit = null!;
    private DeferredAction _rebuild = null!;
    private string? _selected;
    private Label? _measured;
    private SpinBox? _length;

    protected override void Build()
    {
        _rebuild = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Button(bar, "Add image…", "Add an image (PNG or JPG) to the active 2D view, centred on the view", Add).Icon =
            Icons.Get("import", EditorTheme.Text);
        WorkbenchUi.Guard(PanelUi.Button(bar, "Remove", "Remove the selected reference image (the file stays)", () =>
        {
            if (_selected is { } name)
            {
                Change($"Remove reference {name}", ("delete", ApiJson.Bool(true)));
            }
        }), () => Current is not null).Icon = Icons.Get("trash", EditorTheme.Text);

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
        };
        PanelUi.Columns(_tree, ("Name", 100, true), ("View", 56, false), ("Height", 64, false));
        _tree.ItemSelected += () =>
        {
            _selected = _tree.GetSelected()?.GetMetadata(0).AsString();
            ShowEditor();
        };
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "image", "No reference images",
            "Add a floor plan, sketch or screenshot to trace in a 2D view. In the Top view an image shows on the storey its height is in.",
            "Add image…", Add);
        _edit = PanelUi.Scroll(this);
        Context.ProjectChanged += _ => _rebuild.Queue();
        MeasureTool.Measured += ShowMeasured;
    }

    public override void _ExitTree() => MeasureTool.Measured -= ShowMeasured;

    public override void Refresh() => _rebuild.Queue();

    private ReferenceImage? Current =>
        Context.Project.Workspace.References.Find(r => string.Equals(r.Name, _selected, StringComparison.OrdinalIgnoreCase));

    private void Rebuild()
    {
        _tree.Clear();
        _empty.Visible = Context.Project.Workspace.References.Count == 0;
        _tree.ColumnTitlesVisible = !_empty.Visible;
        TreeItem root = _tree.CreateItem();
        foreach (ReferenceImage r in Context.Project.Workspace.References)
        {
            TreeItem item = _tree.CreateItem(root);
            string file = ReferenceLayer.Resolve(r.Path, Context.Session.Folder);
            item.SetText(0, r.Name);
            item.SetText(1, PanelUi.Words(r.View));
            item.SetText(2, PanelUi.Num(r.Origin.Z));
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetMetadata(0, r.Name);
            item.SetTooltipText(0, File.Exists(file) ? file : $"{file}\n(the file is missing)");
            if (!File.Exists(file))
            {
                item.SetIcon(0, PanelIcons.Severity(Severity.Error));
            }

            if (string.Equals(r.Name, _selected, StringComparison.OrdinalIgnoreCase))
            {
                item.Select(0);
            }
        }

        ShowEditor();
    }

    private void ShowEditor()
    {
        foreach (Node child in _edit.GetChildren())
        {
            _edit.RemoveChild(child);
            child.QueueFree();
        }

        if (Current is not { } r)
        {
            if (!_empty.Visible)
            {
                PanelUi.Caption(_edit, "Select an image to place, scale and fade it.");
            }

            return;
        }

        string name = r.Name;
        PanelUi.Header(_edit, "Image");
        var rename = new LineEdit { Text = r.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The image's name (Enter renames it)" };
        rename.TextSubmitted += text =>
        {
            string to = text.Trim();
            if (to.Length > 0 && !string.Equals(to, name, StringComparison.Ordinal)
                && Change($"Rename reference {name}", ("rename", ApiJson.Str(to))))
            {
                _selected = to;
            }
            else
            {
                rename.Text = name;
            }
        };
        PanelUi.Row(_edit, "Name", "The image's name (Enter renames it)").AddChild(rename);
        Label file = PanelUi.Caption(_edit, r.Path, wrap: false);
        file.TooltipText = ReferenceLayer.Resolve(r.Path, Context.Session.Folder);

        HBoxContainer viewRow = PanelUi.Row(_edit, "View");
        OptionButton view = PanelUi.Options(viewRow, "The 2D view the image is drawn in");
        view.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (string v in s_views)
        {
            view.AddItem(PanelUi.Words(v));
        }

        view.Select(Math.Max(0, Array.IndexOf(s_views, r.View)));
        view.ItemSelected += i => Change($"Reference {name} view", ("view", ApiJson.Str(s_views[i])));

        AxisFields axes = AxisFields.Create(["X", "Y", "Z"]);
        axes.SetValues([r.Origin.X, r.Origin.Y, r.Origin.Z]);
        Vec3 origin = r.Origin;
        axes.Edited += (axis, v, final) =>
        {
            if (final)
            {
                Change($"Move reference {name}", ("origin", ApiJson.Vec(origin.With(axis, v))));
            }
        };
        PanelUi.Row(_edit, "Origin", "The world point under the image's centre").AddChild(axes);
        HFlowContainer centre = PanelUi.Toolbar(_edit);
        PanelUi.Button(centre, "Centre on view", "Put the image's centre at the point under the view centre", () =>
        {
            if (Context.PlacementPoint?.Invoke() is { } at)
            {
                Change($"Move reference {name}", ("origin", ApiJson.Vec(at)));
            }
        });

        Number("Units per pixel", "World units one image pixel covers", r.UnitsPerPixel, 0.001, 1000, 0.01,
            v => Change($"Scale reference {name}", ("unitsPerPixel", ApiJson.Num(v))));
        if (ImageWidth(r) is { } pixels)
        {
            Number("Width (units)", "Calibrate: the width of the whole image in world units (sets units per pixel)",
                r.UnitsPerPixel * pixels, 1, 1e6, 1, v => Change($"Scale reference {name}", ("unitsPerPixel", ApiJson.Num(v / pixels))));
        }

        Number("Opacity", "0 is invisible, 1 is solid", r.Opacity, 0, 1, 0.05, v => Change($"Reference {name} opacity", ("opacity", ApiJson.Num(v))));

        PanelUi.Header(_edit, "Calibrate");
        PanelUi.Caption(_edit, $"Turn Grid Snap off (G), so the marks sit where you click, then Measure (M) in the {PanelUi.Words(r.View)} view " +
            "between two marks on the image whose real distance you know, and type that distance. The image scales about the first mark.");
        _measured = PanelUi.Caption(_edit, "");
        _length = PanelUi.Spin(PanelUi.Row(_edit, "Real length", "The real distance between the two measured marks, in units"),
            0.01, 1e6, 0.01, 256, "The real distance between the two marks, in units");
        _length.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _length.Suffix = "u";
        ShowMeasured();
        SpinBox length = _length;
        var calibrate = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _edit.AddChild(calibrate);
        PanelUi.Button(calibrate, "Calibrate from measure", "Scale the image so the last two Measure points are the real length apart", () =>
        {
            if (MeasuredMarks() is not { } m)
            {
                Context.SetStatus("Measure (M) between two marks on the image first.");
                return;
            }

            Change($"Calibrate reference {name}", ("calibrate", ApiJson.Obj(
                ("from", ApiJson.Vec(m.From)), ("to", ApiJson.Vec(m.To)), ("length", ApiJson.Num(length.Value)))));
        });
    }

    /// <summary>Shows the last measured distance across the selected image and starts the real length at it, so a
    /// new measurement is never paired with the length typed for the one before.</summary>
    private void ShowMeasured()
    {
        if (_measured is null || !IsInstanceValid(_measured) || _length is null || !IsInstanceValid(_length) || Current is not { } r)
        {
            return;
        }

        if (MeasuredMarks() is not { } marks)
        {
            _measured.Text = "Nothing measured yet.";
            return;
        }

        double across = (marks.To - marks.From).With(r.DepthAxis, 0).Length;
        string snapped = ToolManager.Instance?.GridSnap == true ? " (Grid Snap is on, so the marks are snapped to the grid)" : "";
        _measured.Text = string.Create(CultureInfo.InvariantCulture, $"Measured across the image: {across:0.##} units{snapped}");
        _length.Value = Math.Round(across, 2);
    }

    /// <summary>The last two points of the Measure tool, or null.</summary>
    private static (Vec3 From, Vec3 To)? MeasuredMarks() =>
        ToolManager.Instance?.Tools.OfType<MeasureTool>().FirstOrDefault()?.Points is { Count: >= 2 } p ? (p[^2], p[^1]) : null;

    private void Number(string title, string help, double value, double min, double max, double step, Action<double> changed)
    {
        HBoxContainer row = PanelUi.Row(_edit, title, help);
        SpinBox spin = PanelUi.Spin(row, min, max, step, value, help);
        spin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // Commit on Enter or focus loss, not on each keystroke.
        spin.GetLineEdit().TextSubmitted += _ => changed(spin.Value);
        spin.GetLineEdit().FocusExited += () =>
        {
            if (Math.Abs(spin.Value - value) > 1e-9)
            {
                changed(spin.Value);
            }
        };
    }

    private int? ImageWidth(ReferenceImage r) => ReferenceLayer.Load(ReferenceLayer.Resolve(r.Path, Context.Session.Folder))?.GetWidth();

    private void Add() => Dialogs.OpenFile("Add reference image", ["*.png, *.jpg, *.jpeg, *.webp ; Images"], AddFile, Context.Session.Folder);

    /// <summary>Adds an image to the active 2D view (the Top view when the 3D view is active), centred on the view.
    /// An image inside the project folder is stored relative to it, so the map can move.</summary>
    private void AddFile(string file)
    {
        string path = Context.Session.Folder is { } folder && Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, file).Replace('\\', '/')
            : Path.GetFullPath(file);
        string view = ViewportLayout.Instance?.Active.Kind switch
        {
            ViewKind.Front => "front",
            ViewKind.Side => "side",
            _ => "top",
        };
        Vec3 at = Context.PlacementPoint?.Invoke() ?? Vec3.Zero;
        if (view == "top" && StoreyFilter.Current is { } storey)
        {
            // On the storey shown, so it shows with it.
            at = at.With(2, storey.Level);
        }

        string? before = _selected;
        _selected = ReferenceImage.NewName(Context.Project.Workspace.References, path);
        if (!Change($"Add reference {_selected}", ("path", ApiJson.Str(path)), ("view", ApiJson.Str(view)), ("origin", ApiJson.Vec(at))))
        {
            _selected = before;
        }
    }

    /// <summary>Changes the selected (or just added) reference through the <c>reference</c> op. False (with the reason
    /// in the log and the status bar) when the op refused it.</summary>
    private bool Change(string label, params (string Key, SvJsonNode? Value)[] fields)
    {
        if (_selected is not { } name)
        {
            return false;
        }

        SvJsonObject op = ApiJson.Obj([("op", ApiJson.Str("reference")), ("name", ApiJson.Str(name)), .. fields]);
        SvJsonObject doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("label", ApiJson.Str(label)), ("ops", ApiJson.Arr([op])));
        ChangeResult result = ChangeApplier.Apply(Context.Session, doc);
        if (!result.Ok)
        {
            string error = ProposalBook.ErrorText(result.Json["error"] as SvJsonObject);
            Context.Log($"{label} failed: {error}", Severity.Error);
            Context.SetStatus($"{label} failed: {error}");
        }

        return result.Ok;
    }
}
