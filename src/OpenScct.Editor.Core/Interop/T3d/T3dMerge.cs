using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>What adding T3D to an open map does: the undoable command, the objects it adds and what the import found.</summary>
public sealed record T3dMergeResult(IEditCommand Command, IReadOnlyList<ObjectRef> Added, IReadOnlyList<Finding> Findings);

/// <summary>
/// Adds T3D to an existing project as one undo step: the stock editor's "import into the current map" (<c>MAP
/// IMPORTADD</c>) and Edit &gt; Paste of copied T3D text. The file's brushes go after the project's (CSG order), its
/// actors and GEs are added, and names the project already uses get fresh ids, with the added objects' references
/// to each other following. A whole-map file's LevelInfo and builder brush are left out: the open map keeps its own.
/// </summary>
public static class T3dMerge
{
    public static T3dMergeResult ImportAdd(MapProject target, T3dDocument doc, UeSchema? schema)
    {
        var findings = new List<Finding>();
        bool wholeMap = doc.Actors.Any(a => T3dImport.IsLevelInfo(InteropValues.ResolveClass(schema, a.Class), schema));
        IEnumerable<string> targetActors = target.Actors.Select(a => a.Id).Append(T3dExport.LevelInfoName(target));
        MapProject source = T3dImport.ToProject(doc, target.Id, schema, findings, hasBuilder: wholeMap, otherActors: targetActors);
        if (wholeMap)
        {
            findings.Add(Finding.Info("import.levelInfoKept", "the file's LevelInfo was not added: the open map keeps its own"));
        }

        if (doc.OtherBlocks.Count > 0)
        {
            findings.Add(Finding.Warning("import.blocksSkipped", $"{doc.OtherBlocks.Count} map-level blocks of the file were not added"));
        }

        Dictionary<string, string> renames = Rename(target, source, findings);
        foreach (Brush b in source.Brushes)
        {
            InteropValues.RenameActorRefs(b.Props, renames);
        }

        foreach (Actor a in source.Actors)
        {
            InteropValues.RenameActorRefs(a.Props, renames);
        }

        var add = new AddObjectsCommand($"Import {source.Brushes.Count + source.Actors.Count + source.Ges.Count} objects",
            source.Brushes, source.Actors, source.Ges);
        IEditCommand command = MergedRefClasses(target, source) is { } refs
            ? new CompositeCommand(add.Name, [add, refs])
            : add;
        return new T3dMergeResult(command, add.Added.ToList(), findings);
    }

    /// <summary>Gives every added object an id the project does not use; returns the renamed actor ids.</summary>
    private static Dictionary<string, string> Rename(MapProject target, MapProject source, List<Finding> findings)
    {
        var used = new HashSet<string>(target.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        used.Add(T3dExport.LevelInfoName(target));
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Fresh(string id)
        {
            string free = InteropValues.FreeId(id, used);
            if (!string.Equals(free, id, StringComparison.Ordinal))
            {
                renames[id] = free;
            }

            return free;
        }

        foreach (Brush b in source.Brushes)
        {
            b.Id = Fresh(b.Id);
        }

        foreach (Actor a in source.Actors)
        {
            a.Id = Fresh(a.Id);
        }

        foreach (Ge g in source.Ges)
        {
            g.Id = Fresh(g.Id);
        }

        if (renames.Count > 0)
        {
            findings.Add(Finding.Info("import.renamed",
                $"{renames.Count} names were already used in the map and got new ids: "
                + string.Join(", ", renames.Take(10).Select(kv => $"{kv.Key} -> {kv.Value}")) + (renames.Count > 10 ? ", ..." : "")));
        }

        return renames;
    }

    /// <summary>A command that adds the file's reference classes to the project's T3D data (so a later export writes
    /// the added objects' references with their classes), or null when there is nothing new.</summary>
    private static IEditCommand? MergedRefClasses(MapProject target, MapProject source)
    {
        if ((source.Extra[T3dImport.ExtraKey] as SvJsonObject)?["refClasses"] is not SvJsonArray added)
        {
            return null;
        }

        SvJsonObject before = target.Extra;
        var t3d = before[T3dImport.ExtraKey] as SvJsonObject ?? new SvJsonObject();
        var pairs = new SortedDictionary<string, SvJsonNode>(StringComparer.Ordinal);
        foreach (SvJsonArray pair in (t3d["refClasses"] as SvJsonArray ?? []).OfType<SvJsonArray>().Concat(added.OfType<SvJsonArray>()))
        {
            if (pair is { Count: 2 } && pair[0] is SvJsonString path)
            {
                pairs.TryAdd(path.Value, pair);
            }
        }

        var newT3d = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in t3d.Where(kv => kv.Key != "refClasses"))
        {
            newT3d.Add(kv.Key, kv.Value);
        }

        newT3d.Add("refClasses", new SvJsonArray(pairs.Values));
        var after = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in before.Where(kv => kv.Key != T3dImport.ExtraKey))
        {
            after.Add(kv.Key, kv.Value);
        }

        after.Add(T3dImport.ExtraKey, newT3d);
        return new DelegateCommand("Add reference classes", p => p.Extra = after, p => p.Extra = before);
    }
}
