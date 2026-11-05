using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Interop;

/// <summary>
/// How the T3D export hands the conveyors an .sdc import converted from RE+ belts back to the original editor.
/// <para>The converted conveyors (those with a <see cref="BakedContent.LiftedFrom"/> source) and the lifted belts fall
/// into groups: conveyors that stand for the same belt or hand items on to each other (<c>Next</c>), with the belts
/// they stand for and the belts linked to those by <c>NextBelt</c>: one RE+ belt chain with its item hand-ons. The
/// import records each group's belts and a hash of its conveyors (<see cref="Record"/>).</para>
/// <para>A group whose conveyors and belts are as imported is written as its belts, as imported. Any other (a conveyor
/// edited, added to it or deleted, or one of its belts deleted) is written as new RE+ belts lowered from its conveyors
/// (<see cref="ReConveyors"/>), and its imported belts are left out, so the edit reaches the original editor. A lifted
/// belt no recorded group holds is written as imported, as are the other lifted conveyor actors (runtime classes and
/// AeroD's tread script, which pans its meshes by tag whatever the belts do).</para>
/// </summary>
public sealed class ConveyorHandoff
{
    /// <summary>The key of the groups' record in the <see cref="BakedContent.LiftedKey"/> object.</summary>
    public const string Key = "conveyors";

    /// <summary>One group: its conveyors and the belts it stood for at import, whether it is written as those belts,
    /// and if not, why.</summary>
    public sealed record Group(IReadOnlyList<string> Conveyors, IReadOnlyList<string> Belts, bool AsImported, string Change);

    // The project's lifted belts.
    private readonly HashSet<string> _belts;

    private ConveyorHandoff(List<Group> groups, HashSet<string> carried, HashSet<string> dropped, HashSet<string> belts)
    {
        Groups = groups;
        Carried = carried;
        Dropped = dropped;
        _belts = belts;
    }

    public IReadOnlyList<Group> Groups { get; }

    /// <summary>Converted conveyors the export does not lower: their belts are written as imported.</summary>
    public IReadOnlySet<string> Carried { get; }

    /// <summary>Lifted belts the export leaves out: their group is lowered from its conveyors, or was deleted.</summary>
    public IReadOnlySet<string> Dropped { get; }

    /// <summary>True when the T3D export writes the actor as itself (not a carried conveyor or a dropped belt).</summary>
    public bool Writes(Actor a) => !Carried.Contains(a.Id) && !Dropped.Contains(a.Id);

    /// <summary>The lifted belts carried conveyors stand for (written with them when only they are copied).</summary>
    public HashSet<string> BeltsOf(IEnumerable<Actor> conveyors) =>
        new(conveyors.Where(c => Carried.Contains(c.Id)).SelectMany(BakedContent.LiftedFrom).Where(_belts.Contains), StringComparer.OrdinalIgnoreCase);

    /// <summary>A lifted RE+ <c>ConveyorBelt</c> (the placed class; its motor, items and rollers are spawned).</summary>
    public static bool IsLiftedBelt(Actor a) =>
        BakedContent.IsLifted(a) && a.ShortClass.Equals("ConveyorBelt", StringComparison.OrdinalIgnoreCase);

