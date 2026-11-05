using System.Globalization;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>Replaces the brushes of a piece with a regenerated set. A part that keeps its id keeps its place in the CSG
/// order; extra parts go after the piece's last brush. Works by id and puts in copies, so it stays correct when other
/// commands have swapped the live objects since (undo, redo, drags).</summary>
public sealed class PieceEdit : IEditCommand
{
    private readonly List<int> _removedAt = new();

    public PieceEdit(int index, IReadOnlyList<Brush> removed, IReadOnlyList<Brush> added)
    {
        Index = index;
        Removed = removed.Select(b => b.Clone()).ToList();
        Added = added;
    }

    /// <summary>Where the piece starts in the brush list (the first old brush's place).</summary>
    public int Index { get; }

    public IReadOnlyList<Brush> Removed { get; }

    public IReadOnlyList<Brush> Added { get; }

    public string Name => $"Rebuild {Added.FirstOrDefault()?.Shape.Builder?.Kind ?? "piece"}";

    public void Do(MapProject project)
    {
        _removedAt.Clear();
        foreach (Brush b in Removed)
        {
            _removedAt.Add(project.IndexOfBrush(b.Id));
        }

        HashSet<string> kept = Ids(Removed.Where((_, i) => _removedAt[i] >= 0));
        kept.IntersectWith(Ids(Added));
        HashSet<string> gone = Ids(Removed);
        gone.ExceptWith(kept);
        project.Brushes.RemoveAll(b => gone.Contains(b.Id));
        int next = Math.Min(Index, project.Brushes.Count);
        foreach (Brush b in Added)
        {
            int i = kept.Contains(b.Id) ? project.IndexOfBrush(b.Id) : -1;
            if (i >= 0)
            {
                project.Brushes[i] = b.Clone();
                next = Math.Max(next, i + 1);
            }
            else
            {
                project.Brushes.Insert(Math.Min(next, project.Brushes.Count), b.Clone());
                next++;
            }
        }
    }

    public void Undo(MapProject project)
    {
        HashSet<string> kept = Ids(Removed.Where((_, i) => _removedAt[i] >= 0));
        kept.IntersectWith(Ids(Added));
        HashSet<string> added = Ids(Added);
        added.ExceptWith(kept);
        project.Brushes.RemoveAll(b => added.Contains(b.Id));
        foreach ((int at, Brush b) in _removedAt.Zip(Removed).Where(p => p.First >= 0).OrderBy(p => p.First))
        {
            int i = kept.Contains(b.Id) ? project.IndexOfBrush(b.Id) : -1;
            if (i >= 0)
            {
                project.Brushes[i] = b.Clone();
            }
            else
            {
                project.Brushes.Insert(Math.Clamp(at, 0, project.Brushes.Count), b.Clone());
            }
        }
    }

