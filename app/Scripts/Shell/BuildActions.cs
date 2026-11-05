using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>
/// The Build menu: build to SVMap, build and play in OpenSCCT, and Map Check. Uses <see cref="EditorServices.Compiler"/>
/// and <see cref="EditorServices.Checker"/> when a module provides them; the actions stay disabled otherwise.
/// Builds go to <c>builds/&lt;MapId&gt;</c> in <see cref="UserData.Folder"/>, which is also the maps folder the game is
/// pointed at. Build > Play Options sets the side, game mode, bots and their difficulty, and rules a play starts with
/// (saved with the map). Play From Camera spawns the player once on the floor under the 3D view's camera, or under
/// where it looks when the camera is outside the level; Play From Cursor on the floor under the point the mouse is on
/// (OpenSCCT's <c>--start-at</c>).
/// </summary>
[ShellPart(Order = -40)]
public sealed class BuildActions : IShellPart
{
    private const string ModePrefix = "build.mode.";
    private EditorContext _ctx = null!;
    private string _modes = "";

    /// <summary>The folder builds are written under (one SVMap folder per map id).</summary>
    public static string BuildsFolder => UserData.PathOf("builds");

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        bool CanBuild() => EditorServices.Compiler is not null && !BackgroundJobs.IsRunning("Build");
        bool CanCheck() => EditorServices.Checker is not null && !BackgroundJobs.IsRunning("Check");
        context.AddAction(new EditorAction("build.build", "Build", "Build", () => Build(play: false))
        {
            Shortcut = "F7", Order = 100, IsEnabled = CanBuild, Description = "Compile the map to an SVMap folder",
        });
        context.AddAction(new EditorAction("build.play", "Build and Play", "Build", () => Build(play: true))
        {
            Shortcut = "Ctrl+P", Order = 110, IsEnabled = CanBuild, Description = "Build, then play it in OpenSCCT with the play options",
        });
        context.AddAction(new EditorAction("build.playFromCamera", "Build and Play From Camera", "Build", () => PlayFrom(CameraStart()))
        {
            Shortcut = "Alt+P", Order = 115, IsEnabled = CanBuild,
            Description = "Build, then play it in OpenSCCT starting on the floor under the 3D view's camera, or under where it looks when the " +
                "camera is outside the level (later lives use the map's starts)",
        });
        context.AddAction(new EditorAction("build.playFromHere", "Build and Play From Cursor", "Build", () => PlayFrom(CursorStart()))
        {
            Order = 116, IsEnabled = () => CanBuild() && ViewportLayout.Instance?.CursorWorld is not null,
            Description = "Build, then play it in OpenSCCT starting on the floor under the point the mouse is on, facing as the 3D view's camera " +
                "(later lives use the map's starts)",
        });
        context.AddAction(new EditorAction("build.playLast", "Play Last Build", "Build", PlayLast)
        {
            Shortcut = "Shift+F5", Order = 120, IsEnabled = () => Directory.Exists(OutputFolder(_ctx.Project.Id)),
        });
        context.AddAction(new EditorAction("build.check", "Check Map", "Build", () => Check(TargetGame.OpenScct))
        {
            Shortcut = "F8", Order = 200, IsEnabled = CanCheck, Description = "Run Map Check (findings go to the Map Check panel)",
        });
        context.AddAction(new EditorAction("build.checkOriginal", "Check for Original Export", "Build", () => Check(TargetGame.Original))
        {
            Order = 210, IsEnabled = CanCheck, Description = "Map Check with the original engine's limits as errors",
        });
        context.AddAction(new EditorAction("build.showOutput", "Show Build Folder", "Build", () => ExternalPrograms.OpenFolder(BuildsFolder))
        {
            Order = 300,
        });

        foreach (string side in new[] { "spy", "merc" })
        {
            context.AddAction(new EditorAction("build.side." + side, "Play as " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(side), "Build/Play Options",
                () => _ctx.Execute(WorkspaceEdits.Play($"Play as {side}", o => o.Side = side)))
            {
                Order = side == "spy" ? 100 : 110,
                RadioGroup = "play.side",
                IsChecked = () => string.Equals(_ctx.Project.Workspace.Play.Side, side, StringComparison.OrdinalIgnoreCase),
            });
        }

