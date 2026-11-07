using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Commands;

/// <summary>Adds objects (at the end of their list, or at given indexes).</summary>
public sealed class AddObjectsCommand : IEditCommand
{
    private readonly List<Brush> _brushes;
    private readonly List<Actor> _actors;
    private readonly List<Ge> _ges;

    public AddObjectsCommand(string name, IEnumerable<Brush>? brushes = null, IEnumerable<Actor>? actors = null, IEnumerable<Ge>? ges = null)
    {
        Name = name;
        _brushes = brushes?.ToList() ?? new();
        _actors = actors?.ToList() ?? new();
        _ges = ges?.ToList() ?? new();
    }

    public string Name { get; }

    public IEnumerable<ObjectRef> Added =>
        _brushes.Select(b => ObjectRef.ForBrush(b)).Concat(_actors.Select(a => ObjectRef.Actor(a.Id))).Concat(_ges.Select(g => ObjectRef.Ge(g.Id)));

    public void Do(MapProject project)
    {
        project.Brushes.AddRange(_brushes);
        project.Actors.AddRange(_actors);
        project.Ges.AddRange(_ges);
    }

    /// <summary>Removes the added objects by id: a later edit that was undone puts back a copy, not the same instance.</summary>
    public void Undo(MapProject project)
    {
        RemoveById(project.Brushes, _brushes, b => b.Id);
        RemoveById(project.Actors, _actors, a => a.Id);
        RemoveById(project.Ges, _ges, g => g.Id);
    }

    private static void RemoveById<T>(List<T> list, List<T> added, Func<T, string> id)
    {
        var ids = new HashSet<string>(added.Select(id), StringComparer.OrdinalIgnoreCase);
        list.RemoveAll(x => ids.Contains(id(x)));
    }
}

/// <summary>Deletes objects, remembering their places so undo puts them back in order (CSG order matters).</summary>
public sealed class DeleteObjectsCommand : IEditCommand
{
    private readonly List<ObjectRef> _refs;
    private readonly List<(int Index, Brush Item)> _brushes = new();
    private readonly List<(int Index, Actor Item)> _actors = new();
    private readonly List<(int Index, Ge Item)> _ges = new();

    public DeleteObjectsCommand(IEnumerable<ObjectRef> refs)
    {
        _refs = refs.Select(r => r.Kind == ObjectKind.Stair ? ObjectRef.Brush(r.Id) : r).Distinct().ToList();
        Name = _refs.Count == 1 ? $"Delete {_refs[0].Id}" : $"Delete {_refs.Count} objects";
    }

    public string Name { get; }

    public void Do(MapProject project)
    {
        _brushes.Clear();
        _actors.Clear();
        _ges.Clear();
        Take(project.Brushes, ObjectKind.Brush, b => b.Id, _brushes);
        Take(project.Actors, ObjectKind.Actor, a => a.Id, _actors);
        Take(project.Ges, ObjectKind.Ge, g => g.Id, _ges);
    }

    public void Undo(MapProject project)
    {
        foreach ((int index, Brush item) in _brushes.OrderBy(p => p.Index))
        {
            project.Brushes.Insert(Math.Min(index, project.Brushes.Count), item);
        }

        foreach ((int index, Actor item) in _actors.OrderBy(p => p.Index))
        {
            project.Actors.Insert(Math.Min(index, project.Actors.Count), item);
        }

        foreach ((int index, Ge item) in _ges.OrderBy(p => p.Index))
        {
            project.Ges.Insert(Math.Min(index, project.Ges.Count), item);
        }
    }

    private void Take<T>(List<T> list, ObjectKind kind, Func<T, string> id, List<(int, T)> taken)
    {
        var ids = new HashSet<string>(_refs.Where(r => r.Kind == kind).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
        {
            if (ids.Contains(id(list[i])))
            {
                taken.Add((i, list[i]));
            }
        }

        for (int i = taken.Count - 1; i >= 0; i--)
        {
            list.RemoveAt(taken[i].Item1);
        }
    }
}

/// <summary>A before/after snapshot edit of whole objects: transforms, shapes, flags, props, meta. Simple and
/// always correct; used by the gizmo, the inspector and most tools. Merges with the next one over the same objects
/// when <see cref="MergeKey"/> matches (a drag is one undo step).</summary>
public sealed class ReplaceObjectsCommand : IEditCommand
{
    private readonly List<(Brush Before, Brush After)> _brushes = new();
    private readonly List<(Actor Before, Actor After)> _actors = new();
    private readonly List<(Ge Before, Ge After)> _ges = new();

    public ReplaceObjectsCommand(string name, string? mergeKey = null)
    {
        Name = name;
        MergeKey = mergeKey;
    }

