using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Commands;

/// <summary>An undoable edit of the layer list (create, rename, delete, show/hide, lock, colour). The objects on a
/// layer are not touched; <see cref="LayerEdits"/> combines both.</summary>
public sealed class LayerListCommand : IEditCommand
{
    private readonly Action<List<Layer>> _edit;
    private List<Layer>? _before;
    private List<Layer>? _after;

    public LayerListCommand(string name, Action<List<Layer>> edit)
    {
        Name = name;
        _edit = edit;
    }

    public string Name { get; }

    public void Do(MapProject project)
    {
        if (_after is null)
        {
            _before = project.Layers.Select(l => l.Clone()).ToList();
            var edited = project.Layers.Select(l => l.Clone()).ToList();
            _edit(edited);
            _after = edited;
        }

        project.Layers = _after.Select(l => l.Clone()).ToList();
    }

    public void Undo(MapProject project)
    {
        if (_before is not null)
        {
            project.Layers = _before.Select(l => l.Clone()).ToList();
        }
    }
}

/// <summary>An undoable edit of the editor-only workspace: the edit runs once on a copy, and do and undo swap whole
/// copies in, so it is exact and cheap (a slider drag runs one per step). Edits with the same <see cref="MergeKey"/> in
/// a row are one undo step. The storey the views show is left as it is unless the edit itself picked one
/// (<see cref="ShownStorey"/>).</summary>
public sealed class WorkspaceCommand : IEditCommand
{
    private readonly Action<Workspace> _edit;
    private Workspace? _before;
    private Workspace? _after;

    public WorkspaceCommand(string name, Action<Workspace> edit, string? mergeKey = null)
    {
        Name = name;
        _edit = edit;
        MergeKey = mergeKey;
    }

    public string Name { get; }

    public string? MergeKey { get; }

    /// <summary>Whether this edit deliberately moved the view, rather than merely saving the camera
    /// alongside unrelated workspace settings. Available after its first Do.</summary>
    public bool ChangesCamera => _before is not null && _after is not null &&
        (_before.CameraLocation != _after.CameraLocation || _before.CameraRotation != _after.CameraRotation);

    public void Do(MapProject project)
    {
        if (_after is null)
        {
            _before = project.Workspace.Clone();
            Workspace edited = project.Workspace.Clone();
            _edit(edited);
            _after = edited;
        }

        SwapIn(project, _after);
    }

    public void Undo(MapProject project)
    {
        if (_before is not null)
        {
            SwapIn(project, _before);
        }
    }

    private void SwapIn(MapProject project, Workspace state)
    {
        double? shown = project.Workspace.ShownStorey;
        FirstMapGuide guide = project.Workspace.FirstMap.Clone();
        project.Workspace = state.Clone();
        project.Workspace.FirstMap = guide;
        ShownStorey.Keep(project, shown, _before!.ShownStorey, _after!.ShownStorey);
    }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not WorkspaceCommand n || MergeKey is null || n.MergeKey != MergeKey)
        {
            return false;
        }

        _after = n._after;
        return true;
    }
}

/// <summary>
/// The storey the views show is a view choice, like the camera: picking one in the views is not an undo step, so the
/// undo and redo of an edit that did not pick one must not move it back to where it was when that edit ran.
/// </summary>
internal static class ShownStorey
{
    /// <summary>After a command put a whole saved state back: keeps the storey shown before (<paramref name="live"/>)
    /// unless the command changed it.</summary>
    public static void Keep(MapProject project, double? live, double? before, double? after)
    {
        if (before == after)
        {
            project.Workspace.ShownStorey = live;
        }
    }
}

/// <summary>Commands for the editor-only workspace (grid, bookmarks, references, play options, movement limits). Each
/// is one exact undo step and marks the map changed.</summary>
public static class WorkspaceEdits
{
    /// <summary>Any workspace edit. Edits with the same <paramref name="mergeKey"/> in a row are one undo step.</summary>
    public static IEditCommand Edit(string name, Action<Workspace> edit, string? mergeKey = null) => new WorkspaceCommand(name, edit, mergeKey);

    public static IEditCommand Grid(double size) =>
        Edit(FormattableString.Invariant($"Grid {size:0.###}"), w => w.GridSize = Math.Clamp(size, 1, 4096), "workspace.grid");

    public static IEditCommand RotationGrid(int units) =>
        Edit($"Rotation grid {units}", w => w.RotationGrid = Math.Clamp(units, 1, 32768), "workspace.rotationGrid");

    public static IEditCommand Bookmarks(string name, Action<List<Bookmark>> edit) => Edit(name, w => edit(w.Bookmarks));

    public static IEditCommand References(string name, Action<List<ReferenceImage>> edit) => Edit(name, w => edit(w.References));

    public static IEditCommand Play(string name, Action<PlayOptions> edit) => Edit(name, w => edit(w.Play), "workspace.play:" + name);

    public static IEditCommand Movement(string name, Action<MovementLimits> edit) => Edit(name, w => edit(w.Movement), "workspace.movement:" + name);

