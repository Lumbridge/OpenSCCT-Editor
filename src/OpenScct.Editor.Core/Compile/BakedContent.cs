using System.Security.Cryptography;
using System.Text;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>The brushes one output layer (render or collision) is built from, and whether its world starts solid.</summary>
public sealed record BrushLayer(IReadOnlyList<Brush> Brushes, bool WorldSolid);

/// <summary>
/// What an .sdc import brings in beside the baked world that already holds it, told by the object's source JSON:
/// <list type="bullet">
/// <item>brushes marked <c>"baked": true</c> (an editor copy's CSG brushes): a kept baked layer (render or collision)
/// already holds them, so that layer leaves them out, or every brush would be built twice; a layer not kept is rebuilt
/// from them (<see cref="Render"/>, <see cref="Collision"/>). The source also keeps a hash of the brush as imported
/// (<c>{"sdc": {"hash"}}</c>), so Map Check can tell an edited one, whose edit is not built while the baked world is
/// kept. Copies of a baked brush are new brushes and lose the mark (<see cref="Unmark"/>), but a baked brush cut and
/// pasted back into its map is moved, not copied, and keeps it (<see cref="Paste"/>);</item>
/// <item>actors marked <c>"cookedOut": true</c> (static lights and other light-typed actors the cook drops, and PS2-only
/// actors): they are already in the baked lighting or not in the PC game, so the compile leaves them out as the cook
/// does; the lights among them still light a bake here (the relight, a rebuild without the baked render), as the stock
/// editor bakes with them before the cook drops them;</item>
/// <item>lights marked <c>"recovered"</c> (also cooked out): lights a cooked copy lost that the import found again in an
/// editor copy, in the file itself or by fitting stand-ins to the baked atlases; they bake like the cooked-out lights;</item>
/// <item>actors marked <c>"lifted": true</c> (RE+ player-count rules, outfits, bot controllers, conveyor belts and
/// AeroD's tread script whose settings the conversion made native data): the compile leaves them out as the conversion
/// does; the project's match rules, outfits and converted conveyors build instead. A converted conveyor names the
/// belts it stands for in its source (<c>"liftedFrom"</c>); the T3D export writes those in its place while they and
/// their conveyors are as imported (<see cref="Interop.ConveyorHandoff"/>).</item>
/// </list>
/// </summary>
public static class BakedContent
{
    /// <summary>True when the project keeps a baked layer that already holds the baked brushes.</summary>
    public static bool BakedWorldKept(MapProject p) => p.Baked is { } b && (b.KeepRender || b.KeepCollision);

    /// <summary>The brushes the world collision (and the zones, rays and traversal) is built from, and whether that world
    /// starts solid.</summary>
    public static BrushLayer Collision(MapProject p) => Layer(p, p.Baked is { KeepCollision: true });

    /// <summary>The brushes the world render (and the bake, the Lit Preview and the viewport's CSG) is built from, and
    /// whether that world starts solid.</summary>
    public static BrushLayer Render(MapProject p) => Layer(p, p.Baked is { KeepRender: true });

    /// <summary>
    /// A layer the baked world keeps holds the baked brushes, so only the others are built, into an empty world. A layer
    /// it does not keep is built from every brush into a solid world, as UE2 builds a level: with neither kept that is
    /// the whole map, and with one kept the other is rebuilt from the brushes it was made of. A converted map with no
    /// baked brushes has nothing to rebuild a layer from, so its other layer only has the brushes added since.
    /// </summary>
    private static BrushLayer Layer(MapProject p, bool kept)
    {
        if (kept)
        {
            return new BrushLayer(Stairs.StairGeometry.Expand(p.Brushes.Where(b => !IsBaked(b))).ToList(), WorldSolid: false);
        }

        bool rebuilt = !BakedWorldKept(p) || p.Brushes.Any(IsBaked);
        return new BrushLayer(Stairs.StairGeometry.Expand(p.Brushes).ToList(), WorldSolid: rebuilt);
    }

    /// <summary>True when the two layers are built from different brushes (one baked layer kept, the other rebuilt).</summary>
    public static bool LayersDiffer(MapProject p) => p.Baked is { OneLayer: true } && p.Brushes.Any(IsBaked);

