using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Commands;

/// <summary>One undoable edit of a <see cref="MapProject"/>. Every change the editor makes goes through a command,
/// property edits included (the stock property window recorded no undo).</summary>
public interface IEditCommand
{
    /// <summary>What the Undo History shows, e.g. "Move 3 objects".</summary>
    string Name { get; }

    void Do(MapProject project);

    void Undo(MapProject project);

    /// <summary>Merges a following command into this one (drags, typing), or returns false.</summary>
    bool TryMerge(IEditCommand next) => false;
}

/// <summary>Several commands as one undo step.</summary>
public sealed class CompositeCommand : IEditCommand
{
    private readonly List<IEditCommand> _parts;

    public CompositeCommand(string name, IEnumerable<IEditCommand> parts)
    {
        Name = name;
        _parts = parts.ToList();
    }

    public string Name { get; }

    public IReadOnlyList<IEditCommand> Parts => _parts;

    public void Do(MapProject project)
    {
        foreach (IEditCommand c in _parts)
        {
            c.Do(project);
        }
    }

    public void Undo(MapProject project)
    {
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            _parts[i].Undo(project);
        }
    }
}

/// <summary>A command made from two delegates.</summary>
public sealed class DelegateCommand : IEditCommand
{
    private readonly Action<MapProject> _do;
    private readonly Action<MapProject> _undo;

    public DelegateCommand(string name, Action<MapProject> doAction, Action<MapProject> undoAction)
    {
        Name = name;
        _do = doAction;
        _undo = undoAction;
    }

    public string Name { get; }

    public void Do(MapProject project) => _do(project);

    public void Undo(MapProject project) => _undo(project);
}

/// <summary>An entry of the undo history.</summary>
public sealed record HistoryEntry(int Index, string Name, bool IsCheckpoint, bool IsDone);

/// <summary>Undo/redo with history, named checkpoints, merging and a dirty flag.</summary>
public sealed class CommandStack
{
    private readonly MapProject _project;
    private readonly List<IEditCommand> _done = new();
    private readonly List<IEditCommand> _undone = new();
    private readonly Dictionary<int, string> _checkpoints = new();
    private int _savedAt;
    private bool _mergeBlocked = true;

    public CommandStack(MapProject project) => _project = project;

    /// <summary>Raised after every do, undo or redo, with the command.</summary>
    public event Action<IEditCommand>? Changed;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public string? UndoName => _done.Count > 0 ? _done[^1].Name : null;

    public string? RedoName => _undone.Count > 0 ? _undone[^1].Name : null;

    /// <summary>True when the project changed since the last <see cref="MarkSaved"/>.</summary>
    public bool IsDirty => _done.Count != _savedAt;

    /// <summary>Number of steps done; the position in the history.</summary>
    public int Position => _done.Count;

    public int Limit { get; set; } = 1000;

    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not Windows.WindowDependentCommand && _project.Actors.Any(a => a.Window?.Binding is not null))
            command = new Windows.WindowDependentCommand(command);
        command.Do(_project);
        DropRedo();
        if (!_mergeBlocked && _done.Count > 0 && _done.Count != _savedAt && _done[^1].TryMerge(command))
        {
            Changed?.Invoke(command);
            return;
        }

        _done.Add(command);
        _mergeBlocked = false;
        if (_done.Count > Limit)
        {
            _done.RemoveAt(0);
            _savedAt--;
            ShiftCheckpoints();
        }

        Changed?.Invoke(command);
    }

    /// <summary>Stops the next command merging into the last (end of a drag or of typing).</summary>
    public void EndMerge() => _mergeBlocked = true;

    public bool Undo()
    {
        if (_done.Count == 0)
        {
            return false;
        }

        IEditCommand c = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        c.Undo(_project);
        _undone.Add(c);
        _mergeBlocked = true;
        Changed?.Invoke(c);
        return true;
    }

    public bool Redo()
    {
        if (_undone.Count == 0)
        {
            return false;
        }

        IEditCommand c = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        c.Do(_project);
        _done.Add(c);
        _mergeBlocked = true;
        Changed?.Invoke(c);
        return true;
    }

    /// <summary>Undoes or redoes until <see cref="Position"/> is <paramref name="position"/> (Undo History jump).</summary>
    public void JumpTo(int position)
    {
        while (_done.Count > position && Undo())
        {
        }

        while (_done.Count < position && Redo())
        {
        }
    }

    /// <summary>Names the current position (shown in the history; RE+ checkpoints).</summary>
    public void Checkpoint(string name) => _checkpoints[_done.Count] = name;

    public void MarkSaved()
    {
        _savedAt = _done.Count;
        _mergeBlocked = true;
    }

    /// <summary>Marks the map as not matching any saved state (a map recovered from an autosave).</summary>
    public void MarkUnsaved() => _savedAt = -1;

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _checkpoints.Clear();
        _savedAt = 0;
    }

    /// <summary>The history, oldest first: done steps, then undone (redoable) steps.</summary>
    public IReadOnlyList<HistoryEntry> History()
    {
        var list = new List<HistoryEntry>();
        for (int i = 0; i < _done.Count; i++)
        {
            list.Add(new HistoryEntry(i + 1, _done[i].Name, _checkpoints.ContainsKey(i + 1), true));
        }

        for (int i = _undone.Count - 1; i >= 0; i--)
        {
            int index = _done.Count + (_undone.Count - i);
            list.Add(new HistoryEntry(index, _undone[i].Name, _checkpoints.ContainsKey(index), false));
        }

        return list;
    }

    public string? CheckpointName(int position) => _checkpoints.GetValueOrDefault(position);

    /// <summary>Forgets the undone steps a new command replaces, with their checkpoints. When the saved state was one
    /// of them, no position matches the disk any more.</summary>
    private void DropRedo()
    {
        if (_undone.Count == 0)
        {
            return;
        }

        _undone.Clear();
        if (_savedAt > _done.Count)
        {
            _savedAt = -1;
        }

        foreach (int position in _checkpoints.Keys.Where(k => k > _done.Count).ToList())
        {
            _checkpoints.Remove(position);
        }
    }

    private void ShiftCheckpoints()
    {
        var shifted = _checkpoints.Where(kv => kv.Key > 1).ToDictionary(kv => kv.Key - 1, kv => kv.Value);
        _checkpoints.Clear();
        foreach (KeyValuePair<int, string> kv in shifted)
        {
            _checkpoints[kv.Key] = kv.Value;
        }
    }
}