    /// <summary>Stores a bookmark under <paramref name="name"/> (replacing one of that name and keeping its note), or
    /// adds it.</summary>
    public static IEditCommand StoreBookmark(string name, Vec3 location, Rot rotation, string? note = null) =>
        Bookmarks($"Bookmark {name}", list =>
        {
            int i = list.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            var bm = new Bookmark { Name = name, Location = location, Rotation = rotation, Note = note ?? (i >= 0 ? list[i].Note : null) };
            if (i >= 0)
            {
                list[i] = bm;
            }
            else
            {
                list.Add(bm);
            }
        });
}

/// <summary>Layer commands that keep the layer list and the objects on it in step.</summary>
public static class LayerEdits
{
    /// <summary>The name objects store for a layer ("" for Default).</summary>
    public static string Stored(string name) => IsDefault(name) ? "" : name;

    public static bool IsDefault(string name) => name.Length == 0 || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static IEditCommand Layers(string name, Action<List<Layer>> edit) => new LayerListCommand(name, edit);

    /// <summary>Every object that stores this layer name (objects on the default layer store "").</summary>
    public static List<ObjectRef> Members(MapProject p, string stored)
    {
        var refs = new List<ObjectRef>();
        refs.AddRange(p.Brushes.Where(b => Same(b.Editor.Layer, stored)).Select(b => ObjectRef.ForBrush(b)));
        refs.AddRange(p.Actors.Where(a => Same(a.Editor.Layer, stored)).Select(a => ObjectRef.Actor(a.Id)));
        refs.AddRange(p.Ges.Where(g => Same(g.Editor.Layer, stored)).Select(g => ObjectRef.Ge(g.Id)));
        return refs;
    }

    /// <summary>The listed layer of that name, or null.</summary>
    public static Layer? Find(MapProject p, string name) => p.Layers.Find(l => Same(l.Name, name));

    /// <summary>Flips a layer's visibility or lock. A layer the objects name but the list lacks is added first.</summary>
    public static IEditCommand Toggle(string name, bool visibility) =>
        Layers(visibility ? $"Toggle visibility of layer {name}" : $"Toggle lock of layer {name}", list =>
        {
            Layer layer = Ensure(list, name);
            if (visibility)
            {
                layer.Visible = !layer.Visible;
            }
            else
            {
                layer.Locked = !layer.Locked;
            }
        });

    /// <summary>Renames a layer and moves the objects that name it with it, as one step. Objects on the default layer
    /// by the empty name stay on the default layer.</summary>
    public static IEditCommand Rename(MapProject p, string oldName, string newName)
    {
        List<ObjectRef> members = Members(p, oldName);
        var parts = new List<IEditCommand>
        {
            Layers("Rename layer", list => list.ForEach(l =>
            {
                if (Same(l.Name, oldName))
                {
                    l.Name = newName;
                }
            })),
        };
        if (members.Count > 0)
        {
            parts.Add(Edits.SetMeta(p, members, "Rename layer", m => m.Layer = newName));
        }

        return new CompositeCommand($"Rename layer {oldName} to {newName}", parts);
    }

    /// <summary>Deletes a layer; its objects move to the default layer.</summary>
    public static IEditCommand Delete(MapProject p, string name)
    {
        List<ObjectRef> members = Members(p, name);
        var parts = new List<IEditCommand>();
        if (members.Count > 0)
        {
            parts.Add(Edits.SetMeta(p, members, "Move to default layer", m => m.Layer = ""));
        }

        parts.Add(Layers("Delete layer", list => list.RemoveAll(l => Same(l.Name, name))));
        return new CompositeCommand($"Delete layer {name}", parts);
    }

    /// <summary>Puts objects on a layer, adding the layer to the list when it is missing.</summary>
    public static IEditCommand MoveTo(MapProject p, IReadOnlyList<ObjectRef> refs, string name)
    {
        var parts = new List<IEditCommand>();
        if (!IsDefault(name) && Find(p, name) is null)
        {
            parts.Add(Layers($"Add layer {name}", list => list.Add(new Layer(name))));
        }

        string stored = Stored(name);
        parts.Add(Edits.SetMeta(p, refs, $"Move to layer {name}", m => m.Layer = stored));
        return new CompositeCommand($"Move {refs.Count} object{(refs.Count == 1 ? "" : "s")} to layer {name}", parts);
    }

    /// <summary>A layer name no layer uses yet: "Layer 1", "Layer 2"...</summary>
    public static string NewName(MapProject p, string stem = "Layer")
    {
        var used = new HashSet<string>(p.Layers.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        used.UnionWith(p.Brushes.Select(b => b.Editor.Layer).Concat(p.Actors.Select(a => a.Editor.Layer)).Concat(p.Ges.Select(g => g.Editor.Layer)));
        for (int i = 1; ; i++)
        {
            string name = $"{stem} {i}";
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }

    private static Layer Ensure(List<Layer> list, string name)
    {
        Layer? layer = list.Find(l => Same(l.Name, name));
        if (layer is null)
        {
            layer = new Layer(IsDefault(name) ? "Default" : name);
            list.Add(layer);
        }

        return layer;
    }
}