    public static bool IsBaked(Brush b) => Source(b.Source, "baked")?["baked"]?.Kind == SvJsonKind.True;

    /// <summary>A brush recovered from a cooked map's compiled BSP (<c>"recovered": {"mode"}</c>, Interop.Sdc.Recovery): not the
    /// original's own brush, though it carries an import hash.</summary>
    public static bool IsRecovered(Brush b) => Source(b.Source, "recovered")?["recovered"] is SvJsonObject;

    /// <summary>True when a brush's surfaces cannot be edited: it came in with the baked world and the baked render is
    /// kept, so the views draw and the build writes the surfaces as imported and an edit would change neither.</summary>
    public static bool SurfacesLocked(MapProject p, Brush b) => p.Baked is { KeepRender: true } && IsBaked(b);

    /// <summary>Why the surfaces of a baked brush (<see cref="SurfacesLocked"/>) are read-only, for the user.</summary>
    public const string LockedSurfaceNote =
        "part of the imported baked world, which is drawn and built as imported, so surface edits would change nothing. " +
        "To edit its surfaces, build the render from the brushes (Map Settings, Baked world)";

    /// <summary>
    /// Makes copies of brushes new brushes: a copy of a baked brush is not in the baked world, so it loses the mark and
    /// its import hash (and is built like any added brush). Returns how many were baked and how many of those carve
    /// (subtract brushes, which cannot carve a kept baked world).
    /// </summary>
    public static (int Baked, int Carving) Unmark(IEnumerable<Brush> copies)
    {
        int baked = 0, carving = 0;
        foreach (Brush b in copies)
        {
            if (!IsBaked(b))
            {
                continue;
            }

            baked++;
            if (b.Oper == CsgOper.Subtract)
            {
                carving++;
            }

            var rest = new SvJsonObject();
            foreach (KeyValuePair<string, SvJsonNode> kv in Source(b.Source, "baked")!)
            {
                if (kv.Key is not ("baked" or "sdc"))
                {
                    rest.Add(kv.Key, kv.Value);
                }
            }

            b.Source = rest.Count == 0 ? null : CanonicalJson.ToCanonicalString(rest);
        }

        return (baked, carving);
    }

    /// <summary>The finding code of the notes about copies and moves of baked brushes.</summary>
    public const string CopyCode = "baked.copy";

    /// <summary>What to tell about <paramref name="baked"/> copies of brushes imported with the baked world
    /// (<see cref="Unmark"/>), or null when there were none: a warning when some of them subtract from a kept baked world.</summary>
    public static Finding? CopyNote(MapProject p, int baked, int carving)
    {
        if (baked == 0)
        {
            return null;
        }

        string note = $"{baked} of the copied brushes came in with the baked world; the copies are new brushes, built like any added brush";
        return carving > 0 && BakedWorldKept(p)
            ? Finding.Warning(CopyCode, note + $", but {carving} of them subtract, and a subtract brush cannot carve the kept baked world (build it "
                + "from the brushes instead: Map Settings, Baked world)")
            : Finding.Info(CopyCode, note);
    }

