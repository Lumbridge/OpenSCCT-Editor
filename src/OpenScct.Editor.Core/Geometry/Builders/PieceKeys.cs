using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>A piece is named by its first brush's id. When that id goes (deleted or renamed), the remaining parts
/// take the id of their new first part, so the freed id never joins a later piece to this one.</summary>
public static class PieceKeys
{
    /// <summary>The pieces still keyed to a gone id: their parts (in part order) and the key they should take.</summary>
    public static List<(IReadOnlyList<Brush> Parts, string Key)> Orphans(MapProject project, IReadOnlySet<string> gone)
    {
        var result = new List<(IReadOnlyList<Brush>, string)>();
        if (gone.Count == 0)
        {
            return result;
        }

        var orphans = project.Brushes
            .Where(b => KeyOf(b) is { } p && gone.Contains(p))
            .GroupBy(b => KeyOf(b)!, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, Brush> piece in orphans)
        {
            List<Brush> parts = piece.OrderBy(b => b.Shape.Builder!.Get(BuilderArgs.PartKey, 0)).ToList();
            result.Add((parts, parts[0].Id));
        }

        return result;
    }

    /// <summary>Deletes objects and re-keys the pieces they leave behind, as one undo step.</summary>
    public static IEditCommand Delete(IReadOnlyList<ObjectRef> refs)
    {
        var delete = new DeleteObjectsCommand(refs);
        return new CompositeCommand(delete.Name, [delete, new RekeyPiecesCommand(BrushIds(refs))]);
    }

    public static HashSet<string> BrushIds(IEnumerable<ObjectRef> refs) =>
        refs.Where(r => r.Kind == ObjectKind.Brush).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? KeyOf(Brush b) => b.Shape.Builder?.Strings.GetValueOrDefault(BuilderArgs.PieceKey);
}

/// <summary>Re-keys the pieces left keyed to gone ids. It looks at the project when it runs, so it goes after the
/// delete or rename in the same undo step; it does nothing when no piece lost its first part.</summary>
public sealed class RekeyPiecesCommand(IReadOnlySet<string> gone) : IEditCommand
{
    private ReplaceObjectsCommand? _edit;

    public string Name => "Rekey pieces";

    public void Do(MapProject project)
    {
        _edit = new ReplaceObjectsCommand(Name);
        foreach ((IReadOnlyList<Brush> parts, string key) in PieceKeys.Orphans(project, gone))
        {
            foreach (Brush b in parts)
            {
                Brush after = b.Clone();
                after.Shape.Builder!.Strings[BuilderArgs.PieceKey] = key;
                _edit.Brush(b.Clone(), after);
            }
        }

        _edit.Do(project);
    }

    public void Undo(MapProject project) => _edit?.Undo(project);
}
