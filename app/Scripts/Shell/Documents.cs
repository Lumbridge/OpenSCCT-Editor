using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// The open map's life cycle: new, open, save, save as, recent files, import, export, autosave, save backups, the window
/// title and the unsaved-changes question on close. Registers the File menu actions.
/// </summary>
[ShellPart(Order = -50)]
public sealed class Documents : IShellPart
{
    private const string RecentPrefix = "file.recent.";
    private const string BackupPrefix = "file.backup.";

    /// <summary>Named jobs that write files (the build folder, an export, an import's work folder, the map's
    /// pictures): quitting while one runs is asked first.</summary>
    private static readonly string[] s_writingJobs = ["Build", "Export", "Import", Panels.IdentityPanel.RenderJob];

    private EditorContext _ctx = null!;
    private MainWindow _window = null!;
    private Godot.Timer? _autosave;
    private bool _autosaveCurrent;
    private int _minutesSinceAutosave;
    private EditorSession? _hooked;

    public static Documents? Instance { get; private set; }

    /// <summary>Raised just before a save writes the project (viewports store the camera in the workspace).</summary>
    public static event Action? BeforeSave;

    /// <summary>Raised after an autosave was written, with the copy's folder (crash recovery remembers it).</summary>
    public static event Action<string>? Autosaved;

    /// <summary>Where autosaves go (rotating slots per map).</summary>
    public static string AutosaveFolder => UserData.PathOf("autosave");

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        _window = window;
        RegisterActions();
        RefreshRecent();
        context.SessionReplaced += OnSessionReplaced;
        context.Saved += RefreshBackups;
        context.GuideChanged += () => { _autosaveCurrent = false; UpdateTitle(); };
        context.ProjectChanged += _ =>
        {
            _autosaveCurrent = false;
            UpdateTitle();
        };
        window.CloseRequested += Quit;

        _autosave = new Godot.Timer { Name = "Autosave", OneShot = false, WaitTime = 60 };
        _autosave.Timeout += AutosaveTick;
        window.AddChild(_autosave);
        _autosave.Start();

        string? startPath = StartPath();
        if (startPath is not null)
        {
            OpenPath(startPath);
        }
        else
        {
            NewMap();
        }