    /// <summary>
    /// Pasted brushes, with the ids they had when copied (<paramref name="copiedIds"/>, in the same order). A baked brush
    /// cut from this map (an id the baked world lists that the map no longer has) is being moved, not copied: it keeps
    /// its mark and takes its id back, like a baked brush dragged in place, so it is not built a second time over the
    /// kept baked world, which still has it where it was. The others are copies (<see cref="Unmark"/>). Returns what to
    /// tell, or null.
    /// </summary>
    public static Finding? Paste(MapProject p, IReadOnlyList<Brush> pasted, IReadOnlyList<string> copiedIds)
    {
        var listed = new HashSet<string>(p.Baked?.Brushes ?? [], StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(p.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        used.UnionWith(pasted.Select(b => b.Id));
        var copies = new List<Brush>();
        var moved = new List<string>();
        for (int i = 0; i < pasted.Count; i++)
        {
            Brush b = pasted[i];
            string was = i < copiedIds.Count ? copiedIds[i] : b.Id;
            if (IsBaked(b) && listed.Contains(was) && (string.Equals(was, b.Id, StringComparison.OrdinalIgnoreCase) || !used.Contains(was)))
            {
                used.Remove(b.Id);
                used.Add(was);
                b.Id = was;
                moved.Add(was);
            }
            else
            {
                copies.Add(b);
            }
        }

        (int baked, int carving) = Unmark(copies);
        Finding? copied = CopyNote(p, baked, carving);
        if (moved.Count == 0)
        {
            return copied;
        }

        string text = $"{moved.Count} pasted brushes were cut from this map's baked world, so they are moved, not copied: they stay baked brushes "
            + (BakedWorldKept(p)
                ? "and the kept baked world still has them where they were (Map Check reports them as edited); build it from the brushes to see them "
                    + "moved (Map Settings, Baked world)"
                : "and are built where they now are");
        text += copied is null ? "" : $". {copied.Message}";
        Severity severity = BakedWorldKept(p) || copied?.Severity == Severity.Warning ? Severity.Warning : Severity.Info;
        return new Finding(severity, CopyCode, text, moved.Select(ObjectRef.Brush).ToArray());
    }

    /// <summary>The hash stored at import, or null for a brush that has none.</summary>
    public static string? ImportedHash(Brush b) => (Source(b.Source, "baked")?["sdc"] as SvJsonObject)?["hash"] is SvJsonString h ? h.Value : null;

    public static bool IsCookedOut(Actor a) => Source(a.Source, "cookedOut")?["cookedOut"]?.Kind == SvJsonKind.True;

    public static bool IsLifted(Actor a) => Source(a.Source, "lifted")?["lifted"]?.Kind == SvJsonKind.True;

    /// <summary>A light the .sdc import brought back from what the cook stripped (<c>"recovered": {"source", "confidence",
    /// ...}</c>): its record, or null for any other actor.</summary>
    public static SvJsonObject? Recovered(Actor a) => Source(a.Source, "recovered")?["recovered"] as SvJsonObject;

    /// <summary>The source of a recovered light: <c>twin</c>, <c>orphan</c>, <c>staleTwin</c> or <c>fitted</c>.</summary>
    public static string? RecoveredSource(Actor a) => Recovered(a)?["source"] is SvJsonString s ? s.Value : null;

    /// <summary>The <see cref="MapProject.Extra"/> key of what the lifted actors stood for at import: hashes of the
    /// outfits and match rules, and the converted conveyors' groups (<c>{"outfits": H, "match": H, "conveyors": [...]}</c>,
    /// <see cref="Interop.ConveyorHandoff.Record"/>), so the T3D export can tell they were edited since.</summary>
    public const string LiftedKey = "sdcLifted";

    /// <summary>What a lifted actor stands for: <c>outfits</c> (outfit and skin classes), <c>match</c> (player-count
    /// rules), <c>conveyors</c> (RE+ conveyor classes and AeroD's tread script), or null (the bot controller, or an actor
    /// that is not lifted).</summary>
    public static string? LiftedData(Actor a)
    {
        if (!IsLifted(a))
        {
            return null;
        }

        string cls = a.Class[(a.Class.LastIndexOf('.') + 1)..];
        return cls.Contains("Outfits", StringComparison.OrdinalIgnoreCase) || cls.Contains("Skins", StringComparison.OrdinalIgnoreCase) ? "outfits"
            : cls is "SMatchSizeRules" or "SPlayerCountRules" or "SObjectivePlayers" ? "match"
            : cls.StartsWith("Conveyor", StringComparison.OrdinalIgnoreCase) || cls == "AerTreadScroll" ? "conveyors"
            : null;
    }

    /// <summary>The RE+ belts a converted conveyor stands for (its source's <c>liftedFrom</c>), or none.</summary>
    public static IReadOnlyList<string> LiftedFrom(Actor a) =>
        Source(a.Source, "liftedFrom")?["liftedFrom"] is SvJsonArray names ? names.OfType<SvJsonString>().Select(s => s.Value).ToList() : [];

    /// <summary>A hash of the project's outfits, file paths by file name (an imported map names its source map's files,
    /// a saved one its own copies).</summary>
    public static string OutfitsHash(MapProject p) => ShortHash(FileNames(OutfitsJson.ToJson(p.Outfits)));

    /// <summary>A hash of the project's match rules: every actor's (lifted ones aside), by actor id.</summary>
    public static string MatchHash(MapProject p)
    {
        var rules = new SvJsonObject();
        foreach (Actor a in p.Actors.Where(a => a.Match is not null && !IsLifted(a)).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            rules.TryAdd(a.Id.ToUpperInvariant(), MatchJson.ToJson(a.Match!));
        }

        return ShortHash(rules);
    }

    private static SvJsonNode FileNames(SvJsonNode node) => node switch
    {
        SvJsonString s when s.Value.Contains('/') || s.Value.Contains('\\') => SvJsonNode.String(Path.GetFileName(s.Value.Replace('\\', '/'))),
        SvJsonObject o => Rebuild(o),
        SvJsonArray a => new SvJsonArray(a.Select(FileNames)),
        _ => node,
    };

    private static SvJsonObject Rebuild(SvJsonObject o)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            copy.Add(kv.Key, FileNames(kv.Value));
        }

        return copy;
    }

