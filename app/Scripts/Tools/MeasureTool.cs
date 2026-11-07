using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Measure (Shift+M): each click adds a point (on the surface under the cursor in 3D, on the grid in 2D views) and
/// every view shows the run with each leg's length, player heights, dX/dY/dZ and spy/merc running time. Clicks go on
/// from the last point; double-click starts a new run; Escape clears. Points snap to the grid.
/// </summary>
public sealed class MeasureTool : IViewportTool
{
    private static readonly Color Line = new(0.45f, 1f, 0.6f);
    private readonly ToolManager _tools;
    private readonly List<Vec3> _points = new();

    public MeasureTool(ToolManager tools) => _tools = tools;

    public string Id => "measure";

    public string Title => "Measure";

    public string Shortcut => "Shift+M";

    private EditorContext Ctx => _tools.Context;

    public IReadOnlyList<Vec3> Points => _points;

    /// <summary>Raised when the measured points change (a click, a measure between objects, a clear).</summary>
    public static event Action? Measured;

    public bool Input(EditorViewport view, InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press)
        {
            return false;
        }

        Vec3 at = view.Cam.IsOrtho
            ? Placement.Point(_tools, view.Cam, press.Position, depth: _points.Count > 0 ? _points[^1][ViewAxes.DepthAxis(view.Kind)] : null)
            : Picking.Surface(view.Cam, Ctx.Project, press.Position) is { } hit ? _tools.Snap(hit.Point) : Placement.Point(_tools, view.Cam, press.Position);
        if (press.DoubleClick)
        {
            _points.Clear();
        }

        _points.Add(at);
        Report();
        return true;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        ViewCamera cam = view.Cam;
        MovementLimits m = Ctx.Project.Workspace.Movement;
        for (int i = 0; i < _points.Count; i++)
        {
            FacePicking.DrawMarker(cam, canvas, _points[i], Line, 4);
            if (i == 0 || cam.ToScreen(_points[i - 1]) is not { } a || cam.ToScreen(_points[i]) is not { } b)
            {
                continue;
            }

            canvas.DrawLine(a, b, Line, 2, antialiased: true);
            Measurement leg = Measurement.Between(_points[i - 1], _points[i], m);
            FacePicking.DrawLabel(canvas, (a + b) / 2 + new Vector2(6, -6),
                string.Create(CultureInfo.InvariantCulture, $"{leg.Distance:0.#} u · {leg.PlayerHeights:0.##} h"), Line);
            if (i == _points.Count - 1)
            {
                FacePicking.DrawLabel(canvas, b + new Vector2(10, 18), Detail(leg), Line);
            }
        }
    }

    public void BuildOptions(HBoxContainer bar) =>
        ToolOptions.Hint(bar, "Click to add points", "Double‑click starts again", "Esc clears");

    public bool Cancel()
    {
        if (_points.Count == 0)
        {
            return false;
        }

        _points.Clear();
        ViewportLayout.Instance?.RedrawAll();
        Measured?.Invoke();
        return true;
    }

    /// <summary>Measures between two points (the selection's pivots, from the Measure menu).</summary>
    public void Between(Vec3 a, Vec3 b)
    {
        _points.Clear();
        _points.Add(a);
        _points.Add(b);
        Report();
    }

    private void Report()
    {
        ViewportLayout.Instance?.RedrawAll();
        Measured?.Invoke();
        if (_points.Count < 2)
        {
            Ctx.SetStatus($"Measure from {_points[0]}");
            return;
        }

        Measurement leg = Measurement.Between(_points[^2], _points[^1], Ctx.Project.Workspace.Movement);
        double total = 0;
        for (int i = 1; i < _points.Count; i++)
        {
            total += (_points[i] - _points[i - 1]).Length;
        }

        Ctx.SetStatus(leg.Summary() + (_points.Count > 2 ? string.Create(CultureInfo.InvariantCulture, $" · total {total:0.#} u") : ""));
    }

    private static string Detail(Measurement m) => string.Create(CultureInfo.InvariantCulture,
        $"dX {m.Delta.X:0.#}  dY {m.Delta.Y:0.#}  dZ {m.Delta.Z:0.#} · spy {m.SpySeconds:0.##} s · merc {m.MercSeconds:0.##} s");
}

/// <summary>The Measure menu entries that work without clicking: between the two selected objects, and clear.</summary>
[ShellPart(Order = -21)]
public sealed class MeasureActions : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        MeasureTool tool = ToolManager.Instance!.Tools.OfType<MeasureTool>().First();
        context.AddAction(new EditorAction("measure.selection", "Measure Between Selected", "Tools/Measure", () =>
        {
            List<Vec3> pivots = context.Selection.Items.Select(r => ObjectGeometry.Pivot(context.Project, r)).OfType<Vec3>().ToList();
            if (pivots.Count >= 2)
            {
                tool.Between(pivots[^2], pivots[^1]);
            }
        })
        {
            IsEnabled = () => context.Selection.Count >= 2,
            Description = "Distance, player heights and run times between the last two selected objects",
        });
        context.AddAction(new EditorAction("measure.clear", "Clear Measurement", "Tools/Measure", () => tool.Cancel())
        {
            IsEnabled = () => tool.Points.Count > 0,
            Order = 10,
        });
    }
}
