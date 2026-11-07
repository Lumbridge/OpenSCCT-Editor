using Godot;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScctEditor.Core;

/// <summary>
/// The app's view of the open map. Panels, viewports and tools talk to each other only through this: the session
/// (project, undo stack, selection), editor actions by id, the log, and the status line. Edits always go through
/// <see cref="Execute"/>, so they are undoable and every view hears about them.
/// </summary>
public sealed class EditorContext
{
    private readonly Dictionary<string, EditorAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private EditorSession? _hooked;

    public EditorContext() => Session = EditorSession.New("NewMD", "New map");

    public static EditorContext Instance { get; } = new();

    public EditorSession Session { get; private set; }

    public MapProject Project => Session.Project;

    public Selection Selection => Session.Selection;

    /// <summary>Raised when another map is opened (panels rebind to the new session).</summary>
    public event Action? SessionReplaced;

    /// <summary>Raised after any edit, undo or redo.</summary>
    public event Action<IEditCommand>? ProjectChanged;

    /// <summary>Raised when the selection changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after the open map was saved (its folder may have changed).</summary>
    public event Action? Saved;
    public event Action? GuideChanged;

    public void UpdateGuide(Action<FirstMapGuide> edit)
    {
        edit(Project.Workspace.FirstMap);
        Session.Commands.MarkUnsaved();
        GuideChanged?.Invoke();
    }

    /// <summary>Raised for each log line (text, severity).</summary>
    public event Action<string, Severity>? Logged;

    /// <summary>Raised when the status text changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when a view should frame these objects (outliner double-click, Map Check "go to").</summary>
    public event Action<IReadOnlyList<ObjectRef>>? FocusRequested;

    /// <summary>Raised when the viewports should jump to a location.</summary>
    public event Action<Vec3>? JumpRequested;

    /// <summary>Raised when the viewports should move the camera to a saved view (bookmarks).</summary>
    public event Action<Vec3, Rot>? ViewRequested;

    /// <summary>Where newly placed objects go (the point under the view centre). The viewport sets this; when it is
    /// null, placement falls back to the workspace camera.</summary>
    public Func<Vec3>? PlacementPoint { get; set; }

    /// <summary>The findings of the last check or build (the Map Check panel shows them).</summary>
    public IReadOnlyList<Finding> Findings { get; private set; } = Array.Empty<Finding>();

    /// <summary>True once a check, build or import has given findings for the open map (none found is then a result,
    /// not just nothing run yet).</summary>
    public bool FindingsChecked { get; private set; }

    public event Action? FindingsChanged;

    /// <summary>The report of the import that made the open map (the Import Report panel shows it); null for a map
    /// opened from disk or made here.</summary>
    public ImportReport? ImportReport { get; private set; }

    public IReadOnlyCollection<EditorAction> Actions => _actions.Values;

    /// <summary>Makes <paramref name="session"/> the open map, with the report of the import that made it, if any. The
    /// old session's events are unhooked, so it and everything it holds can be collected.</summary>
    public void ReplaceSession(EditorSession session, ImportReport? importReport = null)
    {
        if (_hooked is { } old)
        {
            old.ProjectChanged -= OnProjectChanged;
            old.Selection.Changed -= OnSelectionChanged;
            old.Saved -= OnSaved;
        }

        Session = session;
        _hooked = session;
        ImportReport = importReport;
        // Findings name objects of the map they came from.
        Findings = Array.Empty<Finding>();
        session.ProjectChanged += OnProjectChanged;
        session.Selection.Changed += OnSelectionChanged;
        session.Saved += OnSaved;
        // Findings name objects of the old map.
        Findings = Array.Empty<Finding>();
        FindingsChecked = false;
        FindingsChanged?.Invoke();
        SessionReplaced?.Invoke();
        SelectionChanged?.Invoke();
        foreach (string note in session.Project.LoadNotes)
        {
            Log(note);
        }
    }

