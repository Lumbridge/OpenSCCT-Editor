using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// Screenshot runs: <c>-- --shots=&lt;script.json | tour:full | tour:quick&gt; --shots-out=&lt;folder&gt;</c> (implies
/// background mode). Runs the script's steps one after another, then writes the PNGs, <c>manifest.json</c> (each
/// shot with the window size, scale, active tool, dock tabs, selection, UI check findings and the errors logged
/// while it was set up) and <c>index.html</c> (a contact sheet), and quits with 0, or 1 when a step failed. A step
/// that takes longer than <see cref="StepTimeout"/> ends the run with exit code 2.
/// </summary>
[ShellPart(Order = 110)]
public sealed partial class ShotRunner : Node, IShellPart
{
    private const string ShotsArg = "--shots=";
    private const string OutArg = "--shots-out=";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The keys each script op reads besides <c>op</c> and <c>frames</c>; input ops are checked by
    /// <see cref="UiDriver"/>. A misspelt key fails the step instead of quietly using a default.</summary>
    private static readonly Dictionary<string, string[]> s_stepKeys = new(StringComparer.Ordinal)
    {
        ["shot"] = ["name", "caption", "crop", "views"],
        ["wait"] = ["idle", "seconds"],
        ["size"] = ["w", "h", "window"],
        ["scale"] = ["factor"],
        ["open"] = ["path"],
        ["run"] = ["action"],
        ["select"] = ["what", "class", "ids", "frame"],
        ["tool"] = ["id"],
        ["tab"] = ["panel"],
        ["render"] = ["mode", "view"],
        ["menu"] = ["path"],
        ["palette"] = ["query"],
        ["quit"] = [],
    };

    private readonly SvJsonArray _shots = new();
    private readonly List<string> _failures = [];
    private readonly ShotLog _log = new();
    private EditorContext _ctx = null!;
    private MainWindow _window = null!;
    private UiDriver _driver = null!;
    private string _out = "";
    private string _spec = "";
    private int _index;
    private bool _finished;

