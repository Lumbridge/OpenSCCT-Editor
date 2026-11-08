using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Tools;

/// <summary>
/// Typed transform values in the toolbar: the primary selection's location (move tool), rotation in degrees (rotate
/// tool) or scale (scale tool). Enter applies the change to the whole selection as one undo step: moves and
/// rotations by the same difference, scales by the same ratio. An empty field leaves that component alone.
/// </summary>
public sealed partial class TransformBar : HBoxContainer
{
    private readonly ToolManager _tools;
    private readonly Label _label = new() { CustomMinimumSize = new Vector2(64, 0), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly LineEdit[] _fields = new LineEdit[3];
    private readonly SpinBox[] _spinners = new SpinBox[3];
    private bool _refreshing;

    /// <summary>The (rounded) text each field was filled with: a field still showing it was not edited.</summary>
    private readonly string[] _shown = ["", "", ""];

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public TransformBar()
        : this(ToolManager.Instance!)
    {
    }

    public TransformBar(ToolManager tools)
    {
        _tools = tools;
        AddChild(_label);
        string[] names = ["X", "Y", "Z"];
        for (int i = 0; i < 3; i++)
        {
            SpinBox spin = PanelUi.Spin(this, -1048576, 1048576, 0.000000001, 0, names[i], 96);
            spin.AllowGreater = spin.AllowLesser = true;
            spin.CustomArrowStep = 1;
            LineEdit edit = spin.GetLineEdit();
            spin.ValueChanged += value =>
            {
                if (_refreshing) { return; }
                edit.Text = value.ToString("R", CultureInfo.InvariantCulture);
                Apply();
            };
            edit.TextSubmitted += _ =>
            {
                Apply();
                edit.ReleaseFocus();
            };
            edit.FocusExited += Apply;
            _fields[i] = edit;
            _spinners[i] = spin;
        }

        EditorContext ctx = tools.Context;
        ctx.SelectionChanged += Refresh;
        ctx.ProjectChanged += _ => Refresh();
        ctx.SessionReplaced += Refresh;
        tools.Changed += Refresh;
        Refresh();
    }

    private TransformMode Mode => _tools.Active is TransformTool t ? t.Mode : TransformMode.Move;

    public void Refresh()
    {
        _refreshing = true;
        MapProject p = _tools.Context.Project;
        string[] labels = Mode switch
        {
            TransformMode.Rotate => ["Pitch", "Yaw", "Roll"],
            TransformMode.Scale => ["Scale X", "Scale Y", "Scale Z"],
            _ => ["X", "Y", "Z"],
        };
        _label.Text = Mode switch { TransformMode.Rotate => "Rotation°", TransformMode.Scale => "Scale", _ => "Location" };
        (Vec3 Location, Rot Rotation, Vec3 Scale)? state = _tools.Context.Selection.Primary is { } r
            ? new TransformSnapshot(p, [r]).Start(r)
            : null;
        for (int i = 0; i < 3; i++)
        {
            LineEdit f = _fields[i];
            f.PlaceholderText = labels[i];
            _spinners[i].Editable = state is not null;
            if (f.HasFocus())
            {
                continue;
            }

            f.Text = _shown[i] = state is not { } s ? "" : Mode switch
            {
                TransformMode.Rotate => Format(Rot.ToDegrees(i == 0 ? s.Rotation.Pitch : i == 1 ? s.Rotation.Yaw : s.Rotation.Roll)),
                TransformMode.Scale => Format(s.Scale[i]),
                _ => Format(s.Location[i]),
            };
            if (double.TryParse(f.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                _spinners[i].SetValueNoSignal(number);
                f.Text = _shown[i];
            }
        }
        _refreshing = false;
    }

    private void Apply()
    {
        EditorContext ctx = _tools.Context;
        if (ctx.Selection.Primary is not { } primary)
        {
            return;
        }

        var snapshot = new TransformSnapshot(ctx.Project, ctx.Selection.Items);
        if (snapshot.Start(primary) is not { } start)
        {
            return;
        }

        // Untouched fields leave their component alone (their text is rounded, so re-reading it would nudge it).
        double?[] typed = _fields.Select((f, i) => f.Text != _shown[i]
            && PanelUi.TryNum(f.Text, out double v) ? v : (double?)null).ToArray();
        for (int i = 0; i < typed.Length; i++)
        {
            if (typed[i] is { } value) _spinners[i].SetValueNoSignal(value);
        }
        IEditCommand? cmd = Mode switch
        {
            TransformMode.Move => snapshot.Translate(ctx.Project, new Vec3(
                (typed[0] ?? start.Location.X) - start.Location.X,
                (typed[1] ?? start.Location.Y) - start.Location.Y,
                (typed[2] ?? start.Location.Z) - start.Location.Z), null),
            TransformMode.Rotate => RotateBy(ctx.Project, snapshot,
                Delta(typed[0], start.Rotation.Pitch), Delta(typed[1], start.Rotation.Yaw), Delta(typed[2], start.Rotation.Roll)),
            _ => ScaleBy(ctx.Project, snapshot, new Vec3(Ratio(typed[0], start.Scale.X), Ratio(typed[1], start.Scale.Y), Ratio(typed[2], start.Scale.Z))),
        };
        if (cmd is not null)
        {
            ctx.Execute(cmd);
        }

        Refresh();
    }

    private static int Delta(double? degrees, int current) => degrees is { } d ? Rot.FromDegrees(d) - current : 0;

    private static double Ratio(double? typed, double current) => typed is { } t && Math.Abs(current) > 1e-9 ? t / current : 1;

    private static ReplaceObjectsCommand RotateBy(MapProject p, TransformSnapshot s, int pitch, int yaw, int roll)
    {
        Rot Add(Rot r) => new(r.Pitch + pitch, r.Yaw + yaw, r.Roll + roll);
        return ReplaceObjectsCommand.Edit(p, s.Refs, "Rotate (typed)",
            brush: b => b.Rotation = Add(s.Start(ObjectRef.ForBrush(b))!.Value.Rotation),
            actor: a => a.Rotation = Add(s.Start(ObjectRef.Actor(a.Id))!.Value.Rotation));
    }

    private static ReplaceObjectsCommand ScaleBy(MapProject p, TransformSnapshot s, Vec3 ratio) =>
        ReplaceObjectsCommand.Edit(p, s.Refs, "Scale (typed)",
            brush: b => TextureLock.Rescale(b, x => x.MainScale = x.MainScale.Scale(ratio), SurfaceActions.ScaleKeepsTextures),
            actor: a => a.DrawScale3D = a.DrawScale3D.Scale(ratio));

    private static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