        foreach (int bots in new[] { 0, 1, 3, 5, 7 })
        {
            int n = bots;
            context.AddAction(new EditorAction($"build.bots.{n}", n == 0 ? "No Bots" : $"{n} Bots", "Build/Play Options", () => _ctx.Execute(WorkspaceEdits.Play("Play bots", o => o.Bots = n)))
            {
                Order = 200 + n,
                RadioGroup = "play.bots",
                IsChecked = () => _ctx.Project.Workspace.Play.Bots == n,
            });
        }

        foreach (string rules in new[] { "playlevel", "map" })
        {
            context.AddAction(new EditorAction("build.rules." + rules, rules == "map" ? "Map's Own Rules" : "Play Level Rules", "Build/Play Options",
                () => _ctx.Execute(WorkspaceEdits.Play("Play rules", o => o.Rules = rules)))
            {
                Order = rules == "map" ? 310 : 300,
                RadioGroup = "play.rules",
                IsChecked = () => string.Equals(_ctx.Project.Workspace.Play.Rules, rules, StringComparison.OrdinalIgnoreCase),
            });
        }

        int order = 500;
        foreach (string difficulty in OpenScct.Editor.Core.Scripting.ChangeApplier.Difficulties)
        {
            string title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(difficulty);
            context.AddAction(new EditorAction("build.difficulty." + difficulty, title + " Bots", "Build/Play Options",
                () => _ctx.Execute(WorkspaceEdits.Play($"Play with {difficulty} bots", o => o.Difficulty = difficulty)))
            {
                Order = order++,
                RadioGroup = "play.difficulty",
                IsChecked = () => string.Equals(_ctx.Project.Workspace.Play.Difficulty, difficulty, StringComparison.OrdinalIgnoreCase),
                Description = "How well the bots play (OpenSCCT's bot skill presets)",
            });
        }