    public string Name { get; }

    public string? MergeKey { get; }

    public bool IsEmpty => _brushes.Count == 0 && _actors.Count == 0 && _ges.Count == 0;

    /// <summary>The objects the command changes.</summary>
    public IEnumerable<ObjectRef> Changed =>
        _brushes.Select(x => ObjectRef.ForBrush(x.Before))
            .Concat(_actors.Select(x => ObjectRef.Actor(x.Before.Id)))
            .Concat(_ges.Select(x => ObjectRef.Ge(x.Before.Id)));

    /// <summary>Records the change of a brush. <paramref name="after"/> must not be the live object.</summary>
    public ReplaceObjectsCommand Brush(Brush before, Brush after)
    {
        if (before.Stair is not null && after.Stair is not null && before.Stair.ToJson() == after.Stair.ToJson()
            && Compile.CsgBrush.FingerprintOf(new Brush("shape") { Shape = before.Shape }) != Compile.CsgBrush.FingerprintOf(new Brush("shape") { Shape = after.Shape }))
            throw new ArgumentException("Edit the staircase properties or use Break Apart before changing individual generated faces");
        _brushes.Add((before, after));
        return this;
    }

    public ReplaceObjectsCommand Actor(Actor before, Actor after)
    {
        _actors.Add((before, after));
        return this;
    }

    public ReplaceObjectsCommand Ge(Ge before, Ge after)
    {
        _ges.Add((before, after));
        return this;
    }

    /// <summary>Builds the command from the live objects of <paramref name="refs"/> and an edit applied to copies. A
    /// mover the edit moves or turns takes its base along (<see cref="MoverBase.Follow"/>), unless
    /// <paramref name="followBase"/> is false (the edit sets the base itself).</summary>
    public static ReplaceObjectsCommand Edit(MapProject project, IEnumerable<ObjectRef> refs, string name,
        Action<Brush>? brush = null, Action<Actor>? actor = null, Action<Ge>? ge = null, string? mergeKey = null,
        bool followBase = true)
    {
        var cmd = new ReplaceObjectsCommand(name, mergeKey);
        foreach (ObjectRef r in refs.Distinct())
        {
            switch (r.Kind)
            {
                case ObjectKind.Brush or ObjectKind.Stair when brush is not null && project.FindBrush(r.Id) is { } b:
                    Brush nb = b.Clone();
                    brush(nb);
                    if (b.Stair is not null && nb.MainScale != b.MainScale)
                    {
                        Stairs.StairTransforms.Resize(b, nb);
                    }
                    cmd.Brush(b.Clone(), nb);
                    break;
                case ObjectKind.Actor when actor is not null && project.FindActor(r.Id) is { } a:
                    Actor na = a.Clone();
                    actor(na);
                    if (followBase)
                    {
                        MoverBase.Follow(a, na);
                    }

                    cmd.Actor(a.Clone(), na);
                    break;
                case ObjectKind.Ge when ge is not null && project.FindGe(r.Id) is { } g:
                    Ge ng = g.Clone();
                    ge(ng);
                    cmd.Ge(g.Clone(), ng);
                    break;
            }
        }

        return cmd;
    }

    public void Do(MapProject project) => Apply(project, after: true);

    public void Undo(MapProject project) => Apply(project, after: false);

    public bool TryMerge(IEditCommand next)
    {
        if (next is not ReplaceObjectsCommand n || MergeKey is null || n.MergeKey != MergeKey)
        {
            return false;
        }

        if (!SameObjects(_brushes.Select(p => p.Before.Id), n._brushes.Select(p => p.Before.Id))
            || !SameObjects(_actors.Select(p => p.Before.Id), n._actors.Select(p => p.Before.Id))
            || !SameObjects(_ges.Select(p => p.Before.Id), n._ges.Select(p => p.Before.Id)))
        {
            return false;
        }

        for (int i = 0; i < _brushes.Count; i++)
        {
            _brushes[i] = (_brushes[i].Before, n._brushes[i].After);
        }

        for (int i = 0; i < _actors.Count; i++)
        {
            _actors[i] = (_actors[i].Before, n._actors[i].After);
        }

        for (int i = 0; i < _ges.Count; i++)
        {
            _ges[i] = (_ges[i].Before, n._ges[i].After);
        }

        return true;
    }

