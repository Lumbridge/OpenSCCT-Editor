using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>The right side of the status bar, each field set off by a hairline: running jobs (with a progress bar),
/// the cursor position, grid, rotation and neighbour snap, the active tool, the selection and the object counts. Updated ten
/// times a second, cheaply (labels only change when their text does).</summary>
[ShellPart(Order = 80)]
public sealed class StatusBarPart : IShellPart
{
    private const string Minus = "−";

    private readonly PanelContainer _jobs = new() { Name = "Jobs", ThemeTypeVariation = "StatusSegment", Visible = false };
    private readonly Label _jobName = new() { ThemeTypeVariation = "StatusText", VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _cursor = Field("Cursor", "Where the pointer is in the map (x, y, z)");
    private readonly Label _grid = Field("Grid", "Grid size, rotation step and neighbour snap distance; \"off\" when that snapping is off");
    private readonly Label _tool = Field("Tool", "The active tool");
    private readonly Label _selection = Field("Selection", "");
    private readonly Label _kind = new() { Name = "Kind", ThemeTypeVariation = "StatusField" };
    private readonly Label _counts = Field("Counts", "Objects in the map");
    private EditorContext _ctx = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        HBoxContainer row = window.StatusRow;

        var job = new HBoxContainer();
        job.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        _jobs.AddChild(job);
        var bar = new ProgressBar
        {
            Indeterminate = true,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(64, 4),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        job.AddChild(bar);
        job.AddChild(_jobName);
        row.AddChild(_jobs);

        _cursor.CustomMinimumSize = new Vector2(EditorTheme.StatusCursorWidth, 0);
        _cursor.HorizontalAlignment = HorizontalAlignment.Right;
        _cursor.AddThemeFontOverride("font", EditorTheme.NumberFont);
        _selection.AddThemeColorOverride("font_color", EditorTheme.Text);
        // The kind follows the name inside the same field: no hairline between them.
        _kind.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        foreach (Label l in new[] { _cursor, _grid, _tool, _selection, _kind, _counts })
        {
            row.AddChild(l);
        }

        var tick = new Godot.Timer { WaitTime = 0.1, Autostart = true };
        tick.Timeout += Update;
        window.AddChild(tick);
    }

    private static Label Field(string name, string tooltip) =>
        new() { Name = name, ThemeTypeVariation = "StatusField", TooltipText = tooltip, MouseFilter = Control.MouseFilterEnum.Pass };

    private void Update()
    {
        _jobs.Visible = BackgroundJobs.IsBusy;
        Set(_jobName, BackgroundJobs.IsBusy ? string.Join(" · ", BackgroundJobs.Running) + "…" : "");

        Vec3? c = ViewportLayout.Instance?.CursorWorld;
        Set(_cursor, c is { } p ? $"X {Num(p.X)}  Y {Num(p.Y)}  Z {Num(p.Z)}" : "");
        if (ToolManager.Instance is { } t)
        {
            string grid = string.Create(CultureInfo.InvariantCulture, $"Grid {t.GridSize:0.##}{(t.GridSnap ? "" : " off")}");
            string rot = string.Create(CultureInfo.InvariantCulture, $"Rot {Rot.ToDegrees(t.RotationGrid):0.###}°{(t.RotationSnap ? "" : " off")}");
            string neighbours = t.NeighbourSnap
                ? string.Create(CultureInfo.InvariantCulture, $"Neighbours {t.NeighbourSnapPx:0.#} px")
                : "Neighbours off";
            Set(_grid, grid + " · " + rot + " · " + neighbours);
            Set(_tool, t.Active.Title);
        }

        int n = _ctx.Selection.Count;
        (string name, string kind) = n switch
        {
            0 => ("", ""),
            1 => Describe(_ctx.Selection.Primary!.Value),
            _ => (PanelUi.Int(n) + " selected", ""),
        };
        Set(_selection, name);
        Set(_kind, kind);
        MapProject project = _ctx.Project;
        Set(_counts, $"{PanelUi.Count(project.Brushes.Count, "brush")} · {PanelUi.Count(project.Actors.Count, "actor")} · " +
            PanelUi.Count(project.Ges.Count, "GE"));
    }

    /// <summary>One selected object as its name and its class's display name ("Corridor0", "Brush"; "Alarm3",
    /// "SAlarm"); the tooltip has the class as stored.</summary>
    private (string Name, string Kind) Describe(ObjectRef r)
    {
        string kind = ObjectInfo.Find(_ctx.Project, r)?.Class ?? r.Kind.ToString();
        _selection.TooltipText = $"{r.Id} ({kind})";
        return (r.Id, PanelUi.DisplayName(kind));
    }

    /// <summary>A coordinate to one decimal, with a true minus sign; a value that rounds to zero is "0", never "−0".</summary>
    private static string Num(double v)
    {
        double r = Math.Round(v, 1);
        return (r == 0 ? 0 : r).ToString("0.#", CultureInfo.InvariantCulture).Replace("-", Minus, StringComparison.Ordinal);
    }

    /// <summary>Sets a label's text only when it changes, and hides an empty field with its hairline.</summary>
    private static void Set(Label l, string text)
    {
        if (l.Text != text)
        {
            l.Text = text;
        }

        l.Visible = text.Length > 0;
    }
}