    /// <summary>A short hash of a JSON value's canonical form.</summary>
    public static string ShortHash(SvJsonNode json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.ToCanonicalString(json))).AsSpan(0, 8));

    /// <summary>A hash of what the CSG builds from a brush: operation, flags, transform and faces (not its id,
    /// properties or editor state).</summary>
    public static string Hash(Brush b)
    {
        SvJsonObject json = ModelJson.BrushToJson(b, includeEditor: false);
        var built = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in json)
        {
            if (kv.Key is not ("id" or "props" or "src"))
            {
                built.Add(kv.Key, kv.Value);
            }
        }

        return ShortHash(built);
    }

    /// <summary>The key of an actor's source holding the hash of its shape as an imported map had it.</summary>
    public const string ShapeKey = "shapeHash";

    /// <summary>A hash of a brush-based actor's shape (its faces).</summary>
    public static string ShapeHash(BrushShape shape) => ShortHash(ModelJson.ShapeToJson(shape));

    /// <summary>Stores the hash of the actor's shape in its source, so the round-trip lint can tell a face the imported
    /// map already had.</summary>
    public static void MarkShape(Actor a)
    {
        SvJsonObject? old = Source(a.Source, "{");
        if (a.Shape is null || (a.Source is { Length: > 0 } && old is null))
        {
            return; // a source that is not JSON is kept as it is
        }

        var src = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in old ?? new SvJsonObject())
        {
            if (kv.Key != ShapeKey)
            {
                src.Add(kv.Key, kv.Value);
            }
        }

        src.Add(ShapeKey, SvJsonNode.String(ShapeHash(a.Shape)));
        a.Source = Interop.InteropValues.Compact(src);
    }

    /// <summary>True when an actor's shape is still the one its import stored (<see cref="MarkShape"/>).</summary>
    public static bool ShapeAsImported(Actor a) =>
        a.Shape is not null && Source(a.Source, ShapeKey)?[ShapeKey] is SvJsonString h && h.Value == ShapeHash(a.Shape);

    /// <summary>Removes the as-imported marks of a brush or actor (an import of the editor's own export: its shapes
    /// are the editor's, not the original's).</summary>
    public static string? WithoutImportMarks(string? source)
    {
        if (Source(source, "{") is not { } o)
        {
            return source;
        }

        var src = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key is not (ShapeKey or "t3d"))
            {
                src.Add(kv.Key, kv.Value);
            }
        }

        return src.Count == 0 ? null : Interop.InteropValues.Compact(src);
    }

    /// <summary>An object's source JSON when it mentions <paramref name="key"/> (a cheap test before parsing).</summary>
    private static SvJsonObject? Source(string? source, string key)
    {
        if (source is not { Length: > 0 } s || s[0] != '{' || !s.Contains(key, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return SvJson.Parse(s) as SvJsonObject;
        }
        catch (SvMapFormatException)
        {
            return null; // a hand-edited source that is not JSON marks nothing
        }
    }
}
