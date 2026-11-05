using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// Collects brush replacements, insertions right after a brush (CSG order matters) and deletions into one undoable
/// command. Insertions are undone by id, so a later redo puts back the same objects.
/// </summary>
public sealed class BrushListEdit(string name)
{
    private readonly List<(Brush Before, Brush After)> _replace = new();
    private readonly List<(string Anchor, Brush Added)> _insert = new();
    private readonly List<string> _delete = new();

    public IReadOnlyList<string> Added => _insert.Select(x => x.Added.Id).ToList();

    public IReadOnlyList<string> Changed => _replace.Select(x => x.Before.Id).ToList();

    public IReadOnlyList<string> Removed => _delete;

    public bool IsEmpty => _replace.Count == 0 && _insert.Count == 0 && _delete.Count == 0;

    public void Replace(Brush before, Brush after)
    {
        after.Id = before.Id;
        _replace.Add((before.Clone(), after));
    }

    public void InsertAfter(Brush anchor, Brush added) => _insert.Add((anchor.Id, added));

    public void Delete(Brush b) => _delete.Add(b.Id);

    public IEditCommand Build()
    {
        var parts = new List<IEditCommand>();
        if (_replace.Count > 0)
        {
            var replace = new ReplaceObjectsCommand(name);
            foreach ((Brush before, Brush after) in _replace)
            {
                replace.Brush(before, after);
            }

            parts.Add(replace);
        }

        foreach ((string anchor, Brush added) in _insert)
        {
            parts.Add(new DelegateCommand($"Insert {added.Id}",
                p => p.Brushes.Insert(p.Brushes.FindIndex(b => string.Equals(b.Id, anchor, StringComparison.OrdinalIgnoreCase)) + 1, added.Clone()),
                p => p.Brushes.RemoveAll(b => string.Equals(b.Id, added.Id, StringComparison.OrdinalIgnoreCase))));
        }

        if (_delete.Count > 0)
        {
            parts.Add(new DeleteObjectsCommand(_delete.Select(ObjectRef.Brush)));
            parts.Add(new RekeyPiecesCommand(_delete.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }

        return new CompositeCommand(name, parts);
    }
}
