using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Commands;

/// <summary>An undoable edit of the map's prefab instance table (<see cref="MapProject.Prefabs"/>). The objects of an
/// instance are edited by the usual object commands alongside it.</summary>
public sealed class PrefabInstancesCommand : IEditCommand
{
    private readonly Action<List<PrefabInstance>> _edit;
    private List<PrefabInstance>? _before;
    private List<PrefabInstance>? _after;

    public PrefabInstancesCommand(string name, Action<List<PrefabInstance>> edit)
    {
        Name = name;
        _edit = edit;
    }

    public string Name { get; }

    public void Do(MapProject project)
    {
        if (_after is null)
        {
            _before = Copy(project.Prefabs);
            List<PrefabInstance> edited = Copy(project.Prefabs);
            _edit(edited);
            _after = edited;
        }

        project.Prefabs = Copy(_after);
    }

    public void Undo(MapProject project)
    {
        if (_before is not null)
        {
            project.Prefabs = Copy(_before);
        }
    }

    /// <summary>Adds an instance, or replaces the one with its id.</summary>
    public static PrefabInstancesCommand Put(PrefabInstance instance) => new($"Prefab instance {instance.Id}", list =>
    {
        int i = list.FindIndex(x => string.Equals(x.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            list[i] = instance.Clone();
        }
        else
        {
            list.Add(instance.Clone());
        }
    });

    public static PrefabInstancesCommand Remove(IReadOnlyCollection<string> ids) =>
        new(ids.Count == 1 ? $"Break prefab instance {ids.First()}" : $"Break {ids.Count} prefab instances",
            list => list.RemoveAll(x => ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase)));

    private static List<PrefabInstance> Copy(List<PrefabInstance> list) => list.Select(i => i.Clone()).ToList();
}
