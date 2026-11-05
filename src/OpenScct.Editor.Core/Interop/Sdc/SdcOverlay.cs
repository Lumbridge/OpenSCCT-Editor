using System.Globalization;
using System.Numerics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Lays a natively read .sdc (<see cref="SdcMap"/>) over the project the SVMap importer made from its conversion. The
/// conversion keeps the baked world (render, collision, lighting), the LevelInfo, map settings, map info, GEs and
/// sounds; the native read adds what the conversion cannot carry:
/// <list type="bullet">
/// <item>the CSG brushes of an editor copy, in Level.Actors (CSG) order, marked as built into the baked world;</item>
/// <item>the exact shapes of volumes, movers and anti-portals from their brush models (in place of the shapes
/// recovered from colliders);</item>
/// <item>every stored property of every actor, decoded as the converter decodes gameplay actors (render actors from
/// the world.glb, lighting and fx tables otherwise carry only a few), with the build language's text kept;</item>
/// <item>the objects an actor owns through an export-object property (an Emitter's particle emitters), with every
/// stored property, as the T3D importer keeps the stock export's Begin Object blocks;</item>
/// <item>the actors only an editor copy holds: static lights the cook drops and PS2-only actors (marked cooked out),
/// and render actors the conversion keeps only in its render files;</item>
/// <item>the RE+ actors whose settings the conversion turned into native data (player-count rules, character outfits
/// and the Play Level bot controller, conveyor belts and AeroD's tread script; marked lifted), so the T3D export still
/// writes them for the original editor.</item>
/// </list>
/// Cameras and deleted actors are dropped, as the stock editor's T3D export drops them.
/// </summary>
public static class SdcOverlay
{
    public const string CookedOutFolder = "Render/Cooked out";
    public const string OtherRenderFolder = "Render/Other";
    public const string LiftedFolder = "Gameplay/RE+ lifted";

    /// <summary>The RE+ classes (by short name) whose actors the conversion lifts into native SVMap data when the map
    /// defines them itself (svmbuild mapconv/mapclasses.py, conveyors.py): player-count rules (sim/match.json),
    /// character outfits (render/outfits.json) and the Play Level bot controller dropped with them, and conveyor belts
    /// (Svm.Conveyor actors) with their runtime classes and AeroD's tread script.</summary>
    public static readonly IReadOnlyList<string> LiftedClasses =
    [
        "SMatchSizeRules", "SPlayerCountRules", "SObjectivePlayers",
        "SCharacterOutfits", "SCharacterSkins", "ReloadedCharacterSkins5", "ReloadedCharacterSkins4", "ReloadedCharacterSkins3",
        "ReloadedCharacterSkins2", "ReloadedCharacterSkins", "ReloadedBotController",
        "ConveyorBelt", "ConveyorMotor", "ConveyorItem", "ConveyorRoller", "AerTreadScroll",
    ];

    private const int PlatformPs2Only = 1;

    /// <summary>True for an actor of a <see cref="LiftedClasses"/> class the map's own package defines: the conversion
    /// lifts only those, and leaves a class of another package as it is.</summary>
    private static bool IsLifted(SdcActor n, SdcMap map) =>
        LiftedClasses.Contains(InteropValues.ShortClass(n.Class), StringComparer.OrdinalIgnoreCase) &&
        n.Class.StartsWith(map.Name + ".", StringComparison.OrdinalIgnoreCase);

