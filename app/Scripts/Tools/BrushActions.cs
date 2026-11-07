using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// The Brush menu: CSG Add / Subtract from the builder brush (also as semi-solid and non-solid), Intersect and
/// Deintersect, the builder shape, copying a brush into the builder, and the selected brushes' CSG type, solidity
/// and order.
/// </summary>
[ShellPart(Order = -25)]
public sealed class BrushActions : IShellPart
{
    private EditorContext _ctx = null!;
    private ToolManager _tools = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        _tools = ToolManager.Instance!;
        Add("brush.palette", "Geometry Palettes", "Brush", null, 5, () => DockTabs.ShowPanel("Geometry"));
        bool BrushSelected() => SelectedBrushes().Any();

        Add("brush.add", "Add", "Brush", "Alt+A", 10, () => Place(CsgOper.Add, 0), "Add the builder brush as solid", () => !_tools.Builder.GestureActive);
        Add("brush.subtract", "Subtract", "Brush", "Alt+S", 20, () => Place(CsgOper.Subtract, 0), "Carve the builder brush out of the world", () => !_tools.Builder.GestureActive);
        Add("brush.addSemisolid", "Add Semisolid", "Brush", null, 30, () => Place(CsgOper.Add, PolyFlags.Semisolid), enabled: () => !_tools.Builder.GestureActive);
        Add("brush.addNonSolid", "Add Non-Solid", "Brush", null, 40, () => Place(CsgOper.Add, PolyFlags.NotSolid), enabled: () => !_tools.Builder.GestureActive);
        Add("brush.intersect", "Intersect", "Brush", null, 50, () => Clip(keepSolid: true),
            "Shrink the builder brush to the solid world inside it");
        Add("brush.deintersect", "Deintersect", "Brush", null, 60, () => Clip(keepSolid: false),
            "Shrink the builder brush to the empty space inside it");
        Add("brush.toBuilder", "Copy to Builder Brush", "Brush", null, 200, CopyToBuilder, "The selected brush's shape becomes the builder brush",
            () => _ctx.Selection.Primary is { Kind: ObjectKind.Brush });
        Add("brush.builderHere", "Builder Brush Here", "Brush", null, 210, BuilderHere, "Move the builder brush to the cursor",
            () => ViewportLayout.Instance?.CursorWorld is not null);
        Add("brush.resetBuilder", "Reset Builder Brush", "Brush", null, 220, () =>
        {
            if (_tools.Builder.GestureActive) _tools.Active.Cancel();
            _tools.Builder.SetKind("cube");
            _tools.Builder.Location = new Vec3(0, 0, 128);
            _tools.Builder.Rotation = Rot.Zero;
            _tools.Builder.Moved();
        });

        foreach (string kind in BuilderBrush.Kinds())
        {
            string k = kind;
            _ctx.AddAction(new EditorAction("brush.kind." + k, BuilderBrush.KindTitle(k), "Brush/Builder Shape", () =>
            {
                if (_tools.Builder.GestureActive) _tools.Active.Cancel();
                _tools.Builder.SetKind(k);
                _tools.Activate("brush");
            })
            {
                Order = 100,
                RadioGroup = "brush.kind",
                IsChecked = () => string.Equals(_tools.Builder.Spec.Kind, k, StringComparison.OrdinalIgnoreCase),
            });
        }

        Add("brush.makeAdd", "Add", "Brush/CSG Type", null, 100, () => EditBrushes("Make additive", b => b.Oper = CsgOper.Add), null, BrushSelected,
            () => Primary()?.Oper == CsgOper.Add);
        Add("brush.makeSubtract", "Subtract", "Brush/CSG Type", null, 110, () => EditBrushes("Make subtractive", b => b.Oper = CsgOper.Subtract), null,
            BrushSelected, () => Primary()?.Oper == CsgOper.Subtract);

        const int solidityMask = PolyFlags.Semisolid | PolyFlags.NotSolid;
        Add("brush.solid", "Solid", "Brush/Solidity", null, 100, () => SetSolidity(0), null, BrushSelected,
            () => Primary() is { } b && (b.PolyFlags & solidityMask) == 0);
        Add("brush.semisolid", "Semisolid", "Brush/Solidity", null, 110, () => SetSolidity(PolyFlags.Semisolid), null, BrushSelected,
            () => Primary()?.IsSemisolid == true);
        Add("brush.nonsolid", "Non-Solid", "Brush/Solidity", null, 120, () => SetSolidity(PolyFlags.NotSolid), null, BrushSelected,
            () => Primary()?.IsNotSolid == true);