    /// <summary>Hooks the first session's events (call once at startup).</summary>
    public void Init() => ReplaceSession(Session);

    private void OnProjectChanged(IEditCommand command) => ProjectChanged?.Invoke(command);

    private void OnSelectionChanged() => SelectionChanged?.Invoke();

    private void OnSaved() => Saved?.Invoke();

    public void Execute(IEditCommand command)
    {
        try
        {
            Session.Execute(command);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Log($"{command.Name} failed: {ex.Message}", Severity.Error);
        }
    }

    public void Log(string text, Severity severity = Severity.Info)
    {
        GD.Print(text);
        Logged?.Invoke(text, severity);
    }

    public void SetStatus(string text) => StatusChanged?.Invoke(text);

    public void Focus(IReadOnlyList<ObjectRef> objects) => FocusRequested?.Invoke(objects);

    public void JumpTo(Vec3 location) => JumpRequested?.Invoke(location);

    /// <summary>Moves the camera to a saved view; views that only handle jumps get the location.</summary>
    public void ViewFrom(Vec3 location, Rot rotation)
    {
        if (ViewRequested is { } handler)
        {
            handler(location, rotation);
        }
        else
        {
            JumpTo(location);
        }
    }

    public void SetFindings(IReadOnlyList<Finding> findings)
    {
        Findings = findings;
        FindingsChecked = true;
        _findingsVersion = AnalysisCache.Of(Session).Version;
        FindingsChanged?.Invoke();
    }

    /// <summary>Drops one finding (fixed or dismissed); the rest stay as old as they were.</summary>
    public void RemoveFinding(Finding finding)
    {
        Findings = [.. Findings.Where(f => !ReferenceEquals(f, finding))];
        FindingsChanged?.Invoke();
    }

    /// <summary>True when the map changed after the findings on show were made (the views fade their shapes).</summary>
    public bool FindingsStale => AnalysisCache.Of(Session).Version != _findingsVersion;

    private int _findingsVersion;

    /// <summary>Registers an action (menus, shortcuts and the command palette all list registered actions).</summary>
    public void AddAction(EditorAction action) => _actions[action.Id] = action;

    /// <summary>Removes an action (dynamic entries such as the recent-files list).</summary>
    public bool RemoveAction(string id) => _actions.Remove(id);

    public EditorAction? FindAction(string id) => _actions.GetValueOrDefault(id);

    public bool Run(string actionId)
    {
        if (_actions.TryGetValue(actionId, out EditorAction? a) && a.IsEnabled())
        {
            a.Run();
            return true;
        }

        return false;
    }
}

/// <summary>A named editor command: menus, the command palette and shortcuts all run these.</summary>
public sealed class EditorAction
{
    public EditorAction(string id, string title, string menu, Action run)
    {
        Id = id;
        Title = title;
        Menu = menu;
        Run = run;
    }

    /// <summary>Stable id, e.g. <c>edit.undo</c>, <c>build.play</c>.</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Menu path, e.g. <c>Edit</c> or <c>Brush/CSG</c>.</summary>
    public string Menu { get; }

    public Action Run { get; }

    /// <summary>Shortcut text as Godot parses it in <c>OS.FindKeycodeFromString</c> plus modifiers, e.g. "Ctrl+Z".</summary>
    public string? Shortcut { get; init; }

    public Func<bool> IsEnabled { get; init; } = () => true;

    /// <summary>For toggles (grid snap, render mode...): whether the action is on now; null for plain commands.</summary>
    public Func<bool>? IsChecked { get; init; }

    /// <summary>Short help shown as a tooltip and in the command palette.</summary>
    public string? Description { get; init; }

    /// <summary>Order within its menu. Menus put a separator between hundreds (docs/UI-DESIGN.md lists the bands).</summary>
    public int Order { get; init; }

    /// <summary>For one of a set of exclusive choices (tools, render mode, layout): the set's name, so menus show it
    /// as a radio item; null for a plain command or an independent toggle.</summary>
    public string? RadioGroup { get; init; }
}
