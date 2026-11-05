using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Session;

/// <summary>The current selection: an ordered set of object references (the last one is the "primary" one). A hash set
/// alongside the list keeps membership tests constant time, so selecting thousands of objects stays fast.</summary>
public sealed class Selection
{
    private readonly List<ObjectRef> _items = new();
    private readonly HashSet<ObjectRef> _set = new();

    public event Action? Changed;

    public IReadOnlyList<ObjectRef> Items => _items;

    public int Count => _items.Count;

    public ObjectRef? Primary => _items.Count > 0 ? _items[^1] : null;

    public bool Contains(ObjectRef r) => _set.Contains(r);

    public void Set(IEnumerable<ObjectRef> refs)
    {
        _items.Clear();
        _set.Clear();
        foreach (ObjectRef r in refs)
        {
            if (_set.Add(r))
            {
                _items.Add(r);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Adds objects; ones already selected move to the end (the last becomes primary).</summary>
    public void Add(IEnumerable<ObjectRef> refs)
    {
        var added = new List<ObjectRef>();
        var seen = new HashSet<ObjectRef>();
        foreach (ObjectRef r in refs)
        {
            if (seen.Add(r))
            {
                added.Add(r);
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        if (seen.Overlaps(_set))
        {
            _items.RemoveAll(seen.Contains);
        }

        _items.AddRange(added);
        _set.UnionWith(added);
        Changed?.Invoke();
    }

    public void Toggle(ObjectRef r)
    {
        if (_set.Remove(r))
        {
            _items.Remove(r);
        }
        else
        {
            _set.Add(r);
            _items.Add(r);
        }

        Changed?.Invoke();
    }

    public void Remove(IEnumerable<ObjectRef> refs)
    {
        var gone = new HashSet<ObjectRef>(refs);
        gone.IntersectWith(_set);
        if (gone.Count == 0)
        {
            return;
        }

        _items.RemoveAll(gone.Contains);
        _set.ExceptWith(gone);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        _set.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops references to objects that no longer exist (after undo or delete).</summary>
    public void Prune(MapProject project)
    {
        if (_items.Count == 0)
        {
            return;
        }

        // Many selected objects: one pass over the map beats a lookup that has to prove each missing one is gone.
        Func<ObjectRef, bool> exists = _items.Count <= 64
            ? project.Exists
            : project.AllRefs().ToHashSet().Contains;
        var gone = _items.Where(r => !exists(r)).ToHashSet();
        if (gone.Count > 0)
        {
            _items.RemoveAll(gone.Contains);
            _set.ExceptWith(gone);
            Changed?.Invoke();
        }
    }
}

/// <summary>
/// One open map: the project, where it lives, its undo stack and selection. The UI, the CLI and the API server all
/// work through a session, so an edit made by any of them is the same undoable command.
/// </summary>
public sealed class EditorSession
{
    public EditorSession(MapProject project, string? folder = null)
    {
        Project = project;
        Folder = folder;
        Commands = new CommandStack(project);
        Commands.Changed += c =>
        {
            Selection.Prune(Project);
            ProjectChanged?.Invoke(c);
        };
    }

    public MapProject Project { get; }

    /// <summary>The .osmap folder, or null for an unsaved map.</summary>
    public string? Folder { get; private set; }

    public CommandStack Commands { get; }

    public Selection Selection { get; } = new();

    /// <summary>Raised after any edit (do, undo, redo) with the command.</summary>
    public event Action<IEditCommand>? ProjectChanged;

    /// <summary>Raised after a save.</summary>
    public event Action? Saved;

    public bool IsDirty => Commands.IsDirty;

    public void Execute(IEditCommand command) => Commands.Execute(command);

    public static EditorSession Open(string folder) => new(ProjectStore.Load(folder), Path.GetFullPath(folder));

    public static EditorSession New(string id, string title) => new(MapProject.CreateDefault(id, title));

    /// <summary>Opens an autosave copy as the map it was taken from: the session saves to the original project folder
    /// (when the copy names one that still exists) and starts out unsaved.</summary>
    public static EditorSession OpenAutosave(string autosaveFolder)
    {
        AutosaveInfo? info = Autosaves.ReadInfo(autosaveFolder);
        string? original = info?.ProjectFolder is { } f && ProjectStore.IsProject(f) ? f : null;
        var session = new EditorSession(ProjectStore.Load(autosaveFolder), original);
        session.Commands.MarkUnsaved();
        return session;
    }

    /// <summary>Opens one of a project's save backups as that project, unsaved: saving puts it back (its assets and
    /// prefabs too), and the files it replaces become the newest backup.</summary>
    public static EditorSession OpenBackup(string projectFolder, string backupFolder)
    {
        var session = new EditorSession(ProjectStore.Load(backupFolder), Path.GetFullPath(projectFolder));
        session._restoreFrom = Path.GetFullPath(backupFolder);
        session.Commands.MarkUnsaved();
        return session;
    }

    /// <summary>The backup this session was opened from, until a save into its project puts its folders back.</summary>
    private string? _restoreFrom;

    /// <summary>True while the session is a backup opened over its project: the next save into the project puts the
    /// backup's assets and prefabs back too.</summary>
    public bool RestoresBackup => _restoreFrom is not null;

    /// <summary>What went wrong with the backups at the last save, without stopping it (<see cref="ProjectStore.Save"/>).</summary>
    public IReadOnlyList<string> SaveWarnings { get; private set; } = [];

    /// <summary>Saves the project, keeping as many backups as the user chose (<see cref="ProjectBackups.Kept"/>).</summary>
    public void Save(string? folder = null)
    {
        folder ??= Folder ?? throw new InvalidOperationException("The map has no folder yet; give one.");
        if (Folder is null)
        {
            // First save of an imported map: its pictures (and the assets an export brought back) move into the project.
            Identity.ProjectPictures.Adopt(Project, folder);
            Interop.Original.HandoffFile.AdoptAssets(Project, folder);
        }

        // Outfit files from outside the map (an SVMap import, files picked before it had a folder) come in with it.
        Project.Outfits = Gameplay.ProjectOutfits.Localize(Project.Outfits, folder);
        // Saving an opened backup into its own project puts the backup's assets and prefabs back too.
        bool restoring = Folder is not null && string.Equals(Path.GetFullPath(folder), Folder, StringComparison.OrdinalIgnoreCase);
        SaveWarnings = ProjectStore.Save(Project, folder, ProjectBackups.Kept, restoring ? _restoreFrom : null);
        _restoreFrom = null;
        CopyPrefabs(Folder, folder);
        CopyPropertySets(Folder, folder);
        if (Folder is not null)
        {
            // Save As: the map-local meshes, materials and pictures go with the map.
            Assets.ProjectAssets.CopyFolder(Folder, folder);
            Identity.ProjectPictures.CopyFolder(Folder, folder);
        }

        Folder = Path.GetFullPath(folder);
        Commands.MarkSaved();
        Saved?.Invoke();
    }

    /// <summary>Save As takes the map's own prefabs along (its instances need them).</summary>
    private static void CopyPrefabs(string? from, string to)
    {
        if (from is null || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string source = Path.Combine(from, ProjectStore.PrefabsFolder);
        if (!Directory.Exists(source))
        {
            return;
        }

        string target = Path.Combine(to, ProjectStore.PrefabsFolder);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*.json"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>Save As takes the map's own property sets along (they are kept beside the map, outside undo).</summary>
    private static void CopyPropertySets(string? from, string to)
    {
        if (PropertySets.MapFile(from) is { } source && File.Exists(source)
            && !string.Equals(Path.GetFullPath(from!), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(to);
            File.Copy(source, PropertySets.MapFile(to)!, overwrite: true);
        }
    }

    /// <summary>Writes an autosave copy in <paramref name="autosaveRoot"/>, rotating <paramref name="keep"/> slots per
    /// map, with a note of the project it came from (<see cref="Autosaves"/>); returns the folder written.</summary>
    public string Autosave(string autosaveRoot, int keep = 10)
    {
        Directory.CreateDirectory(autosaveRoot);
        string stem = Project.Id;
        string[] existing = Directory.GetDirectories(autosaveRoot, stem + ".auto*" + ProjectStore.Extension);
        string? oldest = existing.OrderBy(Directory.GetLastWriteTimeUtc).FirstOrDefault();
        // The first free slot, else the oldest copy.
        string target = Enumerable.Range(0, Math.Max(keep, 1))
            .Select(i => Path.Combine(autosaveRoot, $"{stem}.auto{i}{ProjectStore.Extension}"))
            .FirstOrDefault(slot => !Directory.Exists(slot)) ?? oldest!;
        ProjectStore.Save(Project, target);
        // The map's own prefabs, imported assets and pictures, so the copy opens like the map.
        CopyPrefabs(Folder, target);
        if (Folder is not null)
        {
            Assets.ProjectAssets.CopyFolder(Folder, target);
            Identity.ProjectPictures.CopyFolder(Folder, target);
        }

        Autosaves.WriteInfo(target, new AutosaveInfo(Folder, Project.Id, Project.Title, DateTime.UtcNow));
        Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        return target;
    }
}