    public void Attach(MainWindow window, EditorContext context)
    {
        string[] args = OS.GetCmdlineUserArgs();
        if (args.FirstOrDefault(a => a.StartsWith(ShotsArg, StringComparison.Ordinal)) is not { } shots || UiDriver.Instance is null)
        {
            return;
        }

        if (DisplayServer.GetName() == "headless")
        {
            GD.PrintErr("Shots: screenshots need a window; run without --headless (background mode keeps it off-screen).");
            EditorLifetime.Quit(window.GetTree(), 1);
            return;
        }

        _spec = shots[ShotsArg.Length..];
        _out = args.FirstOrDefault(a => a.StartsWith(OutArg, StringComparison.Ordinal))?[OutArg.Length..] ??
            Path.Combine(UserData.ShotsFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _ctx = context;
        _window = window;
        _driver = UiDriver.Instance;
        _driver.OutputFolder = _out;
        Name = "ShotRunner";
        window.AddChild(this);
        OS.AddLogger(_log);
        context.Logged += (text, severity) =>
        {
            if (severity != Severity.Info)
            {
                _log.Add($"{severity}: {text}");
            }
        };
        // Frame waits must always finish and input must use the synthetic pointer, also when OSE_FOCUS=1 shows the
        // window for debugging.
        OS.LowProcessorUsageMode = false;
        EditorPointer.Detached = true;
        EditorLifetime.Defer(() => { _ = Run(); });
    }

    private async Task Run()
    {
        // The watchdog runs from the start, so a stall before the first step (a map that never finishes loading)
        // still ends the run with a manifest.
        var watchdog = new Godot.Timer { OneShot = true, WaitTime = StepTimeout.TotalSeconds };
        watchdog.Timeout += () =>
        {
            _failures.Add(_index < 0
                ? $"the editor was not ready within {StepTimeout.TotalSeconds:0} s"
                : $"step {_index + 1} took over {StepTimeout.TotalSeconds:0} s");
            Finish(2);
        };
        AddChild(watchdog);
        _index = -1;
        watchdog.Start();
        try
        {
            Directory.CreateDirectory(_out);
            GD.Print($"Shots: {_spec} -> {_out}");
            SvJsonArray steps = Load(_spec);
            // A window restored from minimised can come back at another size: start every run at the project's size.
            await Guarded(null, () => Size(ApiJson.Obj(
                ("w", SvJsonNode.Integer(ProjectSettings.GetSetting("display/window/size/viewport_width").AsInt32())),
                ("h", SvJsonNode.Integer(ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32())))));
            GD.Print("Background window: " + BackgroundWindow.Report());
            await WaitIdle();
            for (_index = 0; _index < steps.Count && !_finished; _index++)
            {
                if (steps[_index] is not SvJsonObject step)
                {
                    _failures.Add($"step {_index + 1} is not an object");
                    continue;
                }

                watchdog.Start();
                bool more = true;
                await Guarded(step, async () => more = await RunStep(step));
                watchdog.Stop();
                if (!more)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // The script itself (missing, not JSON) or a failure between steps.
            _failures.Add(ex.Message);
            GD.PrintErr("Shots: " + ex.Message);
        }

        Finish(_failures.Count == 0 ? 0 : 1);
    }

    /// <summary>Runs a step, recording any failure against it; the run goes on with the next step.</summary>
    private async Task Guarded(SvJsonObject? step, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            string what = ex is ApiException or IOException or UnauthorizedAccessException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            string message = step is null ? what : $"step {_index + 1} ({ApiJson.Write(step)}): {what}";
            _failures.Add(message);
            GD.PrintErr("Shots: " + message);
        }
    }

    /// <summary>Runs one step; false ends the run (quit).</summary>
    private async Task<bool> RunStep(SvJsonObject step)
    {
        string op = ApiJson.GetString(step, "op") ?? throw new ApiException("each step needs an op");
        if (s_stepKeys.TryGetValue(op, out string[]? keys))
        {
            UiDriver.CheckKeys(step, op, keys);
        }
        else if (!UiDriver.IsInputOp(op))
        {
            throw new ApiException($"unknown op '{op}'");
        }

        switch (op)
        {
            case "shot":
                await Shot(step);
                break;
            case "wait":
                await Wait(step);
                break;
            case "size":
                await Size(step);
                break;
            case "scale":
                UiScale.Set(GetTree().Root, (float)ApiJson.Number(step, "factor", 1));
                await _driver.Frames(3);
                break;
            case "open":
                Documents.Instance?.OpenPath(ApiJson.GetString(step, "path") ?? throw new ApiException("open needs a path"));
                await WaitIdle();
                break;
            case "run":
            {
                string id = ApiJson.GetString(step, "action") ?? throw new ApiException("run needs an action");
                if (!_ctx.Run(id))
                {
                    throw new ApiException($"action {id} is missing or disabled");
                }

                await _driver.Frames(2);
                break;
            }

            case "select":
                Select(step);
                await _driver.Frames(2);
                break;
            case "tool":
                ToolManager.Instance?.Activate(ApiJson.GetString(step, "id") ?? "select");
                await _driver.Frames(2);
                break;
            case "tab":
                Tab(ApiJson.GetString(step, "panel") ?? throw new ApiException("tab needs a panel"));
                await _driver.Frames(2);
                break;
            case "render":
                Render(step);
                await _driver.Frames(2);
                break;
            case "menu":
                await Menu(ApiJson.GetString(step, "path") ?? throw new ApiException("menu needs a path"));
                break;
            case "palette":
                _ctx.Run("view.commandPalette");
                await _driver.Frames(2);
                UiInput.Type(ApiJson.GetString(step, "query") ?? "");
                await _driver.Frames(3);
                break;
            case "quit":
                return false;
            default:
                // Everything else is an input step (click, drag, key, hover, escape...), the same ones ui.input takes.
                await _driver.Input(step);
                break;
        }

        return true;
    }

    private async Task Shot(SvJsonObject step)
    {
        string name = $"{_shots.Count + 1:000}-{ApiJson.GetString(step, "name") ?? "shot"}";
        var args = ApiJson.Obj(("name", ApiJson.Str(name)), ("dir", ApiJson.Str(_out)), ("crop", step["crop"]),
            ("views", ApiJson.Bool(ApiJson.GetBool(step, "views", false))));
        SvJsonObject shot = await _driver.Shot(args);
        Window root = GetTree().Root;
        shot.Add("name", ApiJson.Str(name));
        shot.Add("caption", ApiJson.Str(ApiJson.GetString(step, "caption") ?? ApiJson.GetString(step, "name") ?? name));
        shot.Add("tool", ApiJson.Str(ToolManager.Instance?.Active.Id ?? ""));
        shot.Add("docks", ApiJson.Strings(_window.Docks
            .Where(d => d.GetTabCount() > 0).Select(d => $"{d.Name}: {DockTabs.Name(d, d.CurrentTab)}")));
        shot.Add("selection", SvJsonNode.Integer(_ctx.Selection.Count));
        shot.Add("objects", SvJsonNode.Integer(_ctx.Project.Brushes.Count + _ctx.Project.Actors.Count + _ctx.Project.Ges.Count));
        shot.Add("camera", ViewportLayout.Instance?.Views.FirstOrDefault(v => !v.Cam.IsOrtho) is { } persp ? ApiJson.Vec(persp.Cam.Location) : SvJsonNode.Null);
        shot.Add("modes", ApiJson.Strings(ViewportLayout.Instance?.Views.Where(v => v.IsVisibleInTree())
            .Select(v => $"{v.Kind}: {v.Mode}") ?? []));
        shot.Add("canvas", ApiJson.Arr([ApiJson.Num(root.GetVisibleRect().Size.X), ApiJson.Num(root.GetVisibleRect().Size.Y)]));
        shot.Add("log", ApiJson.Strings(_log.Drain()));
        _shots.Add(shot);
        int lint = shot["lint"] is SvJsonArray l ? l.Count : 0;
        GD.Print($"Shots: {name}{(lint > 0 ? $" ({lint} UI findings)" : "")}");
    }

    private async Task Wait(SvJsonObject step)
    {
        if (ApiJson.GetBool(step, "idle", false))
        {
            await WaitIdle();
        }

        if (ApiJson.Number(step, "seconds", 0) is > 0 and var seconds)
        {
            await ToSignal(GetTree().CreateTimer(seconds), Godot.Timer.SignalName.Timeout);
        }

        await _driver.Frames(Math.Max(1, (int)ApiJson.Number(step, "frames", 1)));
    }

    /// <summary>Waits until no background job runs (map imports, the asset index), then a few drawn frames.</summary>
    private async Task WaitIdle()
    {
        do
        {
            await _driver.Frames(3);
        }
        while (BackgroundJobs.IsBusy);
    }

    private async Task Size(SvJsonObject step)
    {
        var size = new Vector2I((int)ApiJson.Number(step, "w", 1600), (int)ApiJson.Number(step, "h", 900));
        string? name = ApiJson.GetString(step, "window");
        Window window = name is null ? GetTree().Root : GetTree().Root.FindChild(name, true, false) as Window
            ?? throw new ApiException($"no window named {name}");
        if (name is null) BackgroundWindow.Resize(size);
        else window.PopupCentered(size);
        await _driver.Frames(4);
        Vector2I real = window.Size;
        if (real != size)
        {
            throw new ApiException($"the window is {real.X}x{real.Y}, not the {size.X}x{size.Y} asked for (Windows limits a window to the desktop size)");
        }
    }

    private void Select(SvJsonObject step)
    {
        MapProject p = _ctx.Project;
        IEnumerable<ObjectRef> refs = ApiJson.GetString(step, "what") switch
        {
            "none" => [],
            "all" => Viewports.ObjectFilter.Pickable(p),
            "brush" => p.Brushes.Skip(p.Brushes.Count > 1 ? 1 : 0).Take(1).Select(b => ObjectRef.ForBrush(b)),
            "ge" => p.Ges.Take(1).Select(g => ObjectRef.Ge(g.Id)),
            _ when ApiJson.GetString(step, "class") is { } cls =>
                p.Actors.Where(a => string.Equals(a.Class, cls, StringComparison.OrdinalIgnoreCase)).Take(1).Select(a => ObjectRef.Actor(a.Id)),
            _ when ApiJson.StringList(step["ids"], "ids") is { } ids => ids.Select(id => MapObjects.Find(p, id)).OfType<ObjectRef>(),
            var w => throw new ApiException($"select takes what (none, all, brush, ge), class or ids, not {w}"),
        };
        List<ObjectRef> list = refs.ToList();
        if (list.Count == 0 && ApiJson.GetString(step, "what") != "none")
        {
            throw new ApiException($"nothing to select for {ApiJson.Write(step)}");
        }

        _ctx.Selection.Set(list);
        if (ApiJson.GetBool(step, "frame", false))
        {
            _ctx.Run("view.frame");
        }
    }

    private void Tab(string panel)
    {
        foreach (TabContainer dock in _window.Docks)
        {
            for (int i = 0; i < dock.GetTabCount(); i++)
            {
                if (string.Equals(DockTabs.Name(dock, i), panel, StringComparison.OrdinalIgnoreCase))
                {
                    DockTabs.Show(dock, i);
                    return;
                }
            }
        }

        throw new ApiException($"no panel '{panel}'");
    }

    private void Render(SvJsonObject step)
    {
        if (!Enum.TryParse(ApiJson.GetString(step, "mode"), ignoreCase: true, out RenderMode mode) || ViewportLayout.Instance is not { } layout)
        {
            throw new ApiException("render needs a mode: wireframe, brushColors, unlit, lit or lightmapDensity");
        }

        string? only = ApiJson.GetString(step, "view");
        foreach (EditorViewport v in layout.Views.Where(v => only is null || string.Equals(v.Kind.ToString(), only, StringComparison.OrdinalIgnoreCase)))
        {
            v.Mode = mode;
        }

        layout.ModesChanged();
    }

    /// <summary>Opens a menu bar menu by clicking its title, then hovers each submenu on the path.</summary>
    private async Task Menu(string path)
    {
        string[] parts = path.Split('/');
        await _driver.Input(ApiJson.Obj(("op", ApiJson.Str("click")), ("at", ApiJson.Str("menu:" + parts[0])), ("frames", SvJsonNode.Integer(3))));
        foreach (string sub in parts.Skip(1))
        {
            await _driver.Input(ApiJson.Obj(("op", ApiJson.Str("move")), ("at", ApiJson.Str("item:" + sub)), ("steps", SvJsonNode.Integer(3))));
            // Submenus open after the theme's delay.
            await ToSignal(GetTree().CreateTimer(0.5), Godot.Timer.SignalName.Timeout);
            await _driver.Frames(2);
        }
    }

    private SvJsonArray Load(string spec)
    {
        if (spec.StartsWith("tour:", StringComparison.Ordinal))
        {
            return ShotTour.Build(spec["tour:".Length..], _ctx, _window);
        }

        return ApiJson.Parse(File.ReadAllText(spec), "shot script") switch
        {
            SvJsonArray a => a,
            SvJsonObject { } o when o["steps"] is SvJsonArray a => a,
            _ => throw new ApiException("a shot script is a JSON array of steps, or an object with steps"),
        };
    }

    private void Finish(int code)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        var manifest = ApiJson.Obj(
            ("script", ApiJson.Str(_spec)),
            ("map", ApiJson.Str(_ctx.Project.Id)),
            ("background", ApiJson.Bool(BackgroundMode.Active)),
            ("window", ApiJson.Str(BackgroundWindow.Report())),
            ("failures", ApiJson.Strings(_failures)),
            ("shots", _shots));
        string json = JsonNode.Parse(ApiJson.Write(manifest))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_out, "manifest.json"), json);
        ContactSheet.Write(Path.Combine(_out, "index.html"), _ctx.Project.Id, _spec, _shots, _failures);
        int lint = _shots.OfType<SvJsonObject>().Sum(s => s["lint"] is SvJsonArray l ? l.Count : 0);
        GD.Print($"Shots: {_shots.Count} shots, {lint} UI findings, {_failures.Count} failed steps. {Path.Combine(_out, "index.html")}");
        // Straight out: the window's close request would ask about unsaved changes.
        EditorLifetime.Quit(GetTree(), code);
    }
}