    public static void Apply(MapProject p, SdcMap map, UeSchema? schema, List<Finding> findings)
    {
        var counts = new Counts();
        var native = new Dictionary<string, SdcActor>(StringComparer.OrdinalIgnoreCase);
        foreach (SdcActor a in map.Actors.Skip(1))
        {
            native.TryAdd(a.Name, a);
        }

        var used = new HashSet<string>(p.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var types = new T3dTypes(schema, p.Id);
        AddBrushes(p, map, used, counts, findings);
        foreach (Actor a in p.Actors)
        {
            if (native.Remove(a.Id, out SdcActor? n))
            {
                Overlay(a, n, map, schema, counts);
                KeepSubObjects(a, n, types, counts);
            }
            else
            {
                counts.ConvertedOnly++;
            }
        }

        // Native actors the conversion did not make into project actors, in Level.Actors order.
        foreach (SdcActor n in map.Actors.Skip(1).Where(n => native.ContainsKey(n.Name)))
        {
            if (AddMissing(p, n, map, schema, used, counts) is { } added)
            {
                KeepSubObjects(added, n, types, counts);
            }
        }

        if (counts.Lifted.Count > 0)
        {
            // What the lifted actors stand for as imported, so the T3D export can tell an edit they do not carry.
            var lifted = new SvJsonObject();
            lifted.Add("outfits", SvJsonNode.String(BakedContent.OutfitsHash(p)));
            lifted.Add("match", SvJsonNode.String(BakedContent.MatchHash(p)));
            lifted.Add(ConveyorHandoff.Key, ConveyorHandoff.Record(p));
            p.Extra.TryAdd(BakedContent.LiftedKey, lifted);
        }

        Report(p, map, counts, findings);
    }

    // ---- brushes --------------------------------------------------------------------------------------------------

    /// <summary>The CSG brushes of the file in Level.Actors order (the CSG order), without the builder brush (the
    /// first CSG_Active one, which adds nothing to the level), each with the actor it came from.</summary>
    public static List<(SdcActor Actor, Brush Brush)> ReadBrushes(SdcMap map, List<Finding> findings)
    {
        bool builderSeen = false;
        var brushes = new List<(SdcActor, Brush)>();
        foreach (SdcActor a in map.Actors.Where(SdcInfo.IsBrushClass))
        {
            PropSet props = PropSet.FromJson(a.Props);
            if (!builderSeen && BrushImport.OperOf(props["CsgOper"]) == 0)
            {
                builderSeen = true;
                continue;
            }

            if (BrushImport.ToBrush(a.Name, a.Class, props, ShapeOf(a.BrushPolys, map), findings) is { } brush)
            {
                brushes.Add((a, brush));
            }
        }

        return brushes;
    }

    private static void AddBrushes(MapProject p, SdcMap map, HashSet<string> used, Counts counts, List<Finding> findings)
    {
        var baked = new List<string>();
        foreach ((SdcActor a, Brush brush) in ReadBrushes(map, findings))
        {
            brush.Id = UniqueId(brush.Id, used, findings);
            var sdc = new SvJsonObject();
            sdc.Add("export", SvJsonNode.Integer(a.Ref));
            sdc.Add("hash", SvJsonNode.String(BakedContent.Hash(brush)));
            var src = new SvJsonObject();
            src.Add("sdc", sdc);
            if (p.Baked is not null)
            {
                src.Add("baked", SvJsonNode.True);
                baked.Add(brush.Id);
            }

            brush.Source = InteropValues.Compact(src);
            p.Brushes.Add(brush);
            counts.Brushes++;
            counts.Faces += brush.Shape.Faces.Count;
        }

        if (p.Baked is not null)
        {
            p.Baked.Brushes = baked;
        }
    }

    /// <summary>A brush model's Polys as faces, in the owner's local space: vertices, material, every poly flag, the
    /// texture base and axes (pans are folded into the base), both lightmap scales, item name and link.</summary>
    private static BrushShape? ShapeOf(IReadOnlyList<FPoly>? polys, SdcMap map)
    {
        if (polys is null)
        {
            return null;
        }

        var shape = new BrushShape();
        foreach (FPoly q in polys)
        {
            shape.Faces.Add(new BrushFace
            {
                Vertices = q.Vertices.Select(V).ToList(),
                Material = q.Material == 0 ? null : map.Package.PathOf(q.Material),
                Flags = unchecked((int)q.PolyFlags),
                TextureBase = V(q.Base),
                TextureU = V(q.TextureU),
                TextureV = V(q.TextureV),
                LightMapScale = q.LightMapScaleXbox,
                LightMapScalePsx2 = q.LightMapScalePsx2 == q.LightMapScaleXbox ? null : q.LightMapScalePsx2,
                ItemName = q.ItemName.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : q.ItemName,
                Link = q.Link,
            });
        }

        return shape;
    }

    private static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The source key holding, per localized property the import replaced, [the text it put, the text the .sdc
    /// stored].</summary>
    public const string StoredTextKey = "storedText";

    // ---- actors ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every stored property and the transform from the native decode; the converted actor keeps its id, class, source,
    /// editor state, and its localized text (the converter puts the build language's text over the stored one, as the
    /// game does). The stored text goes into the source (<see cref="StoredTextKey"/>) so an .sdc written back from the
    /// project keeps it while the text is unedited. A brush model's polys replace a shape recovered from colliders.
    /// </summary>
    private static void Overlay(Actor a, SdcActor n, SdcMap map, UeSchema? schema, Counts counts)
    {
        PropSet props = NativeProps(n, map);
        var stored = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in a.Props)
        {
            if (schema?.FindProperty(a.Class, key) is { Localized: true } && !Same(props[key], value))
            {
                if (props[key] is { } before)
                {
                    var pair = new SvJsonArray();
                    pair.Add(value);
                    pair.Add(before);
                    stored.Add(key, pair);
                }

                props.Set(key, value);
                counts.Localized++;
            }
        }

        if (stored.Count > 0)
        {
            SvJsonObject src = a.Source is { Length: > 0 } s && s[0] == '{' && SvJson.Parse(s) is SvJsonObject o ? o : new SvJsonObject();
            src.Add(StoredTextKey, stored);
            a.Source = InteropValues.Compact(src);
        }

        a.Location = Vec3.Zero;
        a.Rotation = default;
        a.DrawScale = 1;
        a.DrawScale3D = Vec3.One;
        a.PrePivot = Vec3.Zero;
        InteropValues.TakeTransform(props, a);
        a.Props = props;
        counts.Overlaid++;
        if (!SdcInfo.IsBrushClass(n) && ShapeOf(n.BrushPolys, map) is { Faces.Count: > 0 } shape)
        {
            counts.ShapesRecovered += a.Shape is null ? 0 : 1;
            a.Shape = shape;
            counts.Shapes++;
            BakedContent.MarkShape(a);
        }
    }

    /// <summary>A native actor as a project actor (every stored property and its transform), for actors read from
    /// another copy of the map (the light recovery's editor copies).</summary>
    public static Actor ActorFrom(SdcActor n, SdcMap map, string id)
    {
        var a = new Actor(id, n.Class);
        PropSet props = NativeProps(n, map);
        InteropValues.TakeTransform(props, a);
        a.Props = props;
        return a;
    }

    private static Actor? AddMissing(MapProject p, SdcActor n, SdcMap map, UeSchema? schema, HashSet<string> used, Counts counts)
    {
        if (SdcInfo.IsBrushClass(n))
        {
            return null; // brushes are CSG brushes, made above
        }

        if (n.Class.Equals("Engine.Camera", StringComparison.OrdinalIgnoreCase) || (schema?.IsA(n.Class, "Engine.Camera") ?? false))
        {
            counts.Cameras++;
            return null;
        }

        if (SdcMapReader.IsDeleted(n, schema))
        {
            counts.Deleted++;
            return null;
        }

        var a = new Actor(UniqueId(n.Name, used, null), n.Class);
        PropSet props = NativeProps(n, map);
        InteropValues.TakeTransform(props, a);
        a.Props = props;
        if (ShapeOf(n.BrushPolys, map) is { Faces.Count: > 0 } shape)
        {
            a.Shape = shape;
            counts.Shapes++;
        }

        var sdc = new SvJsonObject();
        sdc.Add("export", SvJsonNode.Integer(n.Ref));
        var src = new SvJsonObject();
        src.Add("sdc", sdc);
        bool ps2 = Int(n, "Platform", schema) == PlatformPs2Only;
        if (ps2 || SdcMapReader.LeftOutByCook(n, schema))
        {
            // Actors the cook drops (static lights and other actors with a light type, already in the baked lighting)
            // and PS2-only actors (not in the PC game).
            src.Add("cookedOut", SvJsonNode.True);
            a.Editor.Folder = CookedOutFolder;
            if (ps2)
            {
                counts.Ps2Only++;
            }
            else
            {
                counts.CookedOutLights++;
            }
        }
        else if (n.Category == SdcMap.Gameplay && IsLifted(n, map))
        {
            // The conversion made their settings native data (the project's match rules, outfits and conveyors): the
            // compile leaves them out as the conversion does, and the T3D export keeps them for the original editor.
            src.Add("lifted", SvJsonNode.True);
            a.Editor.Folder = LiftedFolder;
            counts.Lifted.Add(n.Name);
        }
        else
        {
            // Render actors the conversion keeps only in its render files (projectors, anti-portals, rain, cloth).
            a.Editor.Folder = n.Category == SdcMap.Gameplay ? "" : OtherRenderFolder;
            counts.Other++;
            if (n.Category == SdcMap.Gameplay)
            {
                counts.GameplayAdded.Add(n.Name);
            }
        }

        a.Source = InteropValues.Compact(src);
        // The shape as the file had it, so the round-trip lint can tell a face the original already had.
        BakedContent.MarkShape(a);
        p.Actors.Add(a);
        return a;
    }

    /// <summary>
    /// Keeps the actor's sub-objects (<see cref="SdcActor.SubObjects"/>) in its source as the T3D importer keeps the
    /// stock export's Begin Object blocks (<see cref="T3dImport.ObjectsKey"/>), so the T3D export writes them inside
    /// the actor as the stock editor does, and the original editor's import makes the particle emitters again.
    /// </summary>
    private static void KeepSubObjects(Actor a, SdcActor n, T3dTypes types, Counts counts)
    {
        if (n.SubObjects.Count == 0)
        {
            return;
        }

        var blocks = new SvJsonArray();
        foreach (SdcSubObject o in n.SubObjects)
        {
            var lines = new SvJsonArray();
            lines.Add(SvJsonNode.String($"Begin Object Class={InteropValues.ShortClass(o.Class)} Name={o.Export.Name}"));
            foreach (T3dField f in types.FromProps(o.Class, o.Props))
            {
                lines.Add(SvJsonNode.String(f.ToString()));
            }

            lines.Add(SvJsonNode.String("End Object"));
            blocks.Add(lines);
            counts.SubObjects++;
        }

        SvJsonObject src = a.Source is { Length: > 0 } s && s[0] == '{' && SvJson.Parse(s) is SvJsonObject o0 ? o0 : new SvJsonObject();
        src.TryAdd(T3dImport.ObjectsKey, blocks);
        a.Source = InteropValues.Compact(src);
    }

    /// <summary>
    /// The actor's stored properties as the cooked copy stores them, as the converter keeps them (mapconv/source.py
    /// _as_cooked): an editor copy holds the PC lighting instance under StaticMeshInstanceOnXBOX and a PS2 one, the cook
    /// writes the PC one as StaticMeshInstance only.
    /// </summary>
    private static PropSet NativeProps(SdcActor n, SdcMap map)
    {
        PropSet props = PropSet.FromJson(n.Props);
        if (map.Layout == SdcLayout.Editor)
        {
            SvJsonNode? pc = props["StaticMeshInstanceOnXBOX"];
            props.Remove("StaticMeshInstanceOnXBOX");
            props.Remove("StaticMeshInstanceOnPSX2");
            if (pc is not null)
            {
                props.Set("StaticMeshInstance", pc);
            }
        }

        return props;
    }

    private static long Int(SdcActor a, string prop, UeSchema? schema) =>
        (a.Props[prop] ?? schema?.DefaultOf(a.Class, prop)) is SvJsonNumber n && n.TryGetInt64(out long v) ? v : 0;

    private static bool Same(SvJsonNode? a, SvJsonNode? b) =>
        a is not null && b is not null && CanonicalJson.ToCanonicalString(a) == CanonicalJson.ToCanonicalString(b);

    private static string UniqueId(string id, HashSet<string> used, List<Finding>? findings)
    {
        string unique = id;
        for (int i = 1; !used.Add(unique); i++)
        {
            unique = id + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        if (unique != id)
        {
            findings?.Add(Finding.Warning("import.duplicateName", $"name {id} is used twice (names ignore case); the second is {unique}"));
        }

        return unique;
    }

    // ---- findings -------------------------------------------------------------------------------------------------

    private static void Report(MapProject p, SdcMap map, Counts c, List<Finding> findings)
    {
        UPackage pkg = map.Package;
        string layout = map.Layout == SdcLayout.Editor ? "an editor copy" : "a cooked copy";
        findings.Add(Finding.Info("import.sdcNative",
            $"{pkg.Name}.sdc is {layout} (package version {pkg.Version}, {pkg.Chunks} zlib chunks), read natively: "
            + $"{c.Brushes} brushes with {c.Faces} faces; {c.Shapes} volume, mover and anti-portal shapes from their brush models"
            + (c.ShapesRecovered > 0 ? $" (in place of {c.ShapesRecovered} shapes recovered from colliders)" : "")
            + $"; {c.Overlaid} actors given every stored property"
            + (c.SubObjects > 0 ? $"; {c.SubObjects} particle emitters (and other sub-objects) kept with their actors" : "")
            + (c.Localized > 0 ? $" ({c.Localized} localized texts kept in the build language)" : "")));
        if (c.CookedOutLights + c.Ps2Only > 0)
        {
            findings.Add(Finding.Info("import.sdcCookedOut",
                $"{c.CookedOutLights} static lights (and other actors with a light type) the cook drops and {c.Ps2Only} PS2-only actors "
                + $"were added from the file in {CookedOutFolder}; the compile leaves them out, as the cook does, but a bake here (a "
                + "relight, added brushes and meshes, or a rebuild without the baked render) still lights with the lights among them, "
                + "as the stock editor baked with them, without writing them"));

        }

        if (c.Other > 0)
        {
            findings.Add(Finding.Info("import.sdcRenderActors",
                $"{c.Other} actors the conversion keeps only in its render files (projectors, anti-portals, rain, cloth...) were added "
                + $"with every stored property, in {OtherRenderFolder}"));
        }

        if (c.Lifted.Count > 0)
        {
            findings.Add(Finding.Info("import.sdcLifted",
                $"{c.Lifted.Count} RE+ actors whose settings the conversion made native data (player-count rules, outfits, conveyor belts) "
                + $"were added from the file in {LiftedFolder}: {string.Join(", ", c.Lifted.Take(10))}{(c.Lifted.Count > 10 ? ", ..." : "")}; "
                + "the compile leaves them out (the map's match rules, outfits and converted conveyors build instead), the T3D export "
                + "keeps them",
                c.Lifted.Take(50).Select(ObjectRef.Actor).ToArray()));
        }

        if (c.GameplayAdded.Count > 0)
        {
            findings.Add(Finding.Warning("import.sdcGameplayMissing",
                $"{c.GameplayAdded.Count} gameplay actors were not in the converted map and were added from the file: "
                + string.Join(", ", c.GameplayAdded.Take(10)) + (c.GameplayAdded.Count > 10 ? ", ..." : ""),
                c.GameplayAdded.Take(50).Select(ObjectRef.Actor).ToArray()));
        }

        int deletedOutside = map.DeletedActors;
        // Only the kinds there were.
        string dropped = string.Join(", ", new[]
        {
            (c.Cameras, "cameras"),
            (c.Deleted, "deleted actors in Level.Actors"),
            (deletedOutside, "actors outside Level.Actors (deleted in the editor)"),
            (map.OrphanBrushes, "brushes outside Level.Actors"),
        }.Where(d => d.Item1 > 0).Select(d => $"{d.Item1} {d.Item2}"));
        if (dropped.Length > 0)
        {
            findings.Add(Finding.Info("import.sdcDropped", $"dropped as the stock editor's T3D export drops them: {dropped}"));
        }

        if (c.ConvertedOnly > 0)
        {
            findings.Add(Finding.Info("import.sdcConvertedOnly",
                $"{c.ConvertedOnly} actors come only from the conversion (none in the file's Level.Actors)"));
        }

        if (map.Stats.Raw > 0 || map.Stats.Unknown > 0)
        {
            findings.Add(Finding.Info("import.sdcUndeclared",
                $"{map.Stats.Unknown} stored properties are not in the class data (classes defined in the map, or removed properties): "
                + $"typed by their tags, {map.Stats.Raw} of them kept as raw bytes ({{\"$raw\"}}, which the compile and Map Check ignore)"));
        }

        List<SdcActor> problems = map.Actors.Where(a => a.Problem is not null).ToList();
        foreach (IGrouping<string, SdcActor> g in problems.GroupBy(a => a.Class, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            findings.Add(Finding.Info("import.sdcClass", $"{g.Count()} {g.Key} actors: {g.First().Problem}",
                g.Take(50).Select(a => ObjectRef.Actor(a.Name)).ToArray()));
        }

        if (pkg.Recovered)
        {
            findings.Add(Finding.Warning("import.sdcRecovered",
                "the package summary was never written (an autosave the editor did not finish); its tables were found by scanning the file"));
        }

        if (map.LevelRecord.Ges.Count != p.Ges.Count)
        {
            findings.Add(Finding.Warning("import.sdcGes",
                $"the file has {map.LevelRecord.Ges.Count} GEs, the converted map {p.Ges.Count}; the converted map's GEs were kept"));
        }
    }

    private sealed class Counts
    {
        public int Brushes { get; set; }

        public int Faces { get; set; }

        public int Shapes { get; set; }

        public int ShapesRecovered { get; set; }

        public int Overlaid { get; set; }

        public int SubObjects { get; set; }

        public int Localized { get; set; }

        public int CookedOutLights { get; set; }

        public int Ps2Only { get; set; }

        public int Other { get; set; }

        public List<string> GameplayAdded { get; } = new();

        public List<string> Lifted { get; } = new();

        public int Cameras { get; set; }

        public int Deleted { get; set; }

        public int ConvertedOnly { get; set; }
    }
}