    public static ConveyorHandoff Of(MapProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        List<(HashSet<string> Belts, string Hash)> records = Recorded(p);
        var groups = new List<Group>();
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((List<Actor> conveyors, HashSet<string> belts, HashSet<string> named) in Components(p, records.Select(r => r.Belts)))
        {
            var recorded = records.Where(r => r.Belts.Overlaps(belts) || r.Belts.Overlaps(named)).ToList();
            if (conveyors.Count == 0 && recorded.Count == 0)
            {
                continue; // belts the conversion did not take: written as imported
            }

            var stood = new HashSet<string>(recorded.SelectMany(r => r.Belts), StringComparer.OrdinalIgnoreCase);
            stood.UnionWith(named);
            string change = recorded.Count == 0 ? "not recorded at import"
                : recorded.Count > 1 ? "joined to another converted chain"
                : stood.Except(belts, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } gone ? $"belt {gone} deleted"
                : belts.Except(stood, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } added ? $"belt {added} added"
                : conveyors.Count == 0 ? "its conveyors deleted"
                : Hash(conveyors) != recorded[0].Hash ? "conveyors edited, added or deleted"
                : "";
            bool asImported = change.Length == 0;
            if (asImported)
            {
                carried.UnionWith(conveyors.Select(c => c.Id));
            }
            else
            {
                dropped.UnionWith(belts.Where(stood.Contains));
            }

            groups.Add(new Group(conveyors.Select(c => c.Id).ToList(), stood.Order(StringComparer.OrdinalIgnoreCase).ToList(), asImported, change));
        }

        return new ConveyorHandoff(groups, carried, dropped,
            new HashSet<string>(p.Actors.Where(IsLiftedBelt).Select(a => a.Id), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The groups as imported, for <see cref="BakedContent.LiftedKey"/>: <c>[{"belts": [...], "hash": H}]</c>.</summary>
    public static SvJsonArray Record(MapProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var records = new SvJsonArray();
        foreach ((List<Actor> conveyors, HashSet<string> belts, HashSet<string> named) in Components(p, []))
        {
            if (conveyors.Count == 0)
            {
                continue;
            }

            var record = new SvJsonObject();
            record.Add("belts", new SvJsonArray(belts.Union(named, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(SvJsonNode.String)));
            record.Add("hash", SvJsonNode.String(Hash(conveyors)));
            records.Add(record);
        }

        return records;
    }

    /// <summary>A hash of what is built from a group's conveyors, by actor id (not their editor state).</summary>
    private static string Hash(IEnumerable<Actor> conveyors)
    {
        var all = new SvJsonObject();
        foreach (Actor a in conveyors.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            all.TryAdd(a.Id.ToUpperInvariant(), ModelJson.ActorToJson(a, includeEditor: false));
        }

        return BakedContent.ShortHash(all);
    }

    private static List<(HashSet<string> Belts, string Hash)> Recorded(MapProject p) =>
        ((p.Extra[BakedContent.LiftedKey] as SvJsonObject)?[Key] as SvJsonArray ?? [])
        .OfType<SvJsonObject>()
        .Where(r => r["hash"] is SvJsonString && r["belts"] is SvJsonArray)
        .Select(r => (new HashSet<string>(((SvJsonArray)r["belts"]!).OfType<SvJsonString>().Select(s => s.Value), StringComparer.OrdinalIgnoreCase),
            ((SvJsonString)r["hash"]!).Value))
        .ToList();

    /// <summary>
    /// The groups: (converted conveyors, lifted belts present, belt names the conveyors stand for), joined by shared
    /// belts, <c>Next</c>, <c>NextBelt</c> and the recorded groups' belts.
    /// </summary>
    private static List<(List<Actor> Conveyors, HashSet<string> Belts, HashSet<string> Named)> Components(MapProject p,
        IEnumerable<HashSet<string>> recorded)
    {
        List<Actor> conveyors = p.Actors.Where(a => BakedContent.LiftedFrom(a).Count > 0).ToList();
        List<Actor> belts = p.Actors.Where(IsLiftedBelt).ToList();
        var converted = new HashSet<string>(conveyors.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var sets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Root(string x)
        {
            while (sets.TryGetValue(x, out string? up) && !up.Equals(x, StringComparison.OrdinalIgnoreCase))
            {
                x = up;
            }

            sets.TryAdd(x, x);
            return x;
        }

        void Join(string a, string b)
        {
            string ra = Root(a), rb = Root(b);
            if (!ra.Equals(rb, StringComparison.OrdinalIgnoreCase))
            {
                sets[ra] = rb;
            }
        }

        foreach (Actor c in conveyors)
        {
            Root(c.Id);
            foreach (string name in BakedContent.LiftedFrom(c))
            {
                Join(c.Id, name);
            }

            if (ConveyorDesign.NextId(c) is { } next && converted.Contains(next))
            {
                Join(c.Id, next);
            }
        }

        foreach (Actor b in belts)
        {
            Root(b.Id);
            if (PropValues.ActorId(b.Props["NextBelt"]) is { } next)
            {
                Join(b.Id, next);
            }
        }

        foreach (HashSet<string> group in recorded)
        {
            string? first = group.FirstOrDefault();
            foreach (string name in group)
            {
                Join(first!, name);
            }
        }

        var byRoot = new Dictionary<string, (List<Actor>, HashSet<string>, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        (List<Actor> Conveyors, HashSet<string> Belts, HashSet<string> Named) Of(string id)
        {
            string root = Root(id);
            if (!byRoot.TryGetValue(root, out var g))
            {
                byRoot[root] = g = ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            return g;
        }

        // The belts a conveyor names: a name of another lifted actor (a tread script an earlier import listed) is no belt.
        var others = new HashSet<string>(p.Actors.Where(a => BakedContent.IsLifted(a) && !IsLiftedBelt(a)).Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (Actor c in conveyors)
        {
            var g = Of(c.Id);
            g.Conveyors.Add(c);
            g.Named.UnionWith(BakedContent.LiftedFrom(c).Where(n => !others.Contains(n)));
        }

        foreach (Actor b in belts)
        {
            Of(b.Id).Belts.Add(b.Id);
        }

        foreach (HashSet<string> group in recorded)
        {
            foreach (string name in group)
            {
                _ = Of(name);
            }
        }

        return byRoot.Values.ToList();
    }
}
