using System.Text.RegularExpressions;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>An actor of Level.Actors (or one the cook took out of it) with its decoded properties.</summary>
public sealed class SdcActor
{
    public SdcActor(int position, UExport export, string className)
    {
        Position = position;
        Export = export;
        Class = className;
    }

    /// <summary>The index in Level.Actors (0 is the LevelInfo); <see cref="OutsideLevel"/> for an actor the cook took
    /// out of Level.Actors.</summary>
    public int Position { get; }

    public const int OutsideLevel = -1;

    public UExport Export { get; }

    /// <summary>The export index + 1 (the objref other objects use).</summary>
    public int Ref => Export.Index + 1;

    public string Name => Export.Name;

    /// <summary><c>Package.Class</c>, spelled as the class data spells it when it knows the class.</summary>
    public string Class { get; }

    /// <summary>How OpenSCCT's converter sorts the actor (svmbuild <c>mapconv/source.py</c>): level, gameplay,
    /// render, editor (cameras, the Brush class) or skipped (deleted, PS2 only).</summary>
    public string Category { get; set; } = SdcMap.Gameplay;

    /// <summary>Every stored property as SVMap property JSON (see <see cref="UPropertyReader"/>).</summary>
    public SvJsonObject Props { get; set; } = new();

    /// <summary>Set when the class data could not type the properties (the actor's class is not in it, or a value
    /// did not read): the properties were then read by their tags alone.</summary>
    public string? Problem { get; set; }

    /// <summary>The actor's brush Model (objref), 0 when it has none.</summary>
    public int BrushModel { get; set; }

    /// <summary>Every export its stored properties refer to (objrefs, whatever they are), from the first read.</summary>
    public IReadOnlyList<int> ExportRefs { get; set; } = [];

    /// <summary>The faces of the brush Model's Polys, in the actor's local space; null without a brush.</summary>
    public IReadOnlyList<FPoly>? BrushPolys { get; set; }

    /// <summary>The map objects its export-object properties refer to (an Emitter's particle emitters), which the
    /// stock T3D export writes inside the actor as Begin Object blocks, with every stored property.</summary>
    public List<SdcSubObject> SubObjects { get; } = new();
}

/// <summary>A map object an actor owns through an export-object property, with its decoded properties.</summary>
public sealed record SdcSubObject(UExport Export, string Class, SvJsonObject Props);

/// <summary>An original map read natively: layout, level header, actors with properties, brush polys and GEs.</summary>
public sealed class SdcMap
{
    public const string Level = "level", Gameplay = "gameplay", Render = "render", Editor = "editor", Skipped = "skipped";

    public required UPackage Package { get; init; }

    public required SdcLayout Layout { get; init; }

    public required ULevel LevelRecord { get; init; }

    public required IReadOnlyList<SdcActor> Actors { get; init; }

    /// <summary>Brush exports not in Level.Actors that are not the cook's (deleted in the editor); ignored.</summary>
    public int OrphanBrushes { get; init; }

    /// <summary>Other actor exports not in Level.Actors that are not the cook's (deleted in the editor); ignored.</summary>
    public int DeletedActors { get; init; }

    /// <summary>Those ignored actor exports (orphan brushes and deleted actors), as read in pass 1: the writer keeps
    /// them in an editor copy and leaves them out of a cooked one.</summary>
    public IReadOnlyList<SdcActor> Ignored { get; init; } = [];

    public required PropertyStats Stats { get; init; }

    public string Name => Package.Name;
}

/// <summary>
/// Reads an original map package natively (SDC-FORMAT.md): the actors of Level.Actors in order with every stored
/// property, typed by the class data and with references written as the project writes them (<c>{"$actor"}</c> for
/// actors, <c>{"$asset":"Map.X"}</c> for other map objects, <c>{"$asset"}</c> / <c>{"$class"}</c> for imports), and the
/// Polys of every brush model. A cooked copy's actors the cook took out of Level.Actors (static lights, CSG brushes) are
/// read after them.
/// </summary>
public static class SdcMapReader
{
    private static readonly HashSet<string> s_editorClasses = new(StringComparer.OrdinalIgnoreCase) { "Engine.Camera", "Engine.Brush" };