    private static HashSet<string> Ids(IEnumerable<Brush> brushes) => new(brushes.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns builder specs into placed brushes and regenerates placed pieces from new parameters. A multi-brush piece
/// marks every part's spec with the piece id (its first brush id) and the part index, so selecting or rebuilding one
/// part finds the whole piece. Pure: nothing is added to the project until the caller runs a command.
/// </summary>
public static class PiecePlacer
{
    /// <summary>The brushes of a spec placed at <paramref name="location"/>. <paramref name="oper"/> applies to plain
    /// builders (null: the kind's <see cref="BuilderKind.DefaultOper"/>); blockout pieces choose their own per part.</summary>
    public static IReadOnlyList<Brush> Create(MapProject project, BuilderSpec spec, Vec3 location, Rot rotation = default,
        CsgOper? oper = null)
    {
        BuilderKind kind = BuilderCatalog.Get(spec.Kind);
        RejectUnknownKeys(kind, spec);
        IReadOnlyList<BuiltPart> parts = kind.Build(kind.WithBodyDefaults(WithoutPieceKeys(spec), Validation.DesignNumbers.Of(project)));
        var ids = new IdAllocator(project);
        string stem = Stem(kind.Kind);
        List<string> partIds = parts.Select(_ => ids.Next(stem)).ToList();
        return Assemble(kind, parts, partIds, location, rotation, templates: null, oper ?? kind.DefaultOper);
    }

    /// <summary>Rebuilds the piece <paramref name="brushId"/> belongs to from <paramref name="spec"/> (or its stored
    /// spec), keeping its place, transform and CSG choice. Old ids are reused in part order, and each part keeps its own
    /// flags, props, editor state and place in the CSG order; extra parts take part 0's.</summary>
    public static PieceEdit Regenerate(MapProject project, string brushId, BuilderSpec? spec = null)
    {
        Brush hit = project.FindBrush(brushId) ?? throw new ArgumentException($"no brush '{brushId}'");
        BuilderSpec stored = hit.Shape.Builder ?? throw new ArgumentException($"brush '{brushId}' is not parametric (no builder spec)");
        List<Brush> old = PieceOf(project, hit);
        Brush template = old[0];
        BuilderSpec next = WithoutPieceKeys(spec ?? stored);
        if (!string.Equals(next.Kind, stored.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"brush '{brushId}' is a {stored.Kind}, not a {next.Kind}");
        }

        BuilderKind kind = BuilderCatalog.Get(stored.Kind);
        if (spec is not null)
        {
            RejectUnknownKeys(kind, spec);
        }

        IReadOnlyList<BuiltPart> parts = kind.Build(next);
        var ids = new IdAllocator(project);
        foreach (Brush b in old)
        {
            ids.Release(b.Id);
        }

        string stem = Stem(kind.Kind);
        var partIds = new List<string>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            partIds.Add(i < old.Count ? ids.Claim(old[i].Id) : ids.Next(stem));
        }

        IReadOnlyList<Brush> added = Assemble(kind, parts, partIds, template.Location, template.Rotation, old, template.Oper);
        int index = old.Min(b => project.Brushes.IndexOf(b));
        return new PieceEdit(index, old, added);
    }

    /// <summary>Every brush of the piece <paramref name="brush"/> belongs to, by part index (the brush alone when it
    /// is not part of a piece).</summary>
    public static List<Brush> PieceOf(MapProject project, Brush brush)
    {
        if (brush.Shape.Builder?.Strings.GetValueOrDefault(BuilderArgs.PieceKey) is not { Length: > 0 } piece)
        {
            return [brush];
        }

        return project.Brushes
            .Where(b => b.Shape.Builder?.Strings.GetValueOrDefault(BuilderArgs.PieceKey) is { } p && string.Equals(p, piece, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.Shape.Builder!.Get(BuilderArgs.PartKey, 0))
            .ThenBy(project.Brushes.IndexOf)
            .ToList();
    }

    /// <summary>Makes copied brushes a piece of their own: a copied part follows the copy of its piece's first brush
    /// (<paramref name="copyOf"/> maps original ids to copy ids), and a part copied without it becomes a standalone
    /// brush. Without this, rebuilding the original or a copy would take every copy with it.</summary>
    public static void RepointPieces(IEnumerable<Brush> copies, IReadOnlyDictionary<string, string> copyOf)
    {
        foreach (Brush b in copies)
        {
            if (b.Shape.Builder?.Strings is not { } strings || !strings.TryGetValue(BuilderArgs.PieceKey, out string? piece))
            {
                continue;
            }

            if (copyOf.TryGetValue(piece, out string? copy))
            {
                strings[BuilderArgs.PieceKey] = copy;
            }
            else
            {
                strings.Remove(BuilderArgs.PieceKey);
            }
        }
    }

    private static List<Brush> Assemble(BuilderKind kind, IReadOnlyList<BuiltPart> parts, List<string> ids, Vec3 location, Rot rotation,
        IReadOnlyList<Brush>? templates, CsgOper oper)
    {
        string? piece = parts.Count > 1 ? ids[0] : null;
        var brushes = new List<Brush>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            BuiltPart part = parts[i];
            Brush? template = templates?[i < templates.Count ? i : 0];
            Brush b = template?.Clone(ids[i]) ?? new Brush(ids[i]);
            b.Shape = part.Shape;
            if (piece is not null)
            {
                b.Shape.Builder!.Strings[BuilderArgs.PieceKey] = piece;
            }

            b.Oper = kind.IsBlockout ? part.Oper : oper;
            b.Location = location + rotation.Rotate(part.Offset);
            b.Rotation = rotation;
            b.Source = template?.Source;
            brushes.Add(b);
        }

        return brushes;
    }

    /// <summary>New specs come from people and scripts: a misspelt parameter is an error, not silently the default.</summary>
    private static void RejectUnknownKeys(BuilderKind kind, BuilderSpec spec)
    {
        List<string> unknown = BuilderArgs.UnknownKeys(kind, spec).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{kind.Kind} has no parameter {string.Join(", ", unknown)} (parameters: {string.Join(", ", kind.Params.Select(p => p.Name))})");
        }
    }

    /// <summary>A copy of a spec without the piece and part keys (<see cref="Assemble"/> stamps fresh ones).</summary>
    private static BuilderSpec WithoutPieceKeys(BuilderSpec spec)
    {
        BuilderSpec clean = spec.Clone();
        clean.Strings.Remove(BuilderArgs.PieceKey);
        clean.Numbers.Remove(BuilderArgs.PartKey);
        return clean;
    }

    /// <summary>The id stem of a kind: <c>linearStair</c> becomes <c>LinearStair</c>.</summary>
    public static string Stem(string kind) => kind.Length == 0 ? "Brush" : char.ToUpperInvariant(kind[0]) + kind[1..];

    /// <summary>Hands out ids no object of the project (or an earlier call) uses, ignoring case: Stem0, Stem1...</summary>
    private sealed class IdAllocator
    {
        private readonly HashSet<string> _used;

        public IdAllocator(MapProject project) =>
            _used = new HashSet<string>(project.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

        public string Next(string stem)
        {
            for (int i = 0; ; i++)
            {
                string id = stem + i.ToString(CultureInfo.InvariantCulture);
                if (_used.Add(id))
                {
                    return id;
                }
            }
        }

        public void Release(string id) => _used.Remove(id);

        public string Claim(string id)
        {
            _used.Add(id);
            return id;
        }
    }
}