        OnSessionReplaced();
    }

    /// <summary>Starts a new map with the starter room (asks about unsaved changes first).</summary>
    public void New() => GuardUnsaved(NewMap);

    public void Open() => GuardUnsaved(() => Dialogs.OpenFolder("Open map project (.osmap folder)", OpenPath, LastFolder()));

    /// <summary>Opens a project folder (or its map.json), or imports any other map an importer reads.</summary>
    public void OpenPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (string.Equals(Path.GetFileName(full), ProjectStore.MapFile, StringComparison.OrdinalIgnoreCase))
        {
            full = Path.GetDirectoryName(full)!;
        }

        if (Directory.Exists(full) && ProjectStore.IsProject(full))
        {
            try
            {
                _ctx.ReplaceSession(EditorSession.Open(full));
                AppSettings.AddRecent(full);
                RefreshRecent();
                _ctx.Log($"Opened {full}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException
                or OpenScct.Content.SvMapFormatException)
            {
                _ctx.Log($"Could not open {full}: {ex.Message}", Severity.Error);
            }

            return;
        }

        if (EditorServices.ImporterFor(full) is not null)
        {
            Import(full);
            return;
        }

        _ctx.Log($"{full} is not a map project and no importer reads it.", Severity.Error);
        AppSettings.RemoveRecent(full);
        RefreshRecent();
    }

    /// <summary>Opens an autosave copy as the map it was taken of (it saves back to that project, when it still
    /// exists) with its changes unsaved.</summary>
    public void OpenAutosave(string copyFolder)
    {
        try
        {
            _ctx.ReplaceSession(EditorSession.OpenAutosave(copyFolder));
            _ctx.Log($"Opened the autosave {copyFolder}" + (_ctx.Session.Folder is { } f ? $"; saving puts it back in {f}" : "; Save As to keep it"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException
            or OpenScct.Content.SvMapFormatException)
        {
            _ctx.Log($"Could not open the autosave {copyFolder}: {ex.Message}", Severity.Error);
        }
    }

    /// <summary>The newest autosave of the open map that is newer than its last save, or null.</summary>
    public AutosaveCopy? LatestAutosave() => Autosaves.Latest(AutosaveFolder, _ctx.Session.Folder, _ctx.Project.Id);

    /// <summary>Saves to the project's folder, or asks for one first. <paramref name="then"/> runs once the save
    /// succeeded (not when the dialog is cancelled or the save fails).</summary>
    public void Save(Action? then = null)
    {
        if (_ctx.Session.Folder is null)
        {
            SaveAs(then);
            return;
        }

        if (SaveTo(_ctx.Session.Folder))
        {
            then?.Invoke();
        }
    }

    public void SaveAs(Action? then = null)
    {
        string start = _ctx.Session.Folder is { } f ? Path.GetDirectoryName(f)! : LastFolder() ?? "";
        Dialogs.SaveFile("Save map project as", ["*.osmap ; OpenSCCT map project"], _ctx.Project.Id + ProjectStore.Extension, path =>
        {
            if (!path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase))
            {
                path += ProjectStore.Extension;
            }

            if (SaveTo(path))
            {
                then?.Invoke();
            }
        }, start);
    }

    /// <summary>The import file dialog's choices: how a cooked .sdc's brushes are recovered.</summary>
    private static readonly (string Name, string Label, string[] Values, string[] Labels, int Default)[] s_importChoices =
    [
        ("brushes", "Recover brushes (cooked .sdc)", ["merge", "cells", "off"],
            ["Merged, as RE+ does", "Cells joined along whole faces", "Off: baked world only"], 0),
    ];

    /// <summary>Imports a map file or folder through the first importer that reads it; the result opens unsaved.
    /// <paramref name="brushes"/> is how a cooked .sdc's brushes are recovered.</summary>
    public void Import(string path) => Import(path, BrushRecoveryMode.Merge);

    public void Import(string path, BrushRecoveryMode brushes)
    {
        IMapImporter? importer = EditorServices.ImporterFor(path);
        if (importer is null)
        {
            _ctx.Log($"No importer reads {path}.", Severity.Error);
            return;
        }

        var options = new ImportOptions { ContentRoot = EditorServices.ContentRoot, BrushRecovery = brushes, Progress = Progress };
        // Edits made while the import ran get the unsaved-changes question before the result replaces them.
        BackgroundJobs.Run("Import", () => EditorServices.Import(importer, path, options), result => GuardUnsaved(() =>
        {
            _ctx.ReplaceSession(new EditorSession(result.Project), ImportReport.Build(result.Project, result.Findings));
            _ctx.SetFindings(result.Findings);
            int errors = result.Findings.Count(f => f.Severity == Severity.Error);
            _ctx.Log($"Imported {path} ({importer.Name}): {result.Project.Brushes.Count} brushes, {result.Project.Actors.Count} actors, " +
                $"{result.Project.Ges.Count} GEs, {result.Findings.Count} findings", errors > 0 ? Severity.Warning : Severity.Info);
            _ctx.SetStatus($"Imported {result.Project.Id}");
        }));
    }

    public void Export(IMapExporter exporter)
    {
        void Run(string path, IReadOnlyDictionary<string, string> picked)
        {
            MapProject snapshot = ProjectSnapshot.Take(_ctx.Project);
            EditorSession session = _ctx.Session;
            var options = new ExportOptions { ContentRoot = EditorServices.ContentRoot, ProjectFolder = session.Folder, Choices = picked, Progress = Progress };
            BackgroundJobs.Run("Export", () => exporter.Export(snapshot, path, options), result =>
            {
                if (ReferenceEquals(session, _ctx.Session))
                {
                    _ctx.SetFindings(result.Findings);
                }

                foreach (Finding f in result.Findings)
                {
                    _ctx.Log(f.ToString(), f.Severity);
                }

                _ctx.Log(result.Ok
                    ? $"Exported {exporter.Name}: {(exporter.WritesFolder ? $"{result.Files.Count} files in {path}" : string.Join(", ", result.Files))}"
                    : $"Export {exporter.Name} failed ({result.Findings.Count} findings)", result.Ok ? Severity.Info : Severity.Error);
            });
        }

        if (exporter.WritesFolder)
        {
            // The folder gets the map's files (an earlier export's are replaced; a folder with other things is refused).
            Dialogs.OpenFolder("Export for the original editor: choose an empty folder", path => Run(path, new Dictionary<string, string>()), LastFolder());
            return;
        }

        string ext = exporter.Extension.StartsWith('.') ? exporter.Extension : "." + exporter.Extension;
        var choices = exporter.Choices.Select(c => (c.Name, c.Label, c.Values.ToArray(), (c.ValueLabels ?? c.Values).ToArray(),
            Math.Max(0, c.Values.ToList().IndexOf(c.Default)))).ToList();
        Dialogs.SaveFile($"Export {exporter.Name.ToUpperInvariant()}", [$"*{ext} ; {exporter.Name.ToUpperInvariant()}"], _ctx.Project.Id + ext, choices, Run, LastFolder());
    }

    /// <summary>Runs <paramref name="next"/> once unsaved changes are saved or discarded (or at once when clean).</summary>
    public void GuardUnsaved(Action next)
    {
        // Once the editor is quitting nothing more is asked or done: the window is already gone.
        if (EditorLifetime.IsQuitting)
        {
            return;
        }

        if (!_ctx.Session.IsDirty)
        {
            next();
            return;
        }

        Dialogs.AskSave(_ctx.Project.Title, () => Save(next), next);
    }

    /// <summary>Quits the editor (the window's close button, File > Quit): asks first when a job that writes files is
    /// still running (quitting stops it) and when there are unsaved changes.</summary>
    private void Quit()
    {
        if (Animation.AnimationWorkspacePart.Studio is { HasUnsavedChanges: true } studio)
        {
            studio.GuardUnsaved(Quit);
            return;
        }
        if (EditorLifetime.IsQuitting)
        {
            return;
        }

        string[] busy = s_writingJobs.Where(BackgroundJobs.IsRunning).ToArray();
        if (busy.Length == 0)
        {
            GuardUnsaved(_window.Quit);
            return;
        }

        Dialogs.AskQuitWhileBusy(string.Join(" and ", busy), () => GuardUnsaved(_window.Quit));
    }

    private void NewMap()
    {
        MapProject p = MapProject.CreateDefault("NewMD", "New map");
        p.Workspace.GridSize = Preferences.DefaultGrid;
        Starter.AddStarterRoom(p);
        _ctx.ReplaceSession(new EditorSession(p));
        _ctx.Log("New map");
    }

    private bool SaveTo(string folder)
    {
        try
        {
            BeforeSave?.Invoke();
            bool restoring = _ctx.Session.RestoresBackup;
            _ctx.Session.Save(folder);
            foreach (string warning in _ctx.Session.SaveWarnings)
            {
                _ctx.Log(warning, Severity.Warning);
            }

            if (restoring && !_ctx.Session.RestoresBackup)
            {
                // The backup's assets and prefabs replaced the project's on disk.
                Assets.AssetService.Instance.ProjectAssetsChanged();
            }

            AppSettings.AddRecent(folder);
            RefreshRecent();
            UpdateTitle();
            _ctx.Log($"Saved {_ctx.Session.Folder}");
            _ctx.SetStatus("Saved");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ctx.Log($"Save failed: {ex.Message}", Severity.Error);
            return false;
        }
    }

    private void AutosaveTick()
    {
        int minutes = AppSettings.AutosaveMinutes;
        if (minutes <= 0 || !_ctx.Session.IsDirty || _autosaveCurrent)
        {
            return;
        }

        // The timer ticks every minute; save when the interval has passed since the last autosave.
        _minutesSinceAutosave++;
        if (_minutesSinceAutosave < minutes)
        {
            return;
        }

        _minutesSinceAutosave = 0;
        AutosaveNow();
    }

    /// <summary>Writes an autosave copy now (rotating slots under <see cref="AutosaveFolder"/>).</summary>
    public void AutosaveNow()
    {
        try
        {
            BeforeSave?.Invoke();
            string written = _ctx.Session.Autosave(AutosaveFolder, AppSettings.AutosaveSlots);
            _autosaveCurrent = true;
            _ctx.Log($"Autosaved to {written}");
            Autosaved?.Invoke(written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Log($"Autosave failed: {ex.Message}", Severity.Warning);
        }
    }

    private void OnSessionReplaced()
    {
        if (_hooked is not null)
        {
            _hooked.Saved -= UpdateTitle;
        }

        _hooked = _ctx.Session;
        _hooked.Saved += UpdateTitle;
        _autosaveCurrent = false;
        _minutesSinceAutosave = 0;
        UpdateTitle();
        RefreshBackups();
    }

    private void UpdateTitle()
    {
        EditorSession s = _ctx.Session;
        string where = s.Folder ?? "unsaved";
        _window.GetWindow().Title = $"{(s.IsDirty ? "* " : "")}{s.Project.Title} ({s.Project.Id}) - {where} - OpenSCCT Editor";
    }

    private void RegisterActions()
    {
        _ctx.AddAction(new EditorAction("file.new", "New", "File", () => { if (Animation.AnimationWorkspacePart.Active) Animation.AnimationWorkspacePart.Studio!.NewDocument(); else New(); }) { Shortcut = "Ctrl+N", Order = 100 });
        _ctx.AddAction(new EditorAction("file.open", "Open…", "File", () => { if (Animation.AnimationWorkspacePart.Active) Animation.AnimationWorkspacePart.Studio!.OpenDocument(); else Open(); }) { Shortcut = "Ctrl+O", Order = 110 });
        _ctx.AddAction(new EditorAction("file.recent.clear", "Clear Recent", "File/Open Recent", () =>
        {
            AppSettings.ClearRecent();
            RefreshRecent();
        }) { Order = 900, IsEnabled = () => AppSettings.Recent.Count > 0 });
        _ctx.AddAction(new EditorAction("file.save", "Save", "File", () => { if (Animation.AnimationWorkspacePart.Active) Animation.AnimationWorkspacePart.Studio!.SaveDocument(); else Save(); }) { Shortcut = "Ctrl+S", Order = 200 });
        _ctx.AddAction(new EditorAction("file.saveAs", "Save As…", "File", () => { if (Animation.AnimationWorkspacePart.Active) Animation.AnimationWorkspacePart.Studio!.SaveDocument(true); else SaveAs(); }) { Shortcut = "Ctrl+Shift+S", Order = 210 });
        _ctx.AddAction(new EditorAction("file.autosaveNow", "Autosave Now", "File", AutosaveNow) { Order = 220 });
        _ctx.AddAction(new EditorAction("file.openAutosaves", "Show Autosave Folder", "File", () => ExternalPrograms.OpenFolder(AutosaveFolder))
        {
            Order = 230,
        });
        _ctx.AddAction(new EditorAction("file.openLatestAutosave", "Open Latest Autosave", "File", () =>
        {
            if (LatestAutosave() is { } copy)
            {
                GuardUnsaved(() => OpenAutosave(copy.Folder));
            }
        })
        {
            Order = 240,
            IsEnabled = () => LatestAutosave() is not null,
            Description = "Open this map's newest autosave, unsaved (only one newer than the map's last save)",
        });

        string[] importFilters = ["*.t3d ; Unreal T3D", "*.sdc ; SCCT map", "*.json ; JSON"];
        _ctx.AddAction(new EditorAction("file.import", "Import File…", "File", () =>
            GuardUnsaved(() => Dialogs.OpenFile("Import map", importFilters, s_importChoices,
                (path, picked) => Import(path, BrushRecoveryImport.ParseMode(picked.GetValueOrDefault("brushes"))), LastFolder())))
        {
            Shortcut = "Ctrl+I",
            Order = 300,
            IsEnabled = () => EditorServices.Importers.Count > 0 && !BackgroundJobs.IsRunning("Import"),
            Description = "Import a T3D export or .sdc map as a new project",
        });
        _ctx.AddAction(new EditorAction("file.importFolder", "Import SVMap Folder…", "File", () =>
            GuardUnsaved(() => Dialogs.OpenFolder("Import SVMap folder", Import, EditorServices.ContentRoot is { } c ? Path.Combine(c, "svmap") : LastFolder())))
        {
            Order = 310,
            IsEnabled = () => EditorServices.Importers.Count > 0 && !BackgroundJobs.IsRunning("Import"),
            Description = "Import a converted SVMap folder (the world comes in as a locked baked layer)",
        });
        int order = 320;
        foreach (IMapExporter exporter in EditorServices.Exporters)
        {
            IMapExporter e = exporter;
            string label = e.WritesFolder ? "Export for the Original Editor…" : $"Export {e.Name.ToUpperInvariant()}…";
            _ctx.AddAction(new EditorAction("file.export." + e.Name.ToLowerInvariant(), label, "File", () => Export(e))
            {
                Order = order++,
                IsEnabled = () => !BackgroundJobs.IsRunning("Export"),
                Description = e.WritesFolder
                    ? "A folder for RE+: the T3D, the map's own assets and classes, its texts and the steps to make it there"
                    : e.Target == TargetGame.Original ? "For the original editor" : null,
            });
        }

        _ctx.AddAction(new EditorAction("file.quit", "Quit", "File", Quit) { Shortcut = "Ctrl+Q", Order = 900 });
    }

    /// <summary>Lists the open project's save backups under File > Restore Backup, newest first.</summary>
    private void RefreshBackups()
    {
        foreach (string id in _ctx.Actions.Select(a => a.Id).Where(id => id.StartsWith(BackupPrefix, StringComparison.Ordinal)).ToList())
        {
            _ctx.RemoveAction(id);
        }

        if (_ctx.Session.Folder is not { } folder)
        {
            return;
        }

        IReadOnlyList<ProjectBackup> backups = ProjectBackups.List(folder);
        for (int i = 0; i < backups.Count; i++)
        {
            ProjectBackup b = backups[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction(BackupPrefix + n, $"{n}. Saved before {b.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", "File/Restore Backup",
                () => GuardUnsaved(() => OpenBackup(folder, b)))
            {
                Order = 250 + i,
                Description = "Open this earlier version unsaved; saving puts it back (the current files become a backup)",
            });
        }
    }

    private void OpenBackup(string folder, ProjectBackup backup)
    {
        try
        {
            _ctx.ReplaceSession(EditorSession.OpenBackup(folder, backup.Folder));
            _ctx.Log($"Opened the backup {backup.Name} of {folder}; save to put it back");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException
            or OpenScct.Content.SvMapFormatException)
        {
            _ctx.Log($"Could not open the backup {backup.Name}: {ex.Message}", Severity.Error);
        }
    }

    private void RefreshRecent()
    {
        foreach (string id in _ctx.Actions.Select(a => a.Id).Where(id => id.StartsWith(RecentPrefix, StringComparison.Ordinal) && id != "file.recent.clear").ToList())
        {
            _ctx.RemoveAction(id);
        }

        IReadOnlyList<string> recent = AppSettings.Recent;
        for (int i = 0; i < recent.Count; i++)
        {
            string path = recent[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction(RecentPrefix + n, $"{n}. {Path.GetFileName(path)}  ({Path.GetDirectoryName(path)})", "File/Open Recent",
                () => GuardUnsaved(() => OpenPath(path)))
            { Order = i });
        }
    }

    private static string? LastFolder() =>
        AppSettings.Recent.Count > 0 ? Path.GetDirectoryName(AppSettings.Recent[0]) : null;

    private static void Progress(string line) => BackgroundJobs.Post(() => EditorContext.Instance.SetStatus(line));

    /// <summary>A project to open from the command line: <c>-- path.osmap</c> or <c>-- --open=path</c>.</summary>
    private static string? StartPath()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--open=", StringComparison.Ordinal))
            {
                return arg["--open=".Length..];
            }

            if (!arg.StartsWith('-'))
            {
                return arg;
            }
        }

        return null;
    }
}