    private static readonly HashSet<string> s_renderClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine.StaticMeshActor", "Engine.Light", "Engine.Spotlight", "Engine.SunLight", "Engine.AmbientSound", "Engine.Projector",
        "Engine.DynamicProjector", "Engine.AntiPortalActor", "Engine.ERainVolume", "SoftBody.ESBPatchActor", "SoftBody.ESBStripDoorActor",
        "SBase.SFogVolume",
    };

    /// <summary>A dotted object path. Names may hold spaces (Oilrig_SM.Third Floor.derrick).</summary>
    private static readonly Regex s_assetPath = new(@"^[^.]+(\.[^.]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex s_classPath = new(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private const int PlatformPs2Only = 1;

    /// <summary>The pass-1 stand-in for a reference to an export: its objref.</summary>
    private const string ExportKey = "$export";

    public static SdcMap Read(UPackage pkg, UeSchema? schema)
    {
        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        ULevel level = ULevelReader.Read(pkg, ULevelReader.Find(pkg));
        var actors = new List<SdcActor>();
        var byRef = new Dictionary<int, SdcActor>();
        for (int pos = 0; pos < level.Actors.Count; pos++)
        {
            int objref = level.Actors[pos];
            if (objref <= 0 || objref > pkg.Exports.Count || byRef.ContainsKey(objref))
            {
                continue;
            }

            UExport e = pkg.Export(objref);
            (string package, string cls) = pkg.ClassOf(e);
            string full = $"{package}.{cls}";
            var actor = new SdcActor(pos, e, schema is not null && Known(schema, full) ? schema.ResolveClass(full)! : full);
            actors.Add(actor);
            byRef[objref] = actor;
        }

        if (actors.Count == 0 || actors[0].Position != 0 || !InteropValues.ShortClass(actors[0].Class).EndsWith("LevelInfo", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{pkg.Name}: Level.Actors[0] is not a LevelInfo");
        }

        // Actor exports (they carry a state frame) outside Level.Actors: the cook takes static lights and CSG brushes
        // out of Level.Actors, and its save still writes those something refers to (or all of them, in some cooked
        // copies); they are the map's actors. Anything else outside Level.Actors was deleted in the editor.
        var outside = new List<SdcActor>();
        foreach (UExport e in pkg.Exports)
        {
            if ((e.Flags & UExport.HasStack) != 0 && e.Size > 0 && !byRef.ContainsKey(e.Index + 1))
            {
                (string package, string cls) = pkg.ClassOf(e);
                string full = $"{package}.{cls}";
                outside.Add(new SdcActor(SdcActor.OutsideLevel, e, schema is not null && Known(schema, full) ? schema.ResolveClass(full)! : full));
            }
        }

        // Pass 1 sorts the actors (deleted and PS2-only actors are told by their properties) and finds their brush
        // models; its references are bare export numbers. Pass 2 decodes again with references resolved by category.
        var scratch = new UPropertyReader(schema, (objref, _) => objref > 0 ? Ref(ExportKey, objref) : null);
        foreach (SdcActor a in actors.Concat(outside))
        {
            a.Props = Decode(pkg, a, scratch, schema);
            a.Category = CategoryOf(a, schema);
            a.BrushModel = a.Props["Brush"] is SvJsonObject { Count: 1 } b && b[ExportKey] is SvJsonNumber n && n.TryGetInt32(out int model) ? model : 0;
            var refs = new List<int>();
            CollectExports(a.Props, refs);
            a.ExportRefs = refs.Distinct().ToList();
        }

        // A deleted actor still in Level.Actors is dropped like those outside it, so references to it are None, as the
        // editor copy of the same save (which holds it outside Level.Actors) has them.
        foreach (SdcActor a in actors.Where(a => IsDeleted(a, schema)))
        {
            byRef.Remove(a.Ref);
        }

        int orphanBrushes = 0, deleted = 0;
        var ignored = new List<SdcActor>();
        foreach (SdcActor a in outside)
        {
            if (layout == SdcLayout.Cooked && LeftOutByCook(a, schema) && !IsDeleted(a, schema))
            {
                actors.Add(a);
                byRef[a.Ref] = a;
                continue;
            }

            ignored.Add(a);
            if (SdcInfo.IsBrushClass(a))
            {
                orphanBrushes++;
            }
            else
            {
                deleted++;
            }
        }

        var stats = new PropertyStats();
        var reader = new UPropertyReader(schema, (objref, isClass) => Resolve(pkg, byRef, objref, isClass, stats), stats);
        foreach (SdcActor a in actors)
        {
            a.Problem = null;
            List<int> subObjects = SubObjectRefs(pkg, a, schema);
            a.Props = Decode(pkg, a, reader, schema);
            foreach (int objref in subObjects)
            {
                UExport e = pkg.Export(objref);
                (string package, string cls) = pkg.ClassOf(e);
                string full = schema?.ResolveClass($"{package}.{cls}") ?? $"{package}.{cls}";
                a.SubObjects.Add(new SdcSubObject(e, full, DecodeObject(pkg, e, reader, schema is not null && Known(schema, full) ? full : null)));
            }
        }

        ReadBrushes(pkg, layout, actors);
        return new SdcMap
        {
            Package = pkg,
            Layout = layout,
            LevelRecord = level,
            Actors = actors,
            OrphanBrushes = orphanBrushes,
            DeletedActors = deleted,
            Ignored = ignored,
            Stats = stats,
        };
    }

    /// <summary>True when the stock editor leaves the actor out of the cooked copy's Level.Actors (<see cref="CookRule"/>).</summary>
    public static bool LeftOutByCook(SdcActor a, UeSchema? schema) =>
        CookRule.LeftOut(prop => a.Props[prop] ?? schema?.DefaultOf(a.Class, prop),
            schema is null ? SdcInfo.IsBrushClass(a) : schema.IsA(a.Class, "Engine.Brush") && !schema.IsA(a.Class, "Engine.Volume"));

    /// <summary>The world Model export of the level (ModelXBOX, else ModelPSX2), or null.</summary>
    public static UExport? WorldModel(SdcMap map) => map.LevelRecord.WorldModel > 0 ? map.Package.Export(map.LevelRecord.WorldModel) : null;

    private static SvJsonObject Decode(UPackage pkg, SdcActor a, UPropertyReader reader, UeSchema? schema)
    {
        string? cls = TypingClass(pkg, a, schema);
        if (cls is null)
        {
            a.Problem = $"class {a.Class} is not in the class data";
        }
        else if (!cls.Equals(a.Class, StringComparison.OrdinalIgnoreCase))
        {
            a.Problem = $"class {a.Class} is defined in the map: its properties are typed as {cls}'s, its own by their tags";
        }

        try
        {
            return ReadProps(pkg, a.Export, reader, cls);
        }
        catch (InvalidDataException ex) when (cls is not null)
        {
            // A value the class data cannot type: read the whole list by its tags instead, so nothing is dropped.
            a.Problem = ex.Message;
            return ReadProps(pkg, a.Export, reader, null);
        }
    }

    /// <summary>
    /// The map objects (not actors, not brush models) the actor's export-object properties refer to, from the pass-1
    /// decode (whose references are bare export numbers), in property order.
    /// </summary>
    private static List<int> SubObjectRefs(UPackage pkg, SdcActor a, UeSchema? schema)
    {
        var refs = new List<int>();
        string? cls = TypingClass(pkg, a, schema);
        foreach ((string key, SvJsonNode value) in a.Props)
        {
            if (cls is not null && schema!.FindProperty(cls, key) is { ExportObject: true })
            {
                CollectExports(value, refs);
            }
        }

        return refs.Distinct().Where(r => r <= pkg.Exports.Count && IsSubObject(pkg, pkg.Export(r))).ToList();
    }

    private static bool IsSubObject(UPackage pkg, UExport e) =>
        (e.Flags & UExport.HasStack) == 0 && e.Size > 0 && !pkg.ClassName(e).Equals("Model", StringComparison.OrdinalIgnoreCase);

    private static void CollectExports(SvJsonNode node, List<int> refs)
    {
        switch (node)
        {
            case SvJsonObject o when o[ExportKey] is SvJsonNumber n && n.TryGetInt32(out int objref):
                refs.Add(objref);
                break;
            case SvJsonObject o:
                foreach ((string _, SvJsonNode v) in o)
                {
                    CollectExports(v, refs);
                }

                break;
            case SvJsonArray list:
                foreach (SvJsonNode v in list)
                {
                    CollectExports(v, refs);
                }

                break;
        }
    }

    /// <summary>A non-actor object's properties, typed by its class (by its tags when the class data cannot).</summary>
    private static SvJsonObject DecodeObject(UPackage pkg, UExport e, UPropertyReader reader, string? cls)
    {
        try
        {
            return ReadProps(pkg, e, reader, cls);
        }
        catch (InvalidDataException) when (cls is not null)
        {
            return ReadProps(pkg, e, reader, null);
        }
    }

    /// <summary>The class whose declared properties type the actor's: its own class, or for a class defined in the map
    /// the nearest super class the class data knows.</summary>
    private static string? TypingClass(UPackage pkg, SdcActor a, UeSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        if (Known(schema, a.Class))
        {
            return a.Class;
        }

        int cls = a.Export.ClassRef;
        for (int guard = 0; cls > 0 && guard < 32; guard++)
        {
            int super = pkg.Export(cls).SuperRef;
            if (super < 0)
            {
                string path = pkg.PathOf(super);
                return Known(schema, path) ? schema.ResolveClass(path) : null;
            }

            cls = super;
        }

        return null;
    }

    private static bool Known(UeSchema schema, string fullClass) =>
        schema.ResolveClass(fullClass) is { } known && known.Equals(fullClass, StringComparison.OrdinalIgnoreCase);

    private static SvJsonObject ReadProps(UPackage pkg, UExport e, UPropertyReader reader, string? cls)
    {
        UReader r = pkg.Reader(e);
        UPropertyReader.SkipStateFrame(r, e.Flags);
        return reader.ReadObject(r, cls, e.Name);
    }

    private static string CategoryOf(SdcActor a, UeSchema? schema)
    {
        if (a.Position == 0)
        {
            return SdcMap.Level;
        }

        if (s_editorClasses.Contains(a.Class))
        {
            return SdcMap.Editor;
        }

        if (IsDeleted(a, schema) || Int(a, "Platform", schema) == PlatformPs2Only)
        {
            return SdcMap.Skipped;
        }

        return s_renderClasses.Contains(a.Class) ? SdcMap.Render : SdcMap.Gameplay;
    }

    /// <summary>True for an actor deleted in the editor that the save still wrote (bDeleteMe or bPendingDelete).</summary>
    public static bool IsDeleted(SdcActor a, UeSchema? schema) => Flag(a, "bDeleteMe", schema) || Flag(a, "bPendingDelete", schema);

    private static bool Flag(SdcActor a, string prop, UeSchema? schema) =>
        (a.Props[prop] ?? schema?.DefaultOf(a.Class, prop))?.Kind == SvJsonKind.True;

    private static long Int(SdcActor a, string prop, UeSchema? schema) =>
        (a.Props[prop] ?? schema?.DefaultOf(a.Class, prop)) is SvJsonNumber n && n.TryGetInt64(out long v) ? v : 0;

    /// <summary>A reference stored in the map, as the converter writes it (mapconv/source.py MapSource.resolve).</summary>
    private static SvJsonNode? Resolve(UPackage pkg, Dictionary<int, SdcActor> byRef, int objref, bool isClass, PropertyStats stats)
    {
        if (objref > pkg.Exports.Count || -objref > pkg.Imports.Count)
        {
            stats.Note($"reference {objref} is outside the tables");
            return null;
        }

        string path = pkg.PathOf(objref);
        if (objref < 0)
        {
            return DefaultRef(path, isClass);
        }

        if (byRef.TryGetValue(objref, out SdcActor? actor))
        {
            // Every actor is a project actor (render actors too: the compile writes those back as the map's assets).
            return Ref("$actor", actor.Name);
        }

        UExport e = pkg.Export(objref);
        if ((e.Flags & UExport.HasStack) != 0)
        {
            stats.Note($"reference to {e.Name}, a deleted actor or one outside Level.Actors, written as None");
            return null;
        }

        return isClass && path.Count(c => c == '.') == 1 ? Ref("$class", path) : Ref("$asset", path);
    }

    /// <summary>References outside the map: classes as $class, everything else with a dotted path as $asset.</summary>
    private static SvJsonNode? DefaultRef(string path, bool isClass)
    {
        if (isClass && s_classPath.IsMatch(path))
        {
            return Ref("$class", path);
        }

        return s_assetPath.IsMatch(path) ? Ref("$asset", path) : null;
    }

    private static SvJsonObject Ref(string key, string value) => Ref(key, SvJsonNode.String(value));

    private static SvJsonObject Ref(string key, int objref) => Ref(key, SvJsonNode.Integer(objref));

    private static SvJsonObject Ref(string key, SvJsonNode value)
    {
        var o = new SvJsonObject();
        o.Add(key, value);
        return o;
    }

    /// <summary>Every actor with a <c>Brush</c> property (brushes, volumes, movers, anti-portals): its Model's Polys.</summary>
    private static void ReadBrushes(UPackage pkg, SdcLayout layout, List<SdcActor> actors)
    {
        var models = new UModelReader(layout);
        foreach (SdcActor a in actors.Where(a => a.BrushModel > 0))
        {
            UExport me = pkg.Export(a.BrushModel);
            if (!pkg.ClassName(me).Equals("Model", StringComparison.OrdinalIgnoreCase) || me.Size <= 0)
            {
                a.BrushModel = 0;
                continue;
            }

            int polys = models.Read(pkg, me, summary: true).Polys;
            if (polys > 0 && polys <= pkg.Exports.Count && pkg.Export(polys).Size > 0)
            {
                a.BrushPolys = UPolysReader.Read(pkg, pkg.Export(polys));
            }
        }
    }
}
