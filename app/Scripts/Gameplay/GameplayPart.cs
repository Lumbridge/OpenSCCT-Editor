using System.Globalization;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// Gameplay authoring in the shell: the GE, security and conveyor tools, and the Gameplay menu's actions (ledge
/// detection, flush and join, GE IDs and the activation mask, device placement and wall snapping, event links). Every
/// action works headless through <c>--run=</c> and reports on the status line.
/// </summary>
[ShellPart(Order = -28)]
public sealed class GameplayPart : IShellPart
{
    private static readonly int[] s_geIds = [0, 1, 2, 4, 8, 16, 32, 64, 128];

    private EditorContext _ctx = null!;
    private GameplayState _state = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        _state = GameplayState.Instance;
        _state.Changed += ViewOverlays.Invalidate;
        if (ToolManager.Instance is { } tools)
        {
            tools.AddTool(new GeDrawTool(tools, _state));
            tools.AddTool(new SecurityTool(tools, _state));
            tools.AddTool(new ConveyorTool(tools, _state));
        }

        TraversalActions();
        GeIdActions();
        SecurityActions();
        WiringActions();
        ConveyorActions();
    }

    private void Add(string id, string title, string menu, Action run, int order, string? description = null,
        Func<bool>? enabled = null, Func<bool>? isChecked = null) =>
        _ctx.AddAction(new EditorAction(id, title, menu, run)
        {
            Order = order,
            Description = description,
            IsEnabled = enabled ?? (() => true),
            IsChecked = isChecked,
        });

    private void TraversalActions()
    {
        const string Menu = "Gameplay/Traversal";
        Add("gameplay.ge.detect", "Detect Ledges", Menu, () =>
        {
            _state.Detect(SelectionBox());
            _ctx.SetStatus(_state.LastDetection);
        }, 10, "Finds grabbable ledge lips in the level geometry (inside the selection's bounds when something is selected)");
        Add("gameplay.ge.apply", "Add Detected Ledges", Menu, () =>
        {
            int n = _state.Apply();
            _ctx.SetStatus($"Added {n} ledge grabs");
        }, 20, enabled: () => _state.Proposals.Count > 0);
        Add("gameplay.ge.discard", "Discard Detected Ledges", Menu, () =>
        {
            _state.Discard();
            _ctx.SetStatus("Ledge proposals discarded");
        }, 30, enabled: () => _state.Proposals.Count > 0);
        Add("gameplay.ge.flush", "Flush Ledges to Edges", Menu, Flush, 40,
            "Moves the selected ledge grabs (all, with none selected) onto the edge next to them");
        Add("gameplay.ge.join", "Join Ledge Corners", Menu, Join, 50,
            "Moves ledge ends that nearly meet onto their shared corner (selected ledges, or all)");
        Add("gameplay.ge.meshes", "Detect on Static Meshes", Menu, () =>
        {
            _state.Options = _state.Options with { IncludeMeshes = !_state.Options.IncludeMeshes };
            _ctx.SetStatus($"Ledge detection on static meshes {(_state.Options.IncludeMeshes ? "on" : "off")}");
        }, 60, isChecked: () => _state.Options.IncludeMeshes);
    }

    private void GeIdActions()
    {
        int order = 0;
        foreach (int id in s_geIds)
        {
            int mask = id;
            string n = mask.ToString(CultureInfo.InvariantCulture);
            Add("gameplay.ge.id." + n, mask == 0 ? "GE ID 0 (Always On)" : "GE ID " + n, "Gameplay/GE ID", () =>
            {
                List<string> ids = SelectedGes(null);
                if (ids.Count > 0)
                {
                    _ctx.Execute(GeEdits.SetMask(_ctx.Project, ids, mask));
                }

                _ctx.SetStatus($"GE ID {mask} on {ids.Count} GEs");
            }, order++, enabled: () => SelectedGes(null).Count > 0);
        }

        for (int i = 0; i < GeEdits.Platforms.Count; i++)
        {
            int platform = i;
            string name = GeEdits.Platforms[i];
            Add("gameplay.ge.platform." + name, name == "common" ? "Every Platform" : name == "ps2" ? "PS2 Only" : "Xbox Only", "Gameplay/GE Platform", () =>
            {
                List<string> ids = SelectedGes(null);
                if (ids.Count > 0)
                {
                    _ctx.Execute(GeEdits.SetPlatform(_ctx.Project, ids, platform));
                }

                _ctx.SetStatus($"GE platform {name} on {ids.Count} GEs");
            }, i, enabled: () => SelectedGes(null).Count > 0);
        }

        for (int bit = 0; bit < 8; bit++)
        {
            int value = 1 << bit;
            string n = value.ToString(CultureInfo.InvariantCulture);
            Add("gameplay.ge.activation." + n, $"GE ID {n} On at Start", "Gameplay/GE Activation", () =>
            {
                int mask = ActivationMask(_ctx.Project) ^ value;
                _ctx.Execute(GeEdits.SetActivationMask(mask));
                _ctx.SetStatus($"GE activation mask {mask}");
            }, bit, isChecked: () => (ActivationMask(_ctx.Project) & value) != 0);
        }
    }

    private void SecurityActions()
    {
        const string Menu = "Gameplay/Security";
        int order = 0;
        foreach (DeviceSpec device in SecurityDevices.Placeable)
        {
            DeviceSpec spec = device;
            string id = spec.Kind.ToString().ToLowerInvariant();
            Add("gameplay.security.place." + id, "Place " + spec.Title, Menu, () => PlaceAtCursor(spec), order++,
                $"Puts a {spec.Class} on the wall nearest the selection (or the camera's view), facing out");
        }

        Add("gameplay.security.snap", "Snap to Wall", Menu, SnapSelected, 50,
            "Moves the selected actors onto the nearest wall, facing out of it", enabled: () => _ctx.Selection.Count > 0);
    }

    private void WiringActions()
    {
        const string Menu = "Gameplay/Wiring";
        Add("gameplay.wire.link", "Link Selected", Menu, LinkSelected, 10,
            "The first selected actor fires (or, for an alarm, locks) each of the others", enabled: () => SelectedActors().Count >= 2);
        Add("gameplay.wire.unlink", "Unlink Selected", Menu, UnlinkSelected, 20,
            "One actor selected: clears its events. Several: removes the links from the first to the others",
            enabled: () => SelectedActors().Count > 0);
        Add("gameplay.graph.report", "Report Event Links", Menu, ReportGraph, 30,
            "Counts links, dangling events and rings (of the selection, or the whole map)");
        Add("gameplay.missions.report", "Report Missions", Menu, ReportMissions, 40);
    }

    private void ConveyorActions()
    {
        const string Menu = "Gameplay/Conveyors";
        Add("gameplay.conveyor.drive", "Drive Selected Meshes", Menu, DriveSelected, 10,
            "Adds a driver conveyor fitted to the top of the selected mesh actors, scrolling their textures at its speed "
            + "(one repeat per the conveyor tool's texture tile)",
            enabled: () => SelectedActors().Count > 0);
    }

    private void DriveSelected()
    {
        List<Actor> meshes = SelectedActors().Select(id => _ctx.Project.FindActor(id)!).ToList();
        Actor actor;
        try
        {
            actor = ConveyorDriver.Fit(_ctx.Session.Folder, meshes, ConveyorTool.ScrollTile, axis: 1);
        }
        catch (ApiException ex)
        {
            _ctx.SetStatus(ex.Message);
            return;
        }

        actor.Id = _ctx.Project.NewId("Conveyor");
        var cmd = new AddObjectsCommand($"Add {actor.Id}", actors: [actor]);
        _ctx.Execute(cmd);
        _ctx.Selection.Set(cmd.Added);
        _ctx.SetStatus($"Added {actor.Id}: a driver over {meshes.Count} meshes, {ConveyorDesign.WorldPath(actor).Length:0} long; "
            + "set its speed in the Inspector");
    }

    private void Flush()
    {
        List<string> ids = SelectedGes(GeKinds.LedgeGrab, allWhenNone: true);
        var notFound = new List<string>();
        ReplaceObjectsCommand cmd = GeEdits.Flush(_ctx.Project, _state.World, ids, _state.Options, notFound);
        Dictionary<string, string> before = ids.ToDictionary(id => id, id => PointsText(_ctx.Project.FindGe(id)));
        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
        }

        int moved = ids.Count(id => PointsText(_ctx.Project.FindGe(id)) != before[id]);
        _ctx.SetStatus($"Flushed {ids.Count} ledges: {moved} moved onto their edge, {notFound.Count} with no edge near");
        if (notFound.Count > 0)
        {
            _ctx.Log($"No edge near: {string.Join(", ", notFound.Take(20))}{(notFound.Count > 20 ? " …" : "")}", Severity.Warning);
        }
    }

    private static string PointsText(Ge? g) => g is null ? "" : string.Join(";", g.Points);

    private void Join()
    {
        List<string> ids = SelectedGes(GeKinds.LedgeGrab, allWhenNone: true);
        ReplaceObjectsCommand cmd = GeEdits.JoinCorners(_ctx.Project, ids);
        Dictionary<string, string> before = ids.ToDictionary(id => id, id => PointsText(_ctx.Project.FindGe(id)));
        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
        }

        int moved = ids.Count(id => PointsText(_ctx.Project.FindGe(id)) != before[id]);
        _ctx.SetStatus($"Joined ledge corners: {moved} of {ids.Count} ledges moved an end onto a corner");
    }

    private void PlaceAtCursor(DeviceSpec spec)
    {
        Vec3 at = PlacementPoint(_ctx);
        if (SecurityDevices.SnapToWall(_state.World, spec, at) is not { } spot)
        {
            _ctx.SetStatus($"No wall within 512 of {at}");
            return;
        }

        SecurityTool.Add(_ctx, SecurityDevices.Place(_ctx.Project, spec, spot.Location, spot.Rotation));
    }

    private void SnapSelected()
    {
        List<string> ids = SelectedActors();
        var missed = new List<string>();
        ReplaceObjectsCommand cmd = SecurityDevices.Snap(_ctx.Project, _state.World, ids, missed);
        if (!cmd.IsEmpty)
        {
            _ctx.Execute(cmd);
        }

        _ctx.SetStatus($"Snapped {ids.Count - missed.Count} actors to walls{(missed.Count > 0 ? $", {missed.Count} with no wall within 512" : "")}");
    }

    /// <summary>Links the first selected actor to all the others in one undo step (an alarm locks or fires each; any
    /// other sender fires them through one shared Tag).</summary>
    private void LinkSelected()
    {
        List<string> ids = SelectedActors();
        if (ids.Count < 2 || Wiring.Link(_ctx.Project, ids[0], ids[1..]) is not { IsEmpty: false } cmd)
        {
            _ctx.SetStatus("Nothing linked");
            return;
        }

        string what = Wiring.Describe(_ctx.Project, ids[0], ids[1..]);
        _ctx.Execute(cmd);
        _ctx.SetStatus(what);
    }

    private void UnlinkSelected()
    {
        List<string> ids = SelectedActors();
        if (ids.Count == 1)
        {
            ReplaceObjectsCommand clear = Wiring.Unlink(_ctx.Project, ids);
            if (!clear.IsEmpty)
            {
                _ctx.Execute(clear);
            }

            _ctx.SetStatus($"Cleared the events of {ids[0]}");
            return;
        }

        int removed = 0;
        foreach (string to in ids.Skip(1))
        {
            if (Wiring.Unlink(_ctx.Project, ids[0], to) is { } cmd)
            {
                _ctx.Execute(cmd);
                removed++;
            }
        }

        _ctx.SetStatus(removed == 0 ? $"{ids[0]} has no links to the others" : $"Removed the links from {ids[0]} to {removed} actors");
    }

    private void ReportGraph()
    {
        GameplayGraph g = _state.Graph;
        List<string> ids = SelectedActors();
        if (ids.Count == 0)
        {
            _ctx.SetStatus($"{g.Links.Count} links, {g.Dangling.Count} dangling events, {g.Rings().Count} rings, {g.Nodes.Count()} actors wired");
            return;
        }

        int outgoing = ids.Sum(id => g.Out(id).Count), incoming = ids.Sum(id => g.In(id).Count);
        int dangling = g.Dangling.Count(d => ids.Contains(d.From, StringComparer.OrdinalIgnoreCase));
        _ctx.SetStatus($"{ids.Count} selected: {outgoing} links out, {incoming} in, {dangling} dangling events");
    }

    private void ReportMissions()
    {
        MissionTree t = _state.Missions;
        string missions = string.Join("; ", t.Missions.Select(m => $"{m.Id} {m.ModeName} {m.Objectives.Count} objectives (needs {m.MinimumObjectives})"));
        _ctx.SetStatus($"{t.Missions.Count} missions{(missions.Length > 0 ? ": " + missions : "")}; {t.Unlisted.Count} unlisted objectives; " +
            $"{t.MatchSize.Count} match-size rules");
    }

    /// <summary>Where a device (or tip, or SMagicEvent) placed from the menu goes near: the selection's centre, else what the camera looks at.</summary>
    internal static Vec3 PlacementPoint(EditorContext ctx)
    {
        if (ctx.Selection.Count > 0 && Viewports.ObjectGeometry.Bounds(ctx.Project, ctx.Selection.Items) is { } b)
        {
            return (b.Min + b.Max) / 2;
        }

        if (ctx.PlacementPoint is { } point)
        {
            return point();
        }

        Workspace w = ctx.Project.Workspace;
        return w.CameraLocation + w.CameraRotation.Axes().Forward * 256;
    }

    /// <summary>The bounds of the selection, grown a little, or null for the whole map.</summary>
    private (Vec3 Min, Vec3 Max)? SelectionBox() =>
        _ctx.Selection.Count > 0 && Viewports.ObjectGeometry.Bounds(_ctx.Project, _ctx.Selection.Items) is { } b
            ? (b.Min - Vec3.One * 64, b.Max + Vec3.One * 64)
            : null;

    private List<string> SelectedGes(string? kind, bool allWhenNone = false)
    {
        List<string> ids = _ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Ge).Select(r => r.Id)
            .Where(id => kind is null || _ctx.Project.FindGe(id)?.Kind == kind).ToList();
        return ids.Count == 0 && allWhenNone && !_ctx.Selection.Items.Any()
            ? _ctx.Project.Ges.Where(g => kind is null || g.Kind == kind).Select(g => g.Id).ToList()
            : ids;
    }

    private List<string> SelectedActors() =>
        _ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor && _ctx.Project.FindActor(r.Id) is not null).Select(r => r.Id).ToList();

    public static int ActivationMask(MapProject p) => p.LevelInfo.GetInt("GEActivationMask", 0);
}