    private static bool SameObjects(IEnumerable<string> a, IEnumerable<string> b) => a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    private void Apply(MapProject project, bool after)
    {
        foreach ((Brush before, Brush aft) in _brushes)
        {
            int i = project.IndexOfBrush((after ? before : aft).Id);
            if (i >= 0)
            {
                project.Brushes[i] = (after ? aft : before).Clone();
            }
        }

        foreach ((Actor before, Actor aft) in _actors)
        {
            int i = project.IndexOfActor((after ? before : aft).Id);
            if (i >= 0)
            {
                project.Actors[i] = (after ? aft : before).Clone();
            }
        }

        foreach ((Ge before, Ge aft) in _ges)
        {
            int i = project.IndexOfGe((after ? before : aft).Id);
            if (i >= 0)
            {
                project.Ges[i] = (after ? aft : before).Clone();
            }
        }
    }
}

/// <summary>Moves brushes within the CSG order (UE2 Order: To First / To Last / Swap).</summary>
public sealed class ReorderBrushesCommand : IEditCommand
{
    private readonly List<string> _ids;
    private readonly bool _toLast;
    private List<Brush>? _before;

    public ReorderBrushesCommand(IEnumerable<string> ids, bool toLast)
    {
        _ids = ids.ToList();
        _toLast = toLast;
        Name = toLast ? "Order: To Last" : "Order: To First";
    }

    public string Name { get; }

    public void Do(MapProject project)
    {
        _before = new List<Brush>(project.Brushes);
        var set = new HashSet<string>(_ids, StringComparer.OrdinalIgnoreCase);
        List<Brush> moved = project.Brushes.Where(b => set.Contains(b.Id)).ToList();
        List<Brush> rest = project.Brushes.Where(b => !set.Contains(b.Id)).ToList();
        project.Brushes.Clear();
        project.Brushes.AddRange(_toLast ? rest.Concat(moved) : moved.Concat(rest));
    }

    public void Undo(MapProject project)
    {
        if (_before is null)
        {
            return;
        }

        project.Brushes.Clear();
        project.Brushes.AddRange(_before);
    }
}

/// <summary>
/// Edits project-level data: identity, LevelInfo, map settings, map info, layers, prefab instances, workspace, baked
/// world, extra data (anything but the objects). The edit runs once; before and after are full
/// <see cref="ProjectState"/> snapshots, so undo and redo restore exactly what the edit changed, whatever it touched.
/// </summary>
public sealed class ProjectSettingsCommand : IEditCommand
{
    private readonly Action<MapProject> _edit;
    private ProjectState? _before;
    private ProjectState? _after;

    public ProjectSettingsCommand(string name, Action<MapProject> edit, string? mergeKey = null)
    {
        Name = name;
        _edit = edit;
        MergeKey = mergeKey;
    }

    public string Name { get; }

    /// <summary>Consecutive edits with the same key are one undo step (typing in a field, dragging a slider).</summary>
    public string? MergeKey { get; }

    public void Do(MapProject project)
    {
        if (_after is not null)
        {
            Restore(project, _after);
            return;
        }

        _before = ProjectState.Capture(project);
        _edit(project);
        _after = ProjectState.Capture(project);
    }

    public void Undo(MapProject project)
    {
        if (_before is not null)
        {
            Restore(project, _before);
        }
    }

    private void Restore(MapProject project, ProjectState state)
    {
        double? shown = project.Workspace.ShownStorey;
        FirstMapGuide guide = project.Workspace.FirstMap.Clone();
        state.Restore(project);
        project.Workspace.FirstMap = guide;
        ShownStorey.Keep(project, shown, _before!.ShownStorey, _after!.ShownStorey);
    }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not ProjectSettingsCommand n || MergeKey is null || n.MergeKey != MergeKey)
        {
            return false;
        }

        _after = n._after;
        return true;
    }
}

/// <summary>Helpers that build common commands.</summary>
public static class Edits
{
    public static ReplaceObjectsCommand Translate(MapProject project, IEnumerable<ObjectRef> refs, Vec3 delta, string? mergeKey = null) =>
        ReplaceObjectsCommand.Edit(project, refs, "Move",
            brush: b => b.Location += delta,
            actor: a => a.Location += delta,
            ge: g => g.Points = g.Points.Select(p => p + delta).ToList(),
            mergeKey: mergeKey);

    public static ReplaceObjectsCommand SetProp(MapProject project, IEnumerable<ObjectRef> actors, string key, SvJsonNode? value) =>
        ReplaceObjectsCommand.Edit(project, actors, $"Set {key}",
            actor: a => a.Props[key] = value,
            brush: b => b.Props[key] = value,
            mergeKey: "prop:" + key);

    public static ReplaceObjectsCommand SetMeta(MapProject project, IEnumerable<ObjectRef> refs, string name, Action<EditorMeta> edit) =>
        ReplaceObjectsCommand.Edit(project, refs, name,
            brush: b => edit(b.Editor),
            actor: a => edit(a.Editor),
            ge: g => edit(g.Editor));
}