        context.SessionReplaced += RefreshModes;
        context.ProjectChanged += _ => RefreshModes();
        RefreshModes();
    }

    /// <summary>One "Mode" entry per game mode the map has settings for (they change when the map's settings do).</summary>
    private void RefreshModes()
    {
        List<string> modes = _ctx.Project.MapSettings.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        string key = string.Join("|", modes);
        if (key == _modes)
        {
            return;
        }

        _modes = key;
        foreach (string id in _ctx.Actions.Select(a => a.Id).Where(id => id.StartsWith(ModePrefix, StringComparison.Ordinal)).ToList())
        {
            _ctx.RemoveAction(id);
        }

        int order = 400;
        foreach (string mode in modes)
        {
            _ctx.AddAction(new EditorAction(ModePrefix + mode.ToLowerInvariant(), "Mode: " + mode, "Build/Play Options",
                () => _ctx.Execute(WorkspaceEdits.Play($"Play {mode}", o => o.Mode = mode)))
            {
                Order = order++,
                RadioGroup = "play.mode",
                IsChecked = () => string.Equals(_ctx.Project.Workspace.Play.Mode, mode, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    public static string OutputFolder(string mapId) => Path.Combine(BuildsFolder, mapId);

    /// <summary>The 3D view's camera as a play start.</summary>
    private PlayStart? CameraStart() => PlayStart.Camera(_ctx.Project.Workspace);

    /// <summary>The point under the mouse as a play start, a little toward the camera (off a wall it is on), facing as
    /// the camera does.</summary>
    private PlayStart? CursorStart()
    {
        if (ViewportLayout.Instance?.CursorWorld is not { } at)
        {
            return null;
        }

        Workspace w = _ctx.Project.Workspace;
        Vec3 toCamera = (w.CameraLocation - at).Normalized();
        return new PlayStart(at + toCamera * 48 + Vec3.UnitZ * 8, new Rot(0, w.CameraRotation.Yaw, 0));
    }

    /// <summary>Builds and plays with the player starting on the floor under <paramref name="start"/>.</summary>
    private void PlayFrom(PlayStart? start)
    {
        if (start is not { } wanted)
        {
            return;
        }

        PlayStart grounded;
        try
        {
            grounded = ActorPlacement.OnFloor(new WorldGeometry(_ctx.Project, _ctx.Session.Folder), wanted);
        }
        catch (ArgumentException ex)
        {
            _ctx.Log($"Cannot play from there: {ex.Message}.", Severity.Error);
            _ctx.SetStatus("Cannot play from there: no floor");
            return;
        }

        _ctx.Log(FormattableString.Invariant($"Playing from {grounded.Location} (yaw {Rot.ToDegrees(grounded.Rotation.Yaw):0}°)"));
        Build(play: true, grounded);
    }

    private void Build(bool play, PlayStart? startAt = null)
    {
        if (EditorServices.Compiler is not { } compiler)
        {
            _ctx.Log("No map compiler is available in this build.", Severity.Warning);
            return;
        }

        MapProject snapshot = ProjectSnapshot.Take(_ctx.Project);
        EditorSession session = _ctx.Session;
        string output = OutputFolder(snapshot.Id);
        var options = new CompileOptions
        {
            ContentRoot = EditorServices.ContentRoot,
            ProjectFolder = _ctx.Session.Folder,
            Progress = line => BackgroundJobs.Post(() => _ctx.SetStatus(line)),
        };
        _ctx.Log($"Building {snapshot.Id} to {output}");
        BackgroundJobs.Run("Build", () =>
        {
            Directory.CreateDirectory(BuildsFolder);
            return compiler.Compile(snapshot, output, options);
        }, result =>
        {
            Report("Build", result.Findings, session);
            string stats = string.Join(", ", result.Stats.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
            _ctx.Log($"Build {(result.Ok ? "done" : "failed")} in {result.Elapsed.TotalSeconds:0.0} s{(stats.Length > 0 ? ": " + stats : "")}",
                result.Ok ? Severity.Info : Severity.Error);
            _ctx.SetStatus(result.Ok ? $"Built {snapshot.Id}" : "Build failed");
            if (play && result.Ok)
            {
                Launch(snapshot.Id, startAt);
            }
        });
    }

    private void PlayLast() => Launch(_ctx.Project.Id);

    private void Launch(string mapId, PlayStart? startAt = null)
    {
        string? exe = GameLauncher.FindGame();
        if (exe is null)
        {
            _ctx.Log("No OpenSCCT game found: set OSE_GAME_EXE (or OPENSCCT_EXE), or install OpenSCCT with its launcher.", Severity.Error);
            return;
        }

        if (GameExe.TooOld(exe) is { } tooOld)
        {
            _ctx.Log(tooOld, Severity.Error);
            return;
        }

        IReadOnlyList<string> args = GameLauncher.PlayArguments(BuildsFolder, mapId, _ctx.Project.Workspace.Play, startAt);
        try
        {
            int pid = GameLauncher.Play(exe, args);
            _ctx.Log($"Playing {mapId} (pid {pid}): {exe} {string.Join(' ', args)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _ctx.Log($"Could not start {exe}: {ex.Message}", Severity.Error);
        }
    }

    private void Check(TargetGame target)
    {
        if (EditorServices.Checker is not { } checker)
        {
            _ctx.Log("No map checker is available in this build.", Severity.Warning);
            return;
        }

        MapProject snapshot = ProjectSnapshot.Take(_ctx.Project);
        EditorSession session = _ctx.Session;
        string? folder = session.Folder;
        BackgroundJobs.Run("Check", () => checker.Check(snapshot, new CheckOptions { Target = target, ProjectFolder = folder }), findings =>
        {
            Report("Map Check", findings, session);
            _ctx.SetStatus(findings.Count == 0 ? "Map Check: no problems" : $"Map Check: {findings.Count} findings");
        });
    }

    /// <summary>Logs a job's findings; the Map Check panel shows them only while <paramref name="session"/> (the map
    /// they name objects of) is still open.</summary>
    private void Report(string what, IReadOnlyList<Finding> findings, EditorSession session)
    {
        if (ReferenceEquals(session, _ctx.Session))
        {
            _ctx.SetFindings(findings);
        }

        foreach (Finding f in findings.Where(f => f.Severity != Severity.Info).Take(200))
        {
            _ctx.Log($"{what}: {f}", f.Severity);
        }

        int errors = findings.Count(f => f.Severity == Severity.Error);
        int warnings = findings.Count(f => f.Severity == Severity.Warning);
        _ctx.Log($"{what}: {errors} errors, {warnings} warnings, {findings.Count - errors - warnings} notes",
            errors > 0 ? Severity.Error : warnings > 0 ? Severity.Warning : Severity.Info);
    }
}
