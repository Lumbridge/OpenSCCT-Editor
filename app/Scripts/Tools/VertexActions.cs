using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Tools;

/// <summary>Runs vertex edits on the selected vertices of each brush as one undoable step, keeps the vertex selection
/// on the moved vertices and puts a convexity warning in the status bar.</summary>
public static class VertexCommands
{
    private static EditorContext Ctx => EditorContext.Instance;

    private static GeometrySelection Geo => GeometrySelection.Instance;

    /// <summary>Applies <paramref name="edit"/> to every brush with selected vertices. <paramref name="moved"/> maps
    /// an old selected position to its new one (null drops it from the selection).</summary>
    public static bool Run(string name, Func<Brush, List<Vec3>, VertexEditResult> edit, Func<Vec3, Vec3?> moved, string? mergeKey = null,
        IReadOnlyList<Brush>? from = null, IReadOnlyList<VertexRef>? selection = null)
    {
        // A copy: the edit prunes the live list before the moved positions are put back.
        List<VertexRef> sel = (selection ?? Geo.Vertices).ToList();
        if (sel.Count == 0)
        {
            Ctx.SetStatus($"{name}: no vertices selected (vertex mode, V)");
            return false;
        }

        var cmd = new ReplaceObjectsCommand(name, mergeKey);
        var warnings = new List<string>();
        try
        {
            Dictionary<string, List<Vec3>> byBrush = sel.GroupBy(v => v.Brush, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(v => v.Position).ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(Ctx.Project);
            foreach (Brush b in from ?? Geo.VertexBrushes())
            {
                if (!byBrush.TryGetValue(b.Id, out List<Vec3>? positions) || !brushes.TryGetValue(b.Id, out Brush? live))
                {
                    continue;
                }

                VertexEditResult r = edit(b, positions);
                if (r.Warning is { } w)
                {
                    warnings.Add(w);
                }

                cmd.Brush(live.Clone(), r.Brush);
            }
        }
        catch (ArgumentException ex)
        {
            Ctx.Log($"{name}: {ex.Message}", Severity.Warning);
            return false;
        }

        if (cmd.IsEmpty)
        {
            return false;
        }

        Ctx.Execute(cmd);
        Geo.SetVertices(sel.Select(v => moved(v.Position) is { } p ? v with { Position = p } : (VertexRef?)null).OfType<VertexRef>());
        Ctx.SetStatus(warnings.Count > 0 ? warnings[0] : $"{name}: {sel.Count} vertices");
        return true;
    }

    public static void Move(Vec3 delta, string? mergeKey = null) =>
        Run("Move vertices", (b, pts) => VertexEdit.Move(b, pts, delta), p => p + delta, mergeKey);

    /// <summary>Welds every selected vertex, across brushes too, into one point at their centre.</summary>
    public static void Weld()
    {
        if (Geo.Vertices.Count < 2)
        {
            Ctx.SetStatus("Weld: select two or more vertices");
            return;
        }

        Vec3 centre = OpenScct.Editor.Core.Geometry.Poly.Tidy(VertexEdit.Centre(Geo.Vertices.Select(v => v.Position).ToList()));
        Run("Weld vertices", (b, pts) => VertexEdit.Weld(b, pts, centre), _ => centre);
    }

    public static void Delete() => Run("Delete vertices", VertexEdit.Delete, _ => null);

    public static void Snap(double grid) => Run("Snap vertices", (b, pts) => VertexEdit.Snap(b, pts, grid), p => p.Snap(grid));

    /// <summary>Bevels the edge between the two selected vertices of a brush by <paramref name="size"/>.</summary>
    public static void Bevel(double size)
    {
        if (Geo.Vertices.Count != 2 || !string.Equals(Geo.Vertices[0].Brush, Geo.Vertices[1].Brush, StringComparison.OrdinalIgnoreCase)
            || Ctx.Project.FindBrush(Geo.Vertices[0].Brush) is not { } b)
        {
            Ctx.SetStatus("Bevel: select the two vertices of one edge (edge mode picks both)");
            return;
        }

        try
        {
            Brush after = VertexEdit.Bevel(b, Geo.Vertices[0].Position, Geo.Vertices[1].Position, size);
            Ctx.Execute(new ReplaceObjectsCommand($"Bevel edge of {b.Id}").Brush(b.Clone(), after));
            Geo.SetVertices([]);
            Ctx.SetStatus($"Bevelled an edge of {b.Id} by {size:0.##}");
        }
        catch (ArgumentException ex)
        {
            Ctx.Log($"Bevel: {ex.Message}", Severity.Warning);
        }
    }

    /// <summary>Selects every vertex of the selected brushes.</summary>
    public static void SelectAll()
    {
        IEnumerable<Brush> brushes = Ctx.Selection.Items.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)).Select(r => Ctx.Project.FindBrush(r.Id)).OfType<Brush>();
        Geo.SetVertices(brushes.SelectMany(b => VertexEdit.Vertices(b).Select(v => new VertexRef(b.Id, v))));
        Ctx.SetStatus($"{Geo.Vertices.Count} vertices selected");
    }
}

/// <summary>
/// The Brush/Vertices menu: select all vertices, weld, delete, snap to grid, nudge by the grid along each axis, bevel
/// the selected edge, and the vertex/edge mode switch.
/// </summary>
[ShellPart(Order = -22)]
public sealed class VertexActions : IShellPart
{
    private EditorContext _ctx = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        GeometrySelection geo = GeometrySelection.Instance;
        bool HasVertices() => geo.Vertices.Count > 0;
        double Grid() => ToolManager.Instance?.GridSize ?? 16;

        Add("vertex.selectAll", "Select All Vertices", 100, VertexCommands.SelectAll,
            () => context.Selection.Items.Any(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)), "Every vertex of the selected brushes");
        Add("vertex.clear", "Clear Vertex Selection", 110, () => geo.SetVertices([]), HasVertices);
        Add("vertex.weld", "Weld", 200, VertexCommands.Weld, () => geo.Vertices.Count >= 2, "Merge the selected vertices at their centre");
        Add("vertex.delete", "Delete Vertices", 210, VertexCommands.Delete, HasVertices, "The brush becomes the hull of the vertices left");
        Add("vertex.snap", "Snap Vertices to Grid", 220, () => VertexCommands.Snap(Grid()), HasVertices);
        Add("vertex.bevel", "Bevel Edge by Grid", 230, () => VertexCommands.Bevel(Grid()), () => geo.Vertices.Count == 2,
            "Cut the selected edge off, a grid step back along both faces");
        for (int axis = 0; axis < 3; axis++)
        {
            int a = axis;
            string n = "XYZ"[axis].ToString();
            Add($"vertex.nudge{n}+", $"Nudge +{n}", 300 + axis * 2, () => VertexCommands.Move(Vec3.Zero.With(a, Grid())), HasVertices);
            Add($"vertex.nudge{n}-", $"Nudge -{n}", 301 + axis * 2, () => VertexCommands.Move(Vec3.Zero.With(a, -Grid())), HasVertices);
        }

        Add("vertex.edgeMode", "Edge Mode", 400, () => VertexTool.EdgeMode = !VertexTool.EdgeMode, null,
            "In the vertex tool, clicks pick edges (both of their vertices)", () => VertexTool.EdgeMode);
    }

    private void Add(string id, string title, int order, Action run, Func<bool>? enabled, string? description = null, Func<bool>? isChecked = null) =>
        _ctx.AddAction(new EditorAction(id, title, "Brush/Vertices", run)
        {
            Order = order,
            IsEnabled = enabled ?? (() => true),
            Description = description,
            IsChecked = isChecked,
        });
}