        Add("brush.orderFirst", "To First", "Brush/Order", null, 100, () => Reorder(toLast: false), "Move to the start of the CSG order", BrushSelected);
        Add("brush.orderLast", "To Last", "Brush/Order", null, 110, () => Reorder(toLast: true), "Move to the end of the CSG order", BrushSelected);
    }

    private void Add(string id, string title, string menu, string? shortcut, int order, Action run, string? description = null,
        Func<bool>? enabled = null, Func<bool>? isChecked = null) =>
        _ctx.AddAction(new EditorAction(id, title, menu, run)
        {
            Shortcut = shortcut,
            Order = order,
            Description = description,
            IsEnabled = enabled ?? (() => true),
            IsChecked = isChecked,
        });

    private IEnumerable<ObjectRef> SelectedBrushes() => _ctx.Selection.Items.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair));

    /// <summary>The last selected brush (its state is what the toggles show).</summary>
    private Brush? Primary()
    {
        foreach (ObjectRef r in _ctx.Selection.Items.Reverse())
        {
            if (r.Kind is (ObjectKind.Brush or ObjectKind.Stair))
            {
                return _ctx.Project.FindBrush(r.Id);
            }
        }

        return null;
    }

    private void Place(CsgOper oper, int flags)
    {
        if (_tools.Builder.GestureActive) return;
        List<Brush> brushes = _tools.Builder.MakeBrushes(_ctx.Project, oper, flags);
        string what = oper == CsgOper.Add ? flags == 0 ? "Add" : (flags & PolyFlags.Semisolid) != 0 ? "Add semisolid" : "Add non-solid" : "Subtract";
        if (brushes.Count == 0)
        {
            if (_tools.Builder.Problem is { } problem)
            {
                _ctx.Log($"{what}: {problem}", Severity.Warning);
            }

            return;
        }

        var cmd = new AddObjectsCommand($"{what} {brushes[0].Id}", brushes: brushes);
        _ctx.Execute(cmd);
        _ctx.Selection.Set(cmd.Added);
        _ctx.SetStatus($"{what}: {string.Join(", ", brushes.Select(b => b.Id))}");
    }

    /// <summary>Intersect or Deintersect: the builder brush becomes the solid (or empty) part of the world it covers.</summary>
    private void Clip(bool keepSolid)
    {
        if (_tools.Builder.GestureActive) _tools.Active.Cancel();
        string what = keepSolid ? "Intersect" : "Deintersect";
        BuilderCsgResult result = keepSolid
            ? BuilderCsg.Intersect(_ctx.Project, _tools.Builder)
            : BuilderCsg.Deintersect(_ctx.Project, _tools.Builder);
        if (result.Error is { } error)
        {
            _ctx.Log($"{what}: {error}", Severity.Warning);
            return;
        }

        if (result.SkippedConcave > 0)
        {
            _ctx.Log($"{what}: skipped {result.SkippedConcave} concave brushes (stairs, sheets...)", Severity.Warning);
        }

        if (result.Pieces.Count == 0)
        {
            _ctx.SetStatus($"{what}: nothing left; the builder brush is unchanged");
            return;
        }

        _tools.Builder.SetPieces(result.Pieces);
        _tools.Activate("brush");
        _ctx.SetStatus($"{what}: the builder brush is now {result.Pieces.Count} convex piece{(result.Pieces.Count == 1 ? "" : "s")}");
    }

    private void CopyToBuilder()
    {
        if (_tools.Builder.GestureActive) _tools.Active.Cancel();
        if (_ctx.Selection.Primary is { Kind: ObjectKind.Brush } r && _ctx.Project.FindBrush(r.Id) is { } b)
        {
            _tools.Builder.CopyFrom(b);
            _ctx.SetStatus($"Builder brush copied from {b.Id}");
        }
    }

    private void BuilderHere()
    {
        if (_tools.Builder.GestureActive) _tools.Active.Cancel();
        if (ViewportLayout.Instance?.CursorWorld is { } at)
        {
            _tools.Builder.Location = at;
            _tools.Builder.Moved();
        }
    }

    private void EditBrushes(string name, Action<Brush> edit)
    {
        var cmd = ReplaceObjectsCommand.Edit(_ctx.Project, SelectedBrushes().ToList(), name, brush: edit);
        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
        }
    }

    private void SetSolidity(int flag) =>
        EditBrushes(flag == 0 ? "Make solid" : flag == PolyFlags.Semisolid ? "Make semisolid" : "Make non-solid",
            b => b.PolyFlags = (b.PolyFlags & ~(PolyFlags.Semisolid | PolyFlags.NotSolid)) | flag);

    private void Reorder(bool toLast)
    {
        List<string> ids = SelectedBrushes().Select(r => r.Id).ToList();
        if (ids.Count > 0)
        {
            _ctx.Execute(new ReorderBrushesCommand(ids, toLast));
        }
    }
}
