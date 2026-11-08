using System.Numerics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Plans and writes one map package for <see cref="SdcWriter"/>. With a source package every source export keeps its
/// slot: the project's actors, brushes (and their models and polys) and the level are written from the project into
/// the slots they came from, the models and StaticMeshInstances are read and written again with their references
/// mapped, and everything else is copied as stored. New objects take the slots of deleted ones, then go at the end;
/// when deletions leave gaps, objects written from the project move into them (copied objects never move, so their
/// stored references stay right).
/// </summary>
internal sealed class SdcPackagePlan
{
    private const uint ActorFlags = 0x02070001;
    private const uint CsgBrushFlags = 0x02340001;
    private const uint CsgModelFlags = 0x00340001;
    private const uint ModelFlags = 0x00070001;
    private const uint LevelFlags = 0x00070001;
    private const int PlatformPs2 = 1;
    private const uint SummaryFlags = 0x00070004;

    private static readonly HashSet<string> s_engineState = new(StringComparer.OrdinalIgnoreCase)
    {
        "StaticMeshInstance", "StaticMeshInstanceOnXBOX", "StaticMeshInstanceOnPSX2",
    };

    private readonly MapProject _p;
    private readonly string _name;
    private readonly SdcWriteOptions _o;
    private readonly UeSchema? _schema;
    private readonly SdcLayout _layout;
    private readonly UPackage? _src;
    private readonly SdcMap? _srcMap;
    private readonly SdcLayout _srcLayout;
    private readonly bool _rebuildWorld;
    private readonly List<Finding> _findings;
    private readonly UPackageBuilder _b;
    private readonly SdcAssetImports _assets;
    private readonly List<Item> _items = new();
    private readonly Item?[] _bySrc;
    private readonly Dictionary<string, Item> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Item> _local = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _srcImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, UModelData> _models = new();
    private readonly Dictionary<Item, URelocatable> _relocated = new();
    private readonly HashSet<string> _unlitMeshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _levelModels = new();
    private UPropertyWriter _props = null!;
    private List<Item> _order = new();
    private Item _level = null!;
    private Item _levelInfo = null!;
    private readonly List<Item> _levelActors = new();
    private readonly HashSet<Item> _outsideLevel = new();
    private HashSet<string> _cookKeeps = new(StringComparer.OrdinalIgnoreCase);
    private List<Actor> _writes = new();
    private Item? _worldXbox;
    private Item? _worldPsx2;

    public SdcPackagePlan(MapProject p, string name, SdcWriteOptions o, UPackage? src, SdcMap? srcMap, List<Finding> findings, bool rebuildWorld = false)
    {
        _p = p;
        _name = name;
        _o = o;
        _schema = o.Schema;
        _layout = o.Layout;
        _src = src;
        _srcMap = srcMap;
        _srcLayout = srcMap?.Layout ?? SdcLayout.Editor;
        _rebuildWorld = rebuildWorld;
        _findings = findings;
        _b = new UPackageBuilder(name);
        _assets = new SdcAssetImports(o.GameRoot);
        _bySrc = new Item?[src?.Exports.Count ?? 0];
    }

    public SdcWriteStats Stats { get; private set; } = new(0, 0, 0, 0, 0, 0, false, null);

    /// <summary>Project actors not in the file as themselves: conveyors written as RE+ belts (or left out) and imported
    /// belts left out for the belts lowered in their place.</summary>
    public HashSet<string> HandedOff { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project ids written under another export name.</summary>
    public Dictionary<string, string> Renamed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One export of the plan.</summary>
    private sealed class Item
    {
        public required string Name;
        public required string Origin;
        public uint Flags;
        public int Src = -1;
        public bool Movable;
        public int Index = -1;
        public Func<int> ClassRef = () => 0;
        public Func<int> SuperRef = () => 0;
        public Func<int> Outer = () => 0;
        public Action<UWriter>? Write;
        public (byte[], int)? Copied;
        public int Ref => Index + 1;
    }

    private const string FromProject = "project", Copied = "copied", Rewritten = "rewritten", New = "new";

    public byte[] Build()
    {
        _props = new UPropertyWriter(_schema, Resolve, _src is null ? null : TagHints(_src, _srcMap!));
        _writes = WrittenActors();
        _cookKeeps = ReferencedCookedOut();
        if (_src is not null)
        {
            StoredOrder(_srcMap!, _props.StoredOrder);
            SeedTables(_src);
            _b.Trailer = TrailingZeros(_src.Data);
            _b.DataStart = _src.Exports.Where(e => e.Size > 0).Select(e => e.Offset).DefaultIfEmpty(0).Min();
            PlanFromSource(_src, _srcMap!);

            // The planning read the models and instances, so every name's stored second compact is known now.
            for (int i = 0; i < _src.Names.Count; i++)
            {
                _b.SetNameHash(i, _src.SecondCompact(i));
            }
        }
        else
        {
            PlanAuthored();
        }

        Order();
        foreach (Item it in _order)
        {
            _b.Exports.Add(new UExportSlot(it.Name, 0, it.Flags) { Write = it.Write, Copied = it.Copied });
        }

        // Table references are resolved once every item has its index.
        for (int i = 0; i < _order.Count; i++)
        {
            UExportSlot s = _b.Exports[i];
            s.ClassRef = _order[i].ClassRef();
            s.SuperRef = _order[i].SuperRef();
            s.Outer = _order[i].Outer();
        }

        byte[] data = _b.Build();
        if (_src is not null && data.Length == _src.Data.Length && data.AsSpan(0, GuidOffset).SequenceEqual(_src.Data.AsSpan(0, GuidOffset))
            && data.AsSpan(GuidOffset + 16).SequenceEqual(_src.Data.AsSpan(GuidOffset + 16)))
        {
            // Nothing changed: the map keeps its package GUID, so the file is the source byte for byte.
            _src.Data.AsSpan(GuidOffset, 16).CopyTo(data.AsSpan(GuidOffset));
        }

        foreach (string problem in _props.Problems.Distinct().Take(50))
        {
            _findings.Add(Finding.Warning("sdc.property", problem));
        }

        RemovedReferences();
        if (_unlitMeshes.Count > 0)
        {
            _findings.Add(Finding.Warning("sdc.meshNotLit",
                $"{_unlitMeshes.Count} static meshes are new since the import (or the map has no source): they have no lighting "
                + "instance (baked vertex light) until the map is rebuilt in the stock editor",
                _unlitMeshes.Take(50).Select(ObjectRef.Actor).ToArray()));
        }

        string[] untextured = _p.Brushes.Where(b => b.Shape.Faces.Any(f => IsPlaceholder(f.Material))).Select(b => b.Id).ToArray();
        if (untextured.Length > 0)
        {
            _findings.Add(Finding.Info("sdc.untextured",
                $"{untextured.Length} brushes have faces with the editor's grid placeholder: they are written with no material, which the game "
                + "and the stock editor draw with the default texture", untextured.Take(50).Select(ObjectRef.Brush).ToArray()));
        }

        Stats = new SdcWriteStats(_order.Count, _order.Count(i => i.Origin == FromProject), _order.Count(i => i.Origin == Copied),
            _order.Count(i => i.Origin == Rewritten), _order.Count(i => i.Origin == New), _bySrc.Count(i => i is null && _src is not null),
            _src is not null, _src is null ? null : _p.Source.File);
        return data;
    }

    // ---- tables -----------------------------------------------------------------------------------------------

    /// <summary>The source's names (with their stored second compact) and imports keep their indices, so copied
    /// export data still reads.</summary>
    private void SeedTables(UPackage src)
    {
        for (int i = 0; i < src.Names.Count; i++)
        {
            _b.AddName(src.Names[i], src.NameFlags[i], src.SecondCompact(i));
        }

        foreach (UImport imp in src.Imports)
        {
            _b.AddImport(imp);
        }

        for (int i = 0; i < src.Imports.Count; i++)
        {
            _srcImports.TryAdd(ImportPath(src, -(i + 1)) + "|" + src.Imports[i].ClassName, -(i + 1));
            _srcImports.TryAdd(ImportPath(src, -(i + 1)), -(i + 1));
        }
    }

    private const int GuidOffset = 36;

    /// <summary>The zero bytes the source ends with (64 after a normal save, none after an autosave).</summary>
    private static int TrailingZeros(byte[] data)
    {
        int n = 0;
        while (n < 64 && n < data.Length && data[data.Length - 1 - n] == 0)
        {
            n++;
        }

        return n;
    }

    private static string ImportPath(UPackage pkg, int objref) => pkg.PathOf(objref);

    /// <summary>The tags of properties the class data does not declare, from the source's actors (top level only).</summary>
    /// <summary>The order the source stores the properties of actors the class data cannot fully type, per class:
    /// every actor's stored order merged (a property first met goes after the one stored before it).</summary>
    private static void StoredOrder(SdcMap map, Dictionary<string, List<string>> orders)
    {
        foreach (SdcActor a in map.Actors.Where(a => a.Problem is not null))
        {
            if (!orders.TryGetValue(a.Class, out List<string>? order))
            {
                orders[a.Class] = order = new List<string>();
            }

            int after = -1;
            foreach ((string key, SvJsonNode _) in a.Props)
            {
                int at = order.FindIndex(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                {
                    order.Insert(after + 1, key);
                    at = after + 1;
                }

                after = Math.Max(after, at);
            }
        }
    }

    private Dictionary<(string, string), UTagHint> TagHints(UPackage src, SdcMap map)
    {
        // Names are case-blind in UE2 (AeroD's belt class declares 'speed').
        var hints = new Dictionary<(string, string), UTagHint>(IgnoreCasePair.Instance);
        foreach (SdcActor a in map.Actors.Where(a => a.Problem is not null))
        {
            UReader r = src.Reader(a.Export);
            try
            {
                UPropertyReader.SkipStateFrame(r, a.Export.Flags);
                while (r.Name() is var name && name != "None")
                {
                    byte info = r.U8();
                    int type = info & 0x0F;
                    string? structName = type == 10 ? r.Name() : null;
                    hints.TryAdd((a.Class, name), new UTagHint(type, structName));
                    // Only the tag is needed: values are skipped by their sizes, which leave out a name's second
                    // compact (a name value is read instead); a struct or array may hold names, so it ends the scan.
                    int code = (info >> 4) & 7;
                    int size = code switch { 0 => 1, 1 => 2, 2 => 4, 3 => 12, 4 => 16, 5 => r.U8(), 6 => r.U16(), _ => r.I32() };
                    if ((info & 0x80) != 0 && type != 3)
                    {
                        byte b = r.U8();
                        r.Skip(b < 0x80 ? 0 : (b & 0xC0) == 0x80 ? 1 : 3);
                    }

                    if (type is 10 or 9)
                    {
                        break;
                    }

                    if (type == 6)
                    {
                        r.Name();
                    }
                    else if (type != 3)
                    {
                        r.Skip(size);
                    }
                }
            }
            catch (InvalidDataException)
            {
                // A tag list that does not walk by sizes: the hints found so far are kept.
            }
        }

        // The simple properties the map's own classes declare (their property exports), for what no stored actor sets:
        // a belt lowered from a conveyor sets settings AeroD's stored belts leave at their defaults.
        foreach (UExport ce in src.Exports.Where(e => e.ClassRef == 0))
        {
            string cls = src.Name + "." + ce.Name;
            UExport? at = ce;
            for (int guard = 0; at is not null && guard < 32; guard++)
            {
                int outer = at.Index + 1;
                foreach (UExport pe in src.Exports.Where(e => e.Outer == outer))
                {
                    int type = src.ClassName(pe) switch
                    {
                        "ByteProperty" => 1, "IntProperty" => 2, "BoolProperty" => 3, "FloatProperty" => 4, "ObjectProperty" => 5,
                        "NameProperty" => 6, "StrProperty" => 13, _ => 0,
                    };
                    if (type > 0)
                    {
                        hints.TryAdd((cls, pe.Name), new UTagHint(type, null));
                    }
                }

                at = at.SuperRef > 0 ? src.Export(at.SuperRef) : null;
            }
        }

        return hints;
    }

    // ---- planning from a source package ----------------------------------------------------------------------

    private void PlanFromSource(UPackage src, SdcMap map)
    {
        // Every source export starts as a copy in its own slot.
        foreach (UExport e in src.Exports)
        {
            var it = new Item { Name = e.Name, Origin = Copied, Flags = e.Flags, Src = e.Index };
            int cls = e.ClassRef, super = e.SuperRef, outer = e.Outer;
            it.ClassRef = () => Remap(cls);
            it.SuperRef = () => Remap(super);
            it.Outer = () => Remap(outer);
            if (e.Size > 0)
            {
                it.Copied = (src.Data.AsSpan(e.Offset, e.Size).ToArray(), e.Offset);
            }

            _bySrc[e.Index] = it;
            string path = src.PathOf(e.Index + 1);
            _local.TryAdd(path[(path.IndexOf('.') + 1)..], it);
        }

        LevelModels(src, map);
        UExport levelExport = ULevelReader.Find(src);
        _level = _bySrc[levelExport.Index]!;
        _level.Origin = FromProject;
        _level.Movable = true;
        _level.Copied = null;
        _level.Write = WriteLevel;

        var matched = new HashSet<int>();
        _levelInfo = _bySrc[map.Actors[0].Export.Index]!;
        matched.Add(map.Actors[0].Export.Index);
        string levelInfoClass = map.Actors[0].Class;
        MakeActorWriter(_levelInfo, levelInfoClass, () => LevelInfoProps(), map.Actors[0]);
        _actors[_levelInfo.Name] = _levelInfo;

        // CSG brushes are editor-only: a cooked copy has none (their source exports go with the unmatched below).
        IReadOnlyList<Brush> brushes = _layout == SdcLayout.Editor ? _p.Brushes : [];
        List<Actor> actors = _writes.Where(a => !LeftOut(a)).ToList();
        (Dictionary<Brush, SdcActor> brushSource, Dictionary<Actor, SdcActor> actorSource) = MatchSources(map, brushes, actors, matched);
        foreach (Brush b in brushes)
        {
            Item item;
            if (brushSource.TryGetValue(b, out SdcActor? n))
            {
                item = _bySrc[n.Export.Index]!;
                item.Name = ExportName(b.Id, item);
                MakeActorWriter(item, n.Class, () => BrushProps(b, n), n);
                PlanShape(item, n, b.Shape, b, src);
            }
            else
            {
                item = NewActor(b.Id, "Engine.Brush", CsgBrushFlags, () => BrushProps(b, null));
                PlanNewShape(item, b.Shape, b, csg: true);
            }

            _actors[b.Id] = item;
        }

        foreach (Actor a in actors)
        {
            Item item;
            if (actorSource.TryGetValue(a, out SdcActor? n))
            {
                item = _bySrc[n.Export.Index]!;
                item.Name = ExportName(a.Id, item);
                MakeActorWriter(item, n.Class, () => ActorProps(a, n), n);
                if (a.Shape is not null)
                {
                    PlanShape(item, n, a.Shape, null, src);
                }
            }
            else
            {
                item = NewActor(a.Id, a.Class, ActorFlags, () => ActorProps(a, null));
                if (a.Shape is { Faces.Count: > 0 })
                {
                    PlanNewShape(item, a.Shape, null, csg: false);
                }
            }

            _actors[a.Id] = item;
            if (_layout == SdcLayout.Cooked && CookedOut(a))
            {
                _outsideLevel.Add(item);
            }
        }

        // Source actors the project does not hold: what the import leaves out by design stays (the builder brush,
        // cameras, actors deleted in the editor that the save still wrote); anything else was deleted in this editor.
        // A cooked copy keeps only the cameras (SDC-FORMAT.md section 13).
        bool builderSeen = false;
        foreach (SdcActor n in map.Actors.Skip(1))
        {
            if (matched.Contains(n.Export.Index))
            {
                continue;
            }

            bool builder = !builderSeen && SdcInfo.IsBrushClass(n) && BrushImport.OperOf(n.Props["CsgOper"]) == 0;
            builderSeen |= builder;
            bool camera = n.Class.Equals("Engine.Camera", StringComparison.OrdinalIgnoreCase) || (_schema?.IsA(n.Class, "Engine.Camera") ?? false);
            bool editorOnly = builder || SdcMapReader.IsDeleted(n, _schema) || n.Position == SdcActor.OutsideLevel;
            bool keep = camera || (editorOnly && _layout == SdcLayout.Editor);
            if (keep)
            {
                _actors.TryAdd(n.Name, _bySrc[n.Export.Index]!);
                continue;
            }

            Remove(n.Export.Index);
            if (n.BrushModel > 0)
            {
                RemoveModel(src, n.BrushModel);
            }
        }

        DropUnreached(src, map);

        // The world models and lighting instances are read and written again with their references mapped.
        ULevel level = map.LevelRecord;
        if (level.ModelXbox > 0)
        {
            _worldXbox = _bySrc[level.ModelXbox - 1];
            Rewrite(src, level.ModelXbox);
            if (_layout == SdcLayout.Cooked && _srcLayout == SdcLayout.Editor && _name.ToLowerInvariant() is "entry" or "lobby")
            {
                // The stock save empties these two menu maps' lightmap textures in the cooked copy (SDC-FORMAT.md section 7).
                SourceModel(src, src.Export(level.ModelXbox)).LightMapTextures.Clear();
            }
        }

        if (level.ModelPsx2 > 0)
        {
            if (_layout == SdcLayout.Editor)
            {
                _worldPsx2 = _bySrc[level.ModelPsx2 - 1];
                Rewrite(src, level.ModelPsx2);
            }
            else
            {
                RemoveModel(src, level.ModelPsx2);
            }
        }

        foreach (UExport e in src.ExportsOfClass("StaticMeshInstance").Where(e => e.Size > 0 && _bySrc[e.Index] is { Origin: Copied }))
        {
            RewriteInstance(src, e);
        }

        if (_layout != _srcLayout)
        {
            RemovePs2Instances(map);

            // Models serialize by layout: those no project object rewrote (shapes the import did not take) change too.
            foreach (UExport e in src.ExportsOfClass("Model").Where(e => e.Size > 0 && _bySrc[e.Index] is { Origin: Copied }))
            {
                Rewrite(src, e.Index + 1);
            }
        }

        LevelActorsFromSource(map);
        if (_rebuildWorld)
        {
            // The compiled source lost its builder brush and editor lightmap records. Retain its resource slots,
            // replacing the world with an editor model whose surfaces refer to the recovered brushes.
            _levelActors.Insert(1, PlanBuilderBrush());
            if (_worldXbox is not null) PlanWorld(_worldXbox);
            if (_worldPsx2 is not null) PlanWorld(_worldPsx2);
        }
        Relocate(src);
    }

    /// <summary>
    /// Lets the objects kept as stored change slots, so the slots of removed objects close up as a stock save closes
    /// them: an object whose every reference the trace finds (<see cref="URelocatable"/>) is written with its references
    /// mapped. An object it cannot read whole (map-local meshes, textures, sounds and classes, whose native data may
    /// point at the map's other assets) keeps its slot, and so do the map's other assets then; the level's own objects
    /// (actors, the objects inside them, soft bodies, the level summary) still move.
    /// </summary>
    private void Relocate(UPackage src)
    {
        // The models and Polys of the level's actors that no project object took (shapes the import left out) are read
        // and written again; other models (a map-local mesh's collision model) are the map's assets.
        foreach (Item it in _bySrc.OfType<Item>().Where(i => i.Origin == Copied && i.Copied is not null && _levelModels.Contains(i.Src)).ToList())
        {
            UExport e = src.Exports[it.Src];
            if (src.ClassName(e) == "Model")
            {
                Rewrite(src, e.Index + 1);
            }
            else if (src.ClassName(e) == "Polys")
            {
                RewritePolys(src, e, it);
            }
        }

        var opaque = new List<Item>();
        var readable = new Dictionary<Item, URelocatable>();
        foreach (Item it in _bySrc.OfType<Item>().Where(i => i.Origin == Copied && i.Copied is not null))
        {
            UExport e = src.Exports[it.Src];
            string? cls = e.ClassRef == 0 ? null : TypingClass(ClassPath(src, e));
            if (e.ClassRef != 0 && URelocatable.Read(src, e, cls, _schema) is { } rel)
            {
                readable[it] = rel;
            }
            else
            {
                opaque.Add(it);
            }
        }

        var pinned = opaque.ToHashSet();
        foreach (Item it in _bySrc.OfType<Item>().Where(i => i.Origin is Copied or Rewritten && !pinned.Contains(i)))
        {
            if (opaque.Count > 0 && !LevelContent(src, src.Exports[it.Src]))
            {
                // Read again for the other layout, it still keeps its slot.
                it.Movable = false;
                continue;
            }

            it.Movable = true;
            if (readable.TryGetValue(it, out URelocatable? rel))
            {
                it.Copied = null;
                it.Write = w => w.Bytes(rel.Write(Remap, w.Pos));
                _relocated[it] = rel;
            }
        }

        if (opaque.Count > 0)
        {
            _findings.Add(Finding.Info("sdc.keptSlots",
                $"{opaque.Count} of the map's own objects keep their export slots because their data is not read whole here "
                + $"({string.Join(", ", opaque.Select(i => src.ClassName(src.Exports[i.Src])).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))}); "
                + "the map's other assets stay with them"));
        }
    }

    /// <summary>The source's world models and its actors' brush models and Polys (by export index).</summary>
    private void LevelModels(UPackage src, SdcMap map)
    {
        IEnumerable<int> models = map.Actors.Concat(map.Ignored).Select(n => n.BrushModel)
            .Append(map.LevelRecord.ModelXbox).Append(map.LevelRecord.ModelPsx2);
        foreach (int model in models.Where(m => m > 0))
        {
            UExport me = src.Export(model);
            _levelModels.Add(me.Index);
            if (ModelPolys(src, me) is > 0 and var polys)
            {
                _levelModels.Add(polys - 1);
            }
        }
    }

    /// <summary>Notes references in objects kept as stored that point at objects this write leaves out: they are
    /// written as None (the cook does the same with what it drops).</summary>
    private void RemovedReferences()
    {
        int count = 0;
        var examples = new List<string>();
        foreach ((Item it, URelocatable rel) in _relocated)
        {
            foreach (int objref in rel.References.Where(r => r > 0 && r <= _bySrc.Length && _bySrc[r - 1] is null))
            {
                count++;
                if (examples.Count < 6)
                {
                    examples.Add($"{it.Name} to {_src!.Exports[objref - 1].Name}");
                }
            }
        }

        if (count > 0)
        {
            _findings.Add(Finding.Info("sdc.removedRefs",
                $"{count} references in objects kept as stored point at objects this write leaves out and are written as None "
                + $"(e.g. {string.Join(", ", examples)})"));
        }
    }

    private static string ClassPath(UPackage pkg, UExport e)
    {
        (string package, string cls) = pkg.ClassOf(e);
        return package.Length > 0 ? package + "." + cls : cls;
    }

    /// <summary>True for an object of the level rather than one of the map's assets: an actor, an object inside one
    /// (emitters' parts), a soft body's state, the level summary.</summary>
    private bool LevelContent(UPackage src, UExport e)
    {
        if (_levelModels.Contains(e.Index))
        {
            return true;
        }

        for (UExport? x = e; x is not null; x = x.Outer > 0 ? src.Export(x.Outer) : null)
        {
            string cls = ClassPath(src, x);
            if ((x.Flags & UExport.HasStack) != 0 || (_schema?.IsA(cls, "Engine.Actor") ?? false)
                || src.ClassName(x) is "ESBPatch" or "ESBStripDoor" or "LevelSummary" or "StaticMeshInstance")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The source actor each project brush and actor came from: the one of its id (the import keeps the export names as
    /// ids), else, for one renamed since, the export its import provenance names. Only source actors the import made
    /// project objects from count (not the builder brush, cameras or deleted actors), and each matches once, so a
    /// duplicate (which copies its original's provenance) is a new object.
    /// </summary>
    private (Dictionary<Brush, SdcActor>, Dictionary<Actor, SdcActor>) MatchSources(SdcMap map, IReadOnlyList<Brush> brushes,
        IReadOnlyList<Actor> actors, HashSet<int> matched)
    {
        HashSet<int> notImported = NotImported(map);
        var byName = new Dictionary<string, SdcActor>(StringComparer.OrdinalIgnoreCase);
        var byRef = new Dictionary<int, SdcActor>();
        foreach (SdcActor n in map.Actors.Skip(1).Where(n => !notImported.Contains(n.Export.Index)))
        {
            byName.TryAdd(n.Name, n);
            byRef.TryAdd(n.Ref, n);
        }

        var brushSource = new Dictionary<Brush, SdcActor>();
        var actorSource = new Dictionary<Actor, SdcActor>();
        bool Take(SdcActor? n, bool brush) => n is not null && SdcInfo.IsBrushClass(n) == brush && matched.Add(n.Export.Index);
        foreach (Brush b in brushes)
        {
            if (byName.GetValueOrDefault(b.Id) is { } n && Take(n, brush: true))
            {
                brushSource[b] = n;
            }
        }

        foreach (Actor a in actors)
        {
            if (byName.GetValueOrDefault(a.Id) is { } n && Take(n, brush: false))
            {
                actorSource[a] = n;
            }
        }

        foreach (Brush b in brushes.Where(b => !brushSource.ContainsKey(b)))
        {
            if (Provenance(b.Source, byName, byRef) is { } n && Take(n, brush: true))
            {
                brushSource[b] = n;
            }
        }

        foreach (Actor a in actors.Where(a => !actorSource.ContainsKey(a)))
        {
            if (Provenance(a.Source, byName, byRef) is { } n && n.Class.Equals(a.Class, StringComparison.OrdinalIgnoreCase) && Take(n, brush: false))
            {
                actorSource[a] = n;
            }
        }

        return (brushSource, actorSource);
    }

    /// <summary>The source actors the import makes no project object from: the builder brush (the first CSG_Active
    /// brush), cameras and deleted actors (SdcOverlay).</summary>
    private HashSet<int> NotImported(SdcMap map)
    {
        var left = new HashSet<int>();
        bool builderSeen = false;
        foreach (SdcActor n in map.Actors.Skip(1))
        {
            bool builder = !builderSeen && SdcInfo.IsBrushClass(n) && BrushImport.OperOf(n.Props["CsgOper"]) == 0;
            builderSeen |= builder;
            if (builder || IsCamera(n) || SdcMapReader.IsDeleted(n, _schema))
            {
                left.Add(n.Export.Index);
            }
        }

        return left;
    }

    private bool IsCamera(SdcActor n) =>
        n.Class.Equals("Engine.Camera", StringComparison.OrdinalIgnoreCase) || (_schema?.IsA(n.Class, "Engine.Camera") ?? false);

    /// <summary>The source actor an object's import provenance names: <c>{"export": n}</c> (the converter's actors),
    /// <c>{"sdc": {"export": n}}</c> (brushes and actors the native reader added) or <c>{"actor": name}</c> (render
    /// actors).</summary>
    private static SdcActor? Provenance(string? source, Dictionary<string, SdcActor> byName, Dictionary<int, SdcActor> byRef)
    {
        if (source is not { Length: > 0 } || source[0] != '{' || SvJson.Parse(source) is not SvJsonObject o)
        {
            return null;
        }

        SvJsonNode? export = o["export"] ?? (o["sdc"] as SvJsonObject)?["export"];
        if (export is SvJsonNumber num && num.TryGetInt32(out int objref))
        {
            return byRef.GetValueOrDefault(objref);
        }

        return o["actor"] is SvJsonString name ? byName.GetValueOrDefault(name.Value) : null;
    }

    /// <summary>The export name of a project object: its id, unless another of the package's objects (one the import
    /// left out, such as the builder brush) already has that name.</summary>
    private string ExportName(string id, Item item)
    {
        if (!_local.TryGetValue(id, out Item? holder) || holder == item)
        {
            _local[id] = item;
            return id;
        }

        string name = FreeName(id);
        _local[name] = item;
        Renamed[id] = name;
        return name;
    }

    /// <summary>
    /// A save writes what the level reaches. The lighting instances of actors deleted here go with them, and in a
    /// cooked copy so do the actor exports the import ignores (deleted in the editor; an editor copy keeps them, as
    /// copies), unless a written actor still refers to them: the cooked twins keep deleted zones that actors' regions
    /// name, and static lights a mover names.
    /// </summary>
    private void DropUnreached(UPackage src, SdcMap map)
    {
        var bySrc = new Dictionary<int, SdcActor>();
        foreach (SdcActor n in map.Actors.Concat(map.Ignored))
        {
            bySrc[n.Ref] = n;
        }

        var ignored = new HashSet<SdcActor>(map.Ignored);
        bool cooked = _layout == SdcLayout.Cooked;
        var reached = new HashSet<int>();
        var queue = new Queue<SdcActor>(bySrc.Values.Where(n => _bySrc[n.Export.Index] is not null && !(cooked && ignored.Contains(n))));
        foreach (SdcActor n in queue)
        {
            reached.Add(n.Ref);
        }

        while (queue.TryDequeue(out SdcActor? n))
        {
            foreach (int objref in n.ExportRefs.Where(reached.Add))
            {
                if (bySrc.TryGetValue(objref, out SdcActor? next) && _bySrc[next.Export.Index] is not null)
                {
                    queue.Enqueue(next);
                }
            }
        }

        foreach (SdcActor n in cooked ? map.Ignored.Where(n => !reached.Contains(n.Ref)) : [])
        {
            Remove(n.Export.Index);
            if (n.BrushModel > 0)
            {
                RemoveModel(src, n.BrushModel);
            }
        }

        foreach (UExport e in src.ExportsOfClass("StaticMeshInstance").Where(e => !reached.Contains(e.Index + 1)))
        {
            Remove(e.Index);
        }
    }

    private void Remove(int srcIndex)
    {
        _bySrc[srcIndex] = null;
    }

    /// <summary>Removes a brush model and its Polys (each belongs to one brush).</summary>
    private void RemoveModel(UPackage src, int modelRef)
    {
        UExport me = src.Export(modelRef);
        if (_bySrc[me.Index] is null)
        {
            return;
        }

        int polys = ModelPolys(src, me);
        Remove(me.Index);
        if (polys > 0)
        {
            Remove(polys - 1);
        }
    }

    private int ModelPolys(UPackage src, UExport me)
    {
        try
        {
            return new UModelReader(_srcLayout).Read(src, me, summary: true).Polys;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>A model read from the source and written again in the target layout with its references mapped.</summary>
    private void Rewrite(UPackage src, int modelRef)
    {
        UExport me = src.Export(modelRef);
        Item? it = _bySrc[me.Index];
        if (it is null || me.Size <= 0)
        {
            return;
        }

        UModelData m = SourceModel(src, me);
        it.Origin = Rewritten;
        it.Movable = true;
        it.Copied = null;
        it.Write = w => m.Write(w, _layout, Remap);
    }

    private UModelData SourceModel(UPackage src, UExport me)
    {
        if (!_models.TryGetValue(me.Index, out UModelData? m))
        {
            m = UModelData.Read(src, me, _srcLayout);
            _models[me.Index] = m;
        }

        return m;
    }

    private void RewritePolys(UPackage src, UExport e, Item it)
    {
        List<FPoly> polys;
        try
        {
            polys = UPolysReader.Read(src, e);
        }
        catch (InvalidDataException)
        {
            return; // kept as a copy
        }

        it.Origin = Rewritten;
        it.Movable = true;
        it.Copied = null;
        it.Write = w => UModelData.WritePolys(w, polys, Remap);
    }

    private void RewriteInstance(UPackage src, UExport e)
    {
        Item it = _bySrc[e.Index]!;
        USmiData smi;
        try
        {
            smi = USmiData.Read(src, e, _srcLayout);
        }
        catch (InvalidDataException)
        {
            return; // kept as a copy
        }

        it.Origin = Rewritten;
        it.Movable = true;
        it.Copied = null;
        it.Write = w => smi.Write(w, _layout, Remap);
    }

    /// <summary>The brush model and Polys of an actor from the source: the Polys are written from the project's
    /// faces, the model from the source with its references mapped. When the faces changed, its bounds are made again,
    /// and so is a volume's or mover's own BSP; both are in the faces' own space, so a move or turn changes neither.</summary>
    private void PlanShape(Item owner, SdcActor n, BrushShape shape, Brush? brush, UPackage src)
    {
        if (n.BrushModel <= 0 || _bySrc[n.BrushModel - 1] is not { } modelItem)
        {
            PlanNewShape(owner, shape, brush, csg: brush is not null);
            return;
        }

        UExport me = src.Export(n.BrushModel);
        UModelData model = SourceModel(src, me);
        int polysRef = model.Polys;
        IReadOnlyList<FPoly> srcPolys = n.BrushPolys ?? [];
        Item? polysItem = polysRef > 0 ? _bySrc[polysRef - 1] : null;
        List<Vector3[]> verts = shape.Faces.Select(FaceVertices).ToList();
        bool same = verts.Count == srcPolys.Count && verts.Zip(srcPolys).All(p => p.First.SequenceEqual(p.Second.Vertices));

        // Materials resolve when written: a map-local texture has its export slot only then.
        var polys = new Lazy<List<FPoly>>(() => shape.Faces.Select((f, i) => ToPoly(f, i < srcPolys.Count ? srcPolys[i] : null)).ToList());
        bool rebuild = !same && model.Nodes.Count > 0;
        if (!same)
        {
            (model.BoxMin, model.BoxMax, model.Sphere) = Bounds(verts);
        }

        modelItem.Origin = FromProject;
        modelItem.Movable = true;
        modelItem.Copied = null;
        modelItem.Write = w =>
        {
            if (rebuild)
            {
                // The model's own BSP, made again from the new faces (its references are the new package's).
                UModelData m = SdcBrushBsp.Build(polys.Value, owner.Name);
                (m.BoxMin, m.BoxMax, m.Sphere, m.BoxValid) = (model.BoxMin, model.BoxMax, model.Sphere, true);
                m.Polys = polysItem?.Ref ?? 0;
                m.Write(w, _layout, Identity);
            }
            else
            {
                model.Write(w, _layout, Remap);
            }
        };
        if (polysItem is not null)
        {
            polysItem.Origin = FromProject;
            polysItem.Movable = true;
            polysItem.Copied = null;
            polysItem.Write = w => UModelData.WritePolys(w, polys.Value, Identity);
        }
    }

    // ---- planning a map with no source -------------------------------------------------------------------------

    private void PlanAuthored()
    {
        _levelInfo = NewActor(T3d.T3dExport.LevelInfoName(_p), "Engine.LevelInfo", ActorFlags, AuthoredLevelInfoProps);
        _actors[_levelInfo.Name] = _levelInfo;
        PlanSummary();
        _level = NewItem("MyLevel", New, LevelFlags);
        _level.ClassRef = () => _b.ImportClass("Engine", "Level");
        _level.Write = WriteLevel;
        _levelActors.Add(_levelInfo);

        // CSG brushes are editor-only (a cooked copy has none).
        if (_layout == SdcLayout.Editor)
        {
            PlanAuthoredBrushes();
        }

        foreach (Actor a in _writes)
        {
            if (LeftOut(a))
            {
                continue;
            }

            Item item = NewActor(a.Id, a.Class, ActorFlags, () => ActorProps(a, null));
            if (a.Shape is { Faces.Count: > 0 })
            {
                PlanNewShape(item, a.Shape, null, csg: false);
            }

            _actors[a.Id] = item;
            if (_layout == SdcLayout.Cooked && CookedOut(a))
            {
                _outsideLevel.Add(item);
            }
            else
            {
                _levelActors.Add(item);
            }
        }

        _worldXbox = NewItem(Unique("Model"), New, ModelFlags);
        _worldXbox.ClassRef = () => _b.ImportClass("Engine", "Model");
        PlanWorld(_worldXbox);
    }

    private void PlanWorld(Item worldItem)
    {
        if (worldItem.Src >= 0) worldItem.Origin = FromProject;
        worldItem.Movable = true;
        worldItem.Copied = null;
        // Built when written: its materials and actors resolve to their slots then.
        var refs = new SdcWorldRefs(FaceMaterial, id => _actors.TryGetValue(id, out Item? a) ? a.Ref : 0,
            () => worldItem.Ref, WorldPoly);
        var world = new Lazy<UModelData>(() =>
        {
            _o.Progress?.Invoke("building the world: CSG, BSP, zones and lightmaps");
            return SdcWorldModel.Build(_p, _o.ContentRoot, _findings, _layout, refs);
        });
        worldItem.Write = w => world.Value.Write(w, _layout, Identity);
    }

    /// <summary>The LevelSummary every stock map has (the LevelInfo's Summary): the map's title and author, which the
    /// game's map list reads without loading the level.</summary>
    private void PlanSummary()
    {
        Item summary = NewItem("LevelSummary", FromProject, SummaryFlags);
        summary.ClassRef = () => _b.ImportClass("Engine", "LevelSummary");
        string? typing = TypingClass("Engine.LevelSummary");
        summary.Write = w => _props.WriteObject(w, typing, "Engine.LevelSummary",
            [new("Title", SvJsonNode.String(Title())), new("Author", SvJsonNode.String(_p.LevelInfo.GetString("Author") ?? ""))], summary.Name);
    }

    private string Title() => _p.LevelInfo.GetString("Title") is { Length: > 0 } t ? t : Svmap.IdentityImport.ImportedTitle(_p);

    /// <summary>A new map's LevelInfo: the project's settings with the title and the summary every stock map stores.</summary>
    private List<KeyValuePair<string, SvJsonNode>> AuthoredLevelInfoProps()
    {
        List<KeyValuePair<string, SvJsonNode>> props = LevelInfoProps();
        if (!props.Any(kv => kv.Key == "Title"))
        {
            props.Add(new("Title", SvJsonNode.String(Title())));
        }

        if (!props.Any(kv => kv.Key == "Summary"))
        {
            props.Add(new("Summary", ItemRef("LevelSummary")));
        }

        return props;
    }

    /// <summary>The builder brush (Level.Actors[1], CSG_Active, an untextured 256 cube as in a new map), then the
    /// project's brushes in CSG order.</summary>
    private void PlanAuthoredBrushes()
    {
        _levelActors.Add(PlanBuilderBrush());
        foreach (Brush b in _p.Brushes)
        {
            Item item = NewActor(b.Id, "Engine.Brush", CsgBrushFlags, () => BrushProps(b, null));
            PlanNewShape(item, b.Shape, b, csg: true);
            _actors[b.Id] = item;
            _levelActors.Add(item);
        }
    }

    private Item PlanBuilderBrush()
    {
        var builderBrush = new Brush(Unique("Brush")) { Oper = CsgOper.Add, Shape = Geometry.Box.Shape(new Vec3(256, 256, 256)) };
        foreach (BrushFace f in builderBrush.Shape.Faces)
        {
            f.Material = null;
        }

        Item builder = NewActor(builderBrush.Id, "Engine.Brush", CsgBrushFlags, () =>
        {
            List<KeyValuePair<string, SvJsonNode>> props = BrushProps(builderBrush, null);
            props.RemoveAll(kv => kv.Key == "CsgOper");
            return props;
        });
        PlanNewShape(builder, builderBrush.Shape, builderBrush, csg: true);
        _actors[builder.Name] = builder;
        return builder;
    }

    // ---- new objects ------------------------------------------------------------------------------------------

    private Item NewItem(string name, string origin, uint flags)
    {
        var it = new Item { Name = name, Origin = origin, Flags = flags, Movable = true };
        _items.Add(it);
        _local.TryAdd(name, it);
        return it;
    }

    private Item NewActor(string id, string cls, uint flags, Func<List<KeyValuePair<string, SvJsonNode>>> props)
    {
        string name = id;
        if (_local.ContainsKey(id))
        {
            name = FreeName(id);
            Renamed[id] = name;
        }

        Item it = NewItem(name, _src is null ? FromProject : New, flags);
        Func<int> classRef = ClassRefOf(cls);
        it.ClassRef = classRef;
        string? typing = TypingClass(cls);
        it.Write = w =>
        {
            UPropertyWriter.StateFrame(w, classRef());
            List<KeyValuePair<string, SvJsonNode>> actorProps = props();
            // Actor::Level is a serialized LevelInfo reference, not something the game fills on load.
            // Fresh actors have no imported engine state; leaving it null crashes IsNetRelevantFor.
            actorProps.RemoveAll(kv => kv.Key.Equals("Level", StringComparison.OrdinalIgnoreCase));
            actorProps.Add(new("Level", ItemRef(_levelInfo.Name)));
            _props.WriteObject(w, typing, cls, WithoutDefaults(actorProps, typing ?? cls, null), id);
        };
        return it;
    }

    /// <summary>A source actor written from the project: its class and state frame as stored.</summary>
    private void MakeActorWriter(Item it, string cls, Func<List<KeyValuePair<string, SvJsonNode>>> props, SdcActor n)
    {
        UExport e = n.Export;
        int latent = Latent(e);
        string? typing = TypingClass(cls);
        it.Origin = FromProject;
        it.Movable = true;
        it.Copied = null;
        it.Write = w =>
        {
            UPropertyWriter.StateFrame(w, Remap(e.ClassRef), latent);
            var actorProps = KeepDeletedRefs(props(), n);
            if (_rebuildWorld)
            {
                actorProps.RemoveAll(kv => kv.Key.Equals("Level", StringComparison.OrdinalIgnoreCase));
                actorProps.Add(new("Level", ItemRef(_levelInfo.Name)));
            }
            _props.WriteObject(w, typing, cls, WithoutDefaults(actorProps, typing ?? cls, n), n.Name, n.Props.Select(kv => kv.Key).ToList());
        };
    }

    /// <summary>
    /// The engine stores only the properties that differ from the class defaults; the project may hold a default
    /// value (a converted LevelInfo lists every gameplay setting). Such a value is left out unless the source actor
    /// stored it.
    /// </summary>
    private List<KeyValuePair<string, SvJsonNode>> WithoutDefaults(List<KeyValuePair<string, SvJsonNode>> props, string cls, SdcActor? n) =>
        props.Where(kv => n?.Props[kv.Key] is not null || !SameValue(kv.Value, DefaultValue(cls, kv.Key))).ToList();

    /// <summary>A property's class default: the class data's, else the zero of its type (unknown properties: null).</summary>
    private SvJsonNode? DefaultValue(string cls, string key)
    {
        if (_schema?.DefaultOf(cls, key) is { } d)
        {
            return d;
        }

        return _schema?.FindProperty(cls, key) is { ArrayDim: <= 1 } prop ? prop.Type switch
        {
            "byte" or "int" => SvJsonNode.Integer(0),
            "float" => SvJsonNode.Float(0),
            "bool" => SvJsonNode.False,
            "string" => SvJsonNode.String(""),
            "object" or "class" => SvJsonNode.Null,
            _ => null,
        } : null;
    }

    /// <summary>Two property values that store the same bytes (numbers compared as the f32 or i32 they are stored as).</summary>
    private static bool SameValue(SvJsonNode? a, SvJsonNode? b)
    {
        switch (a, b)
        {
            case (null, _) or (_, null):
                return false;
            case (SvJsonNumber x, SvJsonNumber y):
                return (float)PropValues.Num(x) == (float)PropValues.Num(y);
            case (SvJsonObject x, SvJsonObject y):
                return x.Count == y.Count && x.All(kv => SameValue(kv.Value, y[kv.Key]));
            case (SvJsonArray x, SvJsonArray y):
                return x.Count == y.Count && x.Zip(y).All(p => SameValue(p.First, p.Second));
            default:
                return a.Kind == b.Kind && CanonicalJson.ToCanonicalString(a) == CanonicalJson.ToCanonicalString(b);
        }
    }

    /// <summary>
    /// The stock editor leaves references to actors deleted later in place, and the import reads them as None (the
    /// project has no such actor). Where the project still holds that None, the stored reference is written back
    /// (the deleted actor stays in the package), so an unedited actor keeps its bytes.
    /// </summary>
    private List<KeyValuePair<string, SvJsonNode>> KeepDeletedRefs(List<KeyValuePair<string, SvJsonNode>> props, SdcActor n)
    {
        var reader = new UPropertyReader(_schema, (objref, _) => objref > 0 ? SourceRef(objref) : null);
        UReader r = _src!.Reader(n.Export);
        UPropertyReader.SkipStateFrame(r, n.Export.Flags);
        SvJsonObject stored = reader.ReadObject(r, n.Class, n.Name);
        return props.Select(kv => new KeyValuePair<string, SvJsonNode>(kv.Key, KeepDeleted(kv.Value, n.Props[kv.Key], stored[kv.Key]))).ToList();
    }

    private static SvJsonNode KeepDeleted(SvJsonNode value, SvJsonNode? imported, SvJsonNode? stored)
    {
        switch (value)
        {
            case { Kind: SvJsonKind.Null } when imported is null or { Kind: SvJsonKind.Null } && stored is SvJsonObject so && so[SourceKey] is not null:
                return stored;
            case SvJsonArray va when imported is SvJsonArray ia && stored is SvJsonArray sa && va.Count == sa.Count && ia.Count == sa.Count:
                var array = new SvJsonArray();
                for (int i = 0; i < va.Count; i++)
                {
                    array.Add(KeepDeleted(va[i], ia[i], sa[i]));
                }

                return array;
            case SvJsonObject vo when imported is SvJsonObject io && stored is SvJsonObject so && so[SourceKey] is null:
                var obj = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> kv in vo)
                {
                    obj.Add(kv.Key, KeepDeleted(kv.Value, io[kv.Key], so[kv.Key]));
                }

                return obj;
            default:
                return value;
        }
    }

    private const string SourceKey = "\u0001src";

    private static SvJsonObject SourceRef(int objref)
    {
        var o = new SvJsonObject();
        o.Add(SourceKey, SvJsonNode.Integer(objref));
        return o;
    }

    /// <summary>The stray latent-action value of a source actor's state frame (the stock editor leaves whatever was
    /// there).</summary>
    private int Latent(UExport e)
    {
        UReader r = _src!.Reader(e);
        r.ObjRef();
        r.ObjRef();
        r.U64();
        return r.I32();
    }

    /// <summary>A new brush model and Polys for an actor's or brush's faces (a CSG brush's are editor-only).</summary>
    private void PlanNewShape(Item owner, BrushShape shape, Brush? brush, bool csg)
    {
        if (_layout == SdcLayout.Cooked && csg)
        {
            return;
        }

        List<Vector3[]> verts = shape.Faces.Select(FaceVertices).ToList();
        var polys = new Lazy<List<FPoly>>(() => shape.Faces.Select(f => ToPoly(f, null)).ToList());
        Item model = NewItem(Unique("Model" + owner.Name), New, csg ? CsgModelFlags : ModelFlags);
        Item polysItem = NewItem(Unique("Polys" + owner.Name), New, csg ? CsgModelFlags : ModelFlags);
        model.ClassRef = () => _b.ImportClass("Engine", "Model");
        polysItem.ClassRef = () => _b.ImportClass("Engine", "Polys");
        (Vector3 min, Vector3 max, Vector4 sphere) = Bounds(verts);
        model.Write = w =>
        {
            // A volume or mover collides with its own BSP, in its faces' own space as the stock models hold it (the
            // engine moves a check into the actor's space); a CSG brush's model holds only the bounds and Polys.
            UModelData m = csg ? new UModelData { NumSharedSides = 4, RootOutside = true } : SdcBrushBsp.Build(polys.Value, owner.Name);
            (m.BoxMin, m.BoxMax, m.Sphere, m.BoxValid) = (min, max, sphere, true);
            m.Polys = polysItem.Ref;
            m.Write(w, _layout, Identity);
        };
        polysItem.Write = w => UModelData.WritePolys(w, polys.Value, Identity);
        _local[ModelKey(owner)] = model;
    }

    private static string ModelKey(Item owner) => "\u0001model:" + owner.Name;

    /// <summary>A free name in the stock style for an id that is taken: its stem (the id without its number) and the
    /// first free number.</summary>
    private string FreeName(string id)
    {
        // Numbered as the stock editor names actors (Light0, Light1 ...).
        string stem = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return stem.Length > 0 ? Unique(stem, bare: false) : Unique(id);
    }

    /// <summary>A name no export, planned object or project object has: the stem (unless <paramref name="bare"/> is
    /// false), else the stem and a number.</summary>
    private string Unique(string stem, bool bare = true)
    {
        bool Free(string name) =>
            !_local.ContainsKey(name) && !_actors.ContainsKey(name) && _p.FindActor(name) is null && _p.FindBrush(name) is null;
        if (bare && Free(stem))
        {
            return stem;
        }

        for (int i = 0; ; i++)
        {
            string candidate = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Free(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The class of an actor: a script class import, or the map's own class export.</summary>
    private Func<int> ClassRefOf(string cls)
    {
        int dot = cls.LastIndexOf('.');
        string package = dot > 0 ? cls[..dot] : "Engine";
        string name = dot > 0 ? cls[(dot + 1)..] : cls;
        if (IsLocalPackage(package))
        {
            return () => _local.TryGetValue(name, out Item? local) ? local.Ref : Unresolved($"class {cls}");
        }

        return () => _src is not null && _srcImports.TryGetValue(cls + "|Class", out int imp) ? imp : _b.ImportClass(package, name);
    }

    private bool IsLocalPackage(string package) =>
        package.Equals(_name, StringComparison.OrdinalIgnoreCase) || package.Equals(_p.Id, StringComparison.OrdinalIgnoreCase)
        || package.Equals("MyLevel", StringComparison.OrdinalIgnoreCase) || (_src is not null && package.Equals(_src.Name, StringComparison.OrdinalIgnoreCase));

    private string? TypingClass(string cls)
    {
        if (_schema is null)
        {
            return null;
        }

        if (_schema.ResolveClass(cls) is { } known && known.Equals(cls, StringComparison.OrdinalIgnoreCase))
        {
            return known;
        }

        // A class defined in the map: typed as its nearest super the class data knows (as the reader types it).
        if (_src is not null)
        {
            string name = cls[(cls.LastIndexOf('.') + 1)..];
            UExport? ce = _src.Exports.FirstOrDefault(e => e.ClassRef == 0 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            for (int guard = 0; ce is not null && guard < 32; guard++)
            {
                if (ce.SuperRef < 0)
                {
                    string path = _src.PathOf(ce.SuperRef);
                    return _schema.ResolveClass(path) is { } s && s.Equals(path, StringComparison.OrdinalIgnoreCase) ? s : null;
                }

                ce = ce.SuperRef > 0 ? _src.Export(ce.SuperRef) : null;
            }
        }

        return null;
    }

    // ---- conveyors -------------------------------------------------------------------------------------------

    /// <summary>
    /// The project's actors as the package holds them. The original game has no conveyor class: conveyors are RE+
    /// belts there, as in the export for the original editor (<see cref="ConveyorHandoff"/>). Converted conveyors still
    /// as imported are written as their imported belts; any other conveyor is lowered to new belts of the map's own
    /// RE+ <c>ConveyorBelt</c> class (<see cref="ReConveyors"/>) in place of its group's imported belts. A package with
    /// no such class (a map made here, or one without RE+ belts) cannot hold them: they are left out, with a warning.
    /// </summary>
    private List<Actor> WrittenActors()
    {
        ConveyorHandoff handoff = ConveyorHandoff.Of(_p);
        List<Actor> written = _p.Actors.Where(a => handoff.Writes(a) && !ConveyorDesign.Is(a)).ToList();
        HandedOff.UnionWith(_p.Actors.Where(a => !handoff.Writes(a)).Select(a => a.Id));
        List<Actor> conveyors = _p.Actors.Where(a => ConveyorDesign.Is(a) && handoff.Writes(a)).ToList();
        HandedOff.UnionWith(conveyors.Select(a => a.Id));
        if (conveyors.Count == 0)
        {
            return written;
        }

        ObjectRef[] refs = conveyors.Take(50).Select(a => ObjectRef.Actor(a.Id)).ToArray();
        string? beltClass = BeltClass();
        if (beltClass is null)
        {
            _findings.Add(Finding.Warning("sdc.conveyorsLeftOut",
                $"{conveyors.Count} conveyors are left out: the original game has no conveyor class, and this package holds no RE+ ConveyorBelt "
                + "class to write them as belts (the export for the original editor writes them as RE+ belts)", refs));
            return written;
        }

        var names = new HashSet<string>(_p.Actors.Select(a => a.Id).Concat(_p.Brushes.Select(b => b.Id)), StringComparer.OrdinalIgnoreCase);
        var lowered = new Dictionary<string, (ReConveyorChain Chain, List<string> Names)>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor c in conveyors)
        {
            ReConveyorChain chain = ReConveyors.Lower(_p, c, []);
            lowered[c.Id] = (chain, chain.Belts.Select(b => UniqueIn(b.Name, names)).ToList());
        }

        var writtenIds = new HashSet<string>(written.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        string? FirstBelt(string id) =>
            lowered.TryGetValue(id, out var l) ? l.Names.FirstOrDefault()
            : handoff.Carried.Contains(id) && _p.FindActor(id) is { } c && BakedContent.LiftedFrom(c).FirstOrDefault() is { } belt
              && writtenIds.Contains(belt) ? belt : null;

        int count = 0;
        HashSet<string>? declared = DeclaredProps(beltClass);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor c in conveyors)
        {
            (ReConveyorChain chain, List<string> named) = lowered[c.Id];
            string? nextBelt = ConveyorDesign.NextId(c) is { } n ? FirstBelt(n) : null;
            for (int i = 0; i < chain.Belts.Count; i++)
            {
                string? next = i + 1 < chain.Belts.Count ? named[i + 1] : chain.Loop ? named[0] : nextBelt;
                Actor belt = Belt(chain, chain.Belts[i], named[i], next, beltClass);
                foreach (string key in BeltProps.Where(k => belt.Props.Contains(k) && declared is not null && !declared.Contains(k)).ToList())
                {
                    belt.Props.Remove(key);
                    missing.Add(key);
                }

                written.Add(belt);
                owner[belt.Id] = c.Id;
                count++;
            }
        }

        _findings.Add(Finding.Info("sdc.conveyorBelts",
            $"{conveyors.Count} conveyors added or changed since the import are written as {count} RE+ belts ({beltClass})", refs));
        if (ChainCap(beltClass) is { } cap && cap < ReConveyors.MaxChain)
        {
            LongChains(written.Skip(written.Count - count).ToList(), cap, beltClass, owner);
        }

        if (missing.Count > 0)
        {
            _findings.Add(Finding.Warning("sdc.conveyorSettings",
                $"the map's {beltClass} class (an older RE+) has no {string.Join(", ", missing)}: the lowered belts are written without them", refs));
        }

        return written;
    }

    /// <summary>
    /// How many belts one RE+ chain runs items over with the map's own belt class: its <c>Seg</c> array (16 in AeroD's
    /// version-1 class, 64 from version 2; the belts conversion in OpenSCCT reads it the same way). Null when the class
    /// has no readable Seg property.
    /// </summary>
    private int? ChainCap(string cls)
    {
        string name = cls[(cls.LastIndexOf('.') + 1)..];
        UExport? ce = _src?.Exports.FirstOrDefault(e => e.ClassRef == 0 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        for (int guard = 0; ce is not null && guard < 32; guard++)
        {
            int outer = ce.Index + 1;
            if (_src!.Exports.FirstOrDefault(e => e.Outer == outer && e.Name.Equals("Seg", StringComparison.OrdinalIgnoreCase)
                    && _src.ClassName(e).EndsWith("Property", StringComparison.Ordinal)) is { Size: > 0 } seg)
            {
                try
                {
                    // UObject (no tags), UField (SuperField, Next), then UProperty's ArrayDim.
                    UReader r = _src.Reader(seg);
                    UPropertyReader.SkipStateFrame(r, seg.Flags);
                    if (r.Name() != "None")
                    {
                        return null;
                    }

                    r.ObjRef();
                    r.ObjRef();
                    int dim = r.I32();
                    return dim is > 1 and <= 4096 ? dim : null;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }

            ce = ce.SuperRef > 0 ? _src.Export(ce.SuperRef) : null;
        }

        return null;
    }

    /// <summary>Warns of lowered RE+ chains (belts linked by NextBelt) longer than the map's class runs items over.</summary>
    private void LongChains(List<Actor> belts, int cap, string cls, Dictionary<string, string> owner)
    {
        var byId = belts.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        var fed = new HashSet<string>(belts.Select(b => PropValues.ActorId(b.Props["NextBelt"])).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longOnes = new List<(string First, int Count)>();
        foreach (Actor first in belts.Where(b => !fed.Contains(b.Id)).Concat(belts))
        {
            int n = 0;
            for (Actor? b = first; b is not null && seen.Add(b.Id); b = PropValues.ActorId(b.Props["NextBelt"]) is { } next ? byId.GetValueOrDefault(next) : null)
            {
                n++;
            }

            if (n > cap)
            {
                longOnes.Add((first.Id, n));
            }
        }

        if (longOnes.Count > 0)
        {
            _findings.Add(Finding.Warning("sdc.conveyorChain",
                $"the map's {cls} class runs items over at most {cap} belts in one chain (an older RE+): "
                + string.Join(", ", longOnes.Select(c => $"the chain lowered from {owner[c.First]} on has {c.Count}"))
                + $"; items run over the first {cap} only (riders still ride every belt)", longOnes.Select(c => ObjectRef.Actor(owner[c.First])).ToArray()));
        }
    }

    /// <summary>The settings a lowered belt takes from RE+ 2.8.0's ConveyorBelt (the engine's own properties aside).</summary>
    private static readonly string[] BeltProps =
    [
        "Length", "Width", "Speed", "NextBelt", "ItemSpacing", "FadeDistance", "ItemScale", "bCarryPlayers", "bShowItems", "bShowRollers",
        "bLeftWall", "bRightWall", "ItemMeshes",
    ];

    /// <summary>The properties a class of the source package declares, with its supers in the package; null when the
    /// package does not define it.</summary>
    private HashSet<string>? DeclaredProps(string cls)
    {
        string name = cls[(cls.LastIndexOf('.') + 1)..];
        UExport? ce = _src?.Exports.FirstOrDefault(e => e.ClassRef == 0 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ce is null)
        {
            return null;
        }

        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int guard = 0; ce is not null && guard < 32; guard++)
        {
            int outer = ce.Index + 1;
            props.UnionWith(_src!.Exports.Where(e => e.Outer == outer && _src.ClassName(e).EndsWith("Property", StringComparison.Ordinal)).Select(e => e.Name));
            ce = ce.SuperRef > 0 ? _src.Export(ce.SuperRef) : null;
        }

        return props;
    }

    /// <summary>The map's own RE+ belt class: the class its imported belts have, else a ConveyorBelt class the source
    /// package defines; null when there is none.</summary>
    private string? BeltClass()
    {
        if (_src is null)
        {
            return null;
        }

        if (_p.Actors.FirstOrDefault(ConveyorHandoff.IsLiftedBelt) is { } lifted)
        {
            return lifted.Class;
        }

        return _src.Exports.Any(e => e.ClassRef == 0 && e.Name.Equals("ConveyorBelt", StringComparison.OrdinalIgnoreCase))
            ? _p.Id + ".ConveyorBelt"
            : null;
    }

    /// <summary>One lowered belt, as the export for the original editor writes it (T3dExport.ConveyorBelts).</summary>
    private Actor Belt(ReConveyorChain chain, ReBelt b, string name, string? next, string cls)
    {
        var a = new Actor(name, cls)
        {
            Location = b.Start,
            Rotation = b.Rotation,
            DrawScale3D = new Vec3(b.Length / 100, chain.Width / 100, 1),
        };
        a.Props.Set("Length", SvJsonNode.Float((float)b.Length));
        a.Props.Set("Width", SvJsonNode.Float((float)chain.Width));
        a.Props.Set("Speed", SvJsonNode.Float((float)chain.Speed));
        if (next is not null)
        {
            a.Props.Set("NextBelt", PropValues.ActorRef(next));
        }

        a.Props.Set("ItemSpacing", SvJsonNode.Float((float)chain.ItemSpacing));
        a.Props.Set("FadeDistance", SvJsonNode.Float((float)chain.FadeDistance));
        a.Props.Set("ItemScale", SvJsonNode.Float((float)chain.ItemScale));
        a.Props.Set("bCarryPlayers", SvJsonNode.Bool(chain.CarryPlayers));
        a.Props.Set("bShowItems", SvJsonNode.Bool(chain.ItemMeshes.Count > 0 && chain.ItemSpacing > 0));
        a.Props.Set("bShowRollers", SvJsonNode.Bool(chain.ShowRollers));
        a.Props.Set("bLeftWall", SvJsonNode.Bool(b.LeftWall));
        a.Props.Set("bRightWall", SvJsonNode.Bool(b.RightWall));
        if (chain.ItemMeshes.Count > 0)
        {
            var meshes = new SvJsonObject();
            for (int m = 0; m < chain.ItemMeshes.Count; m++)
            {
                meshes.Add(m.ToString(System.Globalization.CultureInfo.InvariantCulture), PropValues.AssetRef(chain.ItemMeshes[m]));
            }

            var elements = new SvJsonObject();
            elements.Add("$elements", meshes);
            a.Props.Set("ItemMeshes", elements);
        }

        if (chain.Driver)
        {
            a.Props.Set("bHidden", SvJsonNode.Bool(true));
            a.Props.Set("bCollideActors", SvJsonNode.Bool(false));
            a.Props.Set("bBlockActors", SvJsonNode.Bool(false));
            a.Props.Set("bBlockPlayers", SvJsonNode.Bool(false));
        }

        return a;
    }

    private static string UniqueIn(string stem, HashSet<string> names)
    {
        if (names.Add(stem))
        {
            return stem;
        }

        for (int i = 0; ; i++)
        {
            string candidate = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // ---- the cooked layout ------------------------------------------------------------------------------------

    /// <summary>True for an actor the cook leaves out of a game copy: static lights (light-typed actors that do not
    /// matter at run time) and PS2-only actors.</summary>
    private bool CookedOut(Actor a) => CookedOut(a, _schema);

    /// <summary>An actor a cooked copy does not hold at all: one the cook leaves out that nothing it keeps refers to.</summary>
    private bool LeftOut(Actor a) => _layout == SdcLayout.Cooked && CookedOut(a) && !_cookKeeps.Contains(a.Id);

    /// <summary>
    /// The actors the cook takes out of Level.Actors that its save still writes: those a kept actor refers to, at any
    /// depth (the cooked AquaD holds the static lights its movers name in IgnoredEchelonLights, outside Level.Actors).
    /// </summary>
    private HashSet<string> ReferencedCookedOut()
    {
        var keeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_layout != SdcLayout.Cooked)
        {
            return keeps;
        }

        var byId = new Dictionary<string, Actor>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in _writes)
        {
            byId.TryAdd(a.Id, a);
        }

        var queue = new Queue<Actor>(_writes.Where(a => !CookedOut(a)));
        while (queue.TryDequeue(out Actor? a))
        {
            foreach (string id in Scripting.ChangeApplier.References(a.Props))
            {
                if (byId.TryGetValue(id, out Actor? target) && CookedOut(target) && keeps.Add(target.Id))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return keeps;
    }

    internal static bool CookedOut(Actor a, UeSchema? schema)
    {
        if (a.Props.GetInt("Platform") == 1)
        {
            return true;
        }

        var probe = new SdcActor(0, new UExport(0, 0, 0, 0, a.Id, 0, 0, 0), a.Class) { Props = a.Props.ToJson() };
        return SdcMapReader.LeftOutByCook(probe, schema) && !(schema?.IsA(a.Class, "Engine.Brush") ?? false);
    }

    /// <summary>The PS2 lighting instances (the actors' StaticMeshInstanceOnPSX2), which a cooked copy does not hold;
    /// one an actor also uses as its XBOX instance (some movers in BankP) stays.</summary>
    private void RemovePs2Instances(SdcMap map)
    {
        Item? Instance(SdcActor n, string key) =>
            n.Props[key] is SvJsonObject o && o["$asset"] is SvJsonString path
            && _local.TryGetValue(path.Value[(path.Value.IndexOf('.') + 1)..], out Item? smi) && smi.Src >= 0 ? smi : null;

        var xbox = new HashSet<Item>(map.Actors.Select(n => Instance(n, "StaticMeshInstanceOnXBOX")).OfType<Item>());
        foreach (Item smi in map.Actors.Select(n => Instance(n, "StaticMeshInstanceOnPSX2")).OfType<Item>().Where(s => !xbox.Contains(s)))
        {
            Remove(smi.Src);
        }
    }

    // ---- ordering ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Final export order: the source's objects in their order with the slots of removed ones closed up, then the new
    /// objects. An object that cannot move keeps its source slot; when too few objects come before it to fill the slots
    /// below it, empty package objects hold the rest.
    /// </summary>
    private void Order()
    {
        var pinned = new Dictionary<int, Item>();
        var free = new List<Item>();
        for (int i = 0; i < _bySrc.Length; i++)
        {
            if (_bySrc[i] is not { } it)
            {
                continue;
            }

            if (it.Movable)
            {
                free.Add(it);
            }
            else
            {
                pinned[i] = it;
            }
        }

        free.AddRange(_items);
        int lastPinned = pinned.Count == 0 ? -1 : pinned.Keys.Max();
        var order = new List<Item>(free.Count + pinned.Count);
        int next = 0, placeholders = 0;
        for (int i = 0; i <= lastPinned || next < free.Count; i++)
        {
            if (pinned.TryGetValue(i, out Item? fixedItem))
            {
                order.Add(fixedItem);
            }
            else if (next < free.Count)
            {
                order.Add(free[next++]);
            }
            else
            {
                var hold = new Item
                {
                    Name = Unique("Unused"), Origin = New, Flags = 0, ClassRef = () => _b.ImportClass("Core", "Package"), Write = w => w.Name("None"),
                };
                _local.TryAdd(hold.Name, hold);
                order.Add(hold);
                placeholders++;
            }
        }

        if (placeholders > 0)
        {
            _findings.Add(Finding.Info("sdc.placeholders",
                $"{placeholders} export slots are held by empty package objects, so the map's objects that keep their slots stay where they were"));
        }

        _order = order;
        for (int i = 0; i < _order.Count; i++)
        {
            _order[i].Index = i;
        }
    }

    /// <summary>A source objref in the new package: imports keep their indices, exports follow their item.</summary>
    private int Remap(int srcRef)
    {
        if (srcRef <= 0 || _src is null)
        {
            return srcRef;
        }

        return srcRef <= _bySrc.Length && _bySrc[srcRef - 1] is { } it ? it.Ref : 0;
    }

    private static int Identity(int objref) => objref;

    // ---- references -------------------------------------------------------------------------------------------

    /// <summary>A reference value to its objref: actors by id, the map's own objects by path, other objects through
    /// imports (the source's, else made from the install's packages).</summary>
    private int Resolve(SvJsonNode value)
    {
        switch (value)
        {
            case SvJsonObject o when o["$actor"] is SvJsonString id:
                if (_actors.TryGetValue(id.Value, out Item? actor))
                {
                    return actor.Ref;
                }

                // The cook leaves CSG brushes and static lights out; references to them become None, as in its copies.
                return _layout == SdcLayout.Cooked && (_p.FindActor(id.Value) is not null || _p.FindBrush(id.Value) is not null)
                    ? 0
                    : Unresolved($"actor {id.Value}");
            case SvJsonObject o when o["$asset"] is SvJsonString path:
                return Asset(path.Value);
            case SvJsonObject o when o["$class"] is SvJsonString cls:
                return ClassRefOf(cls.Value)();
            case SvJsonObject o when o["\u0001item"] is SvJsonString key:
                return _local.TryGetValue(key.Value, out Item? item) ? item.Ref : 0;
            case SvJsonObject o when o[SourceKey] is SvJsonNumber n && n.TryGetInt64(out long objref):
                return Remap((int)objref);
            default:
                return 0;
        }
    }

    /// <summary>A face's material: none for an untextured face or the editor's grid placeholder (no game package
    /// holds it; a face with no material is what an untextured face is in the stock editor).</summary>
    private int FaceMaterial(string? material) => material is not { Length: > 0 } || IsPlaceholder(material) ? 0 : Asset(material);

    private static bool IsPlaceholder(string? material) =>
        string.Equals(material, Geometry.Starter.DefaultMaterial, StringComparison.OrdinalIgnoreCase);

    private int Asset(string path)
    {
        int dot = path.IndexOf('.');
        if (dot > 0 && IsLocalPackage(path[..dot]))
        {
            if (_local.TryGetValue(path[(dot + 1)..], out Item? local) && local.Index >= 0)
            {
                return local.Ref;
            }

            // Not the map's own object: an editor copy can import from a package of the map's name (the cooked
            // copy's textures, say).
        }

        if (_srcImports.TryGetValue(path, out int imp))
        {
            return imp;
        }

        int made = _assets.Import(_b, path);
        return made != 0 ? made : Unresolved($"asset {path}");
    }

    private int Unresolved(string what)
    {
        if (_unresolved.Add(what) && _unresolved.Count <= 50)
        {
            _findings.Add(Finding.Warning("sdc.unresolved", $"{what} is not in the map or the install: the reference is written as None. "
                + "Use an asset from the selected original-game install, restore the missing source package, or export custom assets through the original-editor export folder."));
        }

        return 0;
    }

    // ---- properties -------------------------------------------------------------------------------------------

    private List<KeyValuePair<string, SvJsonNode>> LevelInfoProps() => _p.LevelInfo.ToList();

    private List<KeyValuePair<string, SvJsonNode>> ActorProps(Actor a, SdcActor? n)
    {
        var props = a.Props.Where(kv => !s_engineState.Contains(kv.Key)).ToList();
        if (EmitterBuild.SystemsOf(a, _p) is { Count: > 0 } &&
            (n is null || n.SubObjects.Count == 0 || T3dImport.ObjectsOf(a).Count == 0))
        {
            _findings.Add(Finding.Warning("sdc.emitterComponents",
                $"actor {a.Id}: write this map through T3D or the RE+ export folder to keep new or edited particle systems",
                ObjectRef.Actor(a.Id)));
            if (n is null || n.SubObjects.Count == 0)
            {
                props.RemoveAll(kv => kv.Key.Equals("Emitters", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (n is not null)
        {
            StoredText(props, a.Source);
        }

        Transform(props, a.Class, a.Location, a.Rotation, a.DrawScale, a.DrawScale3D, a.PrePivot);
        if (n is not null && a.Props["StaticMeshInstance"] is { } smi)
        {
            props.Add(new(_layout == SdcLayout.Editor ? "StaticMeshInstanceOnXBOX" : "StaticMeshInstance", smi));
        }
        else if (n is null && a.Props["StaticMesh"] is not null)
        {
            // A lighting instance belongs to the source actor it was made for (a duplicate copies its original's); a
            // new mesh actor has none until a rebuild bakes one.
            _unlitMeshes.Add(a.Id);
        }

        if (_layout == SdcLayout.Editor && n?.Props["StaticMeshInstanceOnPSX2"] is { } ps2)
        {
            props.Add(new("StaticMeshInstanceOnPSX2", ps2));
        }

        if (a.Shape is { Faces.Count: > 0 })
        {
            OwnModel(props, a.Id);
        }

        return props;
    }

    /// <summary>The import puts the build language's text over a localized property's stored text; while the project
    /// still holds that text, the stored one is written back (SdcOverlay.StoredTextKey).</summary>
    private static void StoredText(List<KeyValuePair<string, SvJsonNode>> props, string? source)
    {
        if (source is not { Length: > 0 } || source[0] != '{' || SvJson.Parse(source) is not SvJsonObject src
            || src[SdcOverlay.StoredTextKey] is not SvJsonObject texts)
        {
            return;
        }

        for (int i = 0; i < props.Count; i++)
        {
            if (texts[props[i].Key] is SvJsonArray { Count: 2 } pair && SameValue(props[i].Value, pair[0]))
            {
                props[i] = new(props[i].Key, pair[1]);
            }
        }
    }

    /// <summary>Points Brush at the model planned for the object's faces, in the place the property had (a duplicate
    /// copies its original's Brush, which is the original's model).</summary>
    private void OwnModel(List<KeyValuePair<string, SvJsonNode>> props, string id)
    {
        if (!_actors.TryGetValue(id, out Item? owner) || !_local.ContainsKey(ModelKey(owner)))
        {
            return;
        }

        var value = new KeyValuePair<string, SvJsonNode>("Brush", ItemRef(ModelKey(owner)));
        int at = props.FindIndex(kv => kv.Key == "Brush");
        if (at >= 0)
        {
            props[at] = value;
        }
        else
        {
            props.Add(value);
        }
    }

    private List<KeyValuePair<string, SvJsonNode>> BrushProps(Brush b, SdcActor? n)
    {
        var props = b.Props.Where(kv => kv.Key is not ("MainScale" or "PostScale")).ToList();
        props.Add(new("CsgOper", SvJsonNode.Integer(b.Oper == CsgOper.Add ? 1 : 2)));
        if (b.PolyFlags != 0)
        {
            props.Add(new("PolyFlags", SvJsonNode.Integer(b.PolyFlags)));
        }

        Transform(props, "Engine.Brush", b.Location, b.Rotation, 1, Vec3.One, b.PrePivot);
        // The stock editor stores both scales on nearly every brush; a few old brushes never had them.
        if (n is null || n.Props["MainScale"] is not null || !IsUnitScale(b.MainScale, b.Props["MainScale"]))
        {
            props.Add(new("MainScale", Scale(b.MainScale, b.Props["MainScale"])));
        }

        if (n is null || n.Props["PostScale"] is not null || !IsUnitScale(b.PostScale, b.Props["PostScale"]))
        {
            props.Add(new("PostScale", Scale(b.PostScale, b.Props["PostScale"])));
        }

        OwnModel(props, b.Id);
        return props;
    }

    private static SvJsonObject ItemRef(string key)
    {
        var o = new SvJsonObject();
        o.Add("\u0001item", SvJsonNode.String(key));
        return o;
    }

    private static bool IsUnitScale(Vec3 scale, SvJsonNode? sheer) =>
        scale == Vec3.One && ((sheer as SvJsonObject)?["SheerRate"] is not SvJsonNumber rate || PropValues.Num(rate) == 0);

    /// <summary>An FScale as the stock editor stores it on brushes: every member (SHEER_ZX when unsheared).</summary>
    private static SvJsonObject Scale(Vec3 scale, SvJsonNode? sheer)
    {
        var o = new SvJsonObject();
        o.Add("Scale", PropValues.Vector(scale));
        o.Add("SheerRate", (sheer as SvJsonObject)?["SheerRate"] ?? SvJsonNode.Float(0));
        o.Add("SheerAxis", (sheer as SvJsonObject)?["SheerAxis"] ?? SvJsonNode.Integer(5));
        return o;
    }

    /// <summary>The transform properties that differ from the class defaults (the engine stores only those). In the
    /// project a zero PrePivot and a unit DrawScale or DrawScale3D mean the class default (ActorRules), so those are
    /// not stored either.</summary>
    private void Transform(List<KeyValuePair<string, SvJsonNode>> props, string cls, Vec3 location, Rot rotation, double drawScale, Vec3 drawScale3D, Vec3 prePivot)
    {
        void Vector(string key, Vec3 v, Vec3 unset)
        {
            Vec3 d = PropValues.TryVector(_schema?.DefaultOf(cls, key), out Vec3 dv) ? dv : unset;
            if (v != d && (key == "Location" || v != unset))
            {
                props.Add(new(key, PropValues.Vector(v)));
            }
        }

        Vector("Location", location, Vec3.Zero);
        Rot rd = PropValues.TryRotator(_schema?.DefaultOf(cls, "Rotation"), out Rot r) ? r : Rot.Zero;
        if (rotation != rd)
        {
            props.Add(new("Rotation", PropValues.Rotator(rotation)));
        }

        double sd = _schema?.DefaultOf(cls, "DrawScale") is SvJsonNumber n ? PropValues.Num(n) : 1;
        if ((float)drawScale != (float)sd && drawScale != 1)
        {
            props.Add(new("DrawScale", SvJsonNode.Float((float)drawScale)));
        }

        Vector("DrawScale3D", drawScale3D, Vec3.One);
        Vector("PrePivot", prePivot, Vec3.Zero);
    }

    // ---- polys ------------------------------------------------------------------------------------------------

    /// <summary>A face as an FPoly in its owner's space, with references in the new package. The pans fold into the base (UE2 has no pans at this
    /// version); what the project does not keep (the stored normal, a poly's link back to its brush) comes from the
    /// source poly when the face has not moved.</summary>
    private FPoly ToPoly(BrushFace f, FPoly? source)
    {
        Vector3[] verts = FaceVertices(f);
        Vec3 baseV = TextureOps.FoldPans(f.TextureBase, f.TextureU, f.TextureV, f.PanU, f.PanV);

        bool same = source is not null && source.Vertices.SequenceEqual(verts);
        Vector3 normal = same ? source!.Normal : PolyNormal(verts);
        int material = f.Material is not { Length: > 0 } m || IsPlaceholder(m) ? 0
            : source is not null && source.Material != 0 && _src!.PathOf(source.Material).Equals(m, StringComparison.OrdinalIgnoreCase)
                ? Remap(source.Material) // the source's own reference (a package can import one path twice)
                : Asset(m);
        float xbox = (float)f.LightMapScale;
        return new FPoly(F(baseV), normal, F(f.TextureU), F(f.TextureV), verts, unchecked((uint)f.Flags),
            same ? Remap(source!.Actor) : 0, material, f.ItemName ?? "None", f.Link, same ? source!.BrushPoly : -1,
            f.LightMapScalePsx2 is double ps2 ? (float)ps2 : xbox, xbox);
    }

    private static Vector3[] FaceVertices(BrushFace f) => f.Vertices.Select(F).ToArray();

    /// <summary>A brush face in world space, as an editor copy's world surfaces hold their source face (its own Actor
    /// and iBrushPoly left None and -1: the surface carries the link).</summary>
    private FPoly WorldPoly(Brush b, int face)
    {
        BrushFace f = b.Shape.Faces[face];
        ShapeTransform t = ShapeTransform.For(b);
        FPoly local = ToPoly(f, null);
        Vector3[] verts = t.FaceToWorld(f).Select(F).ToArray();
        static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);
        return local with
        {
            Base = F(t.ToWorld(V(local.Base))),
            Normal = PolyNormal(verts),
            TextureU = F(t.TextureAxisToWorld(V(local.TextureU))),
            TextureV = F(t.TextureAxisToWorld(V(local.TextureV))),
            Vertices = verts,
        };
    }

    /// <summary>UE2 FPoly::CalcNormal: the sum of the fan's cross products, normalised.</summary>
    public static Vector3 PolyNormal(IReadOnlyList<Vector3> v)
    {
        Vector3 n = Vector3.Zero;
        for (int i = 2; i < v.Count; i++)
        {
            n += Vector3.Cross(v[i - 1] - v[0], v[i] - v[0]);
        }

        return n.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(n);
    }

    private static Vector3 F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>A brush model's box and sphere: its faces in their own (Polys) space, as every stock brush, volume and
    /// mover model holds them whatever the actor's Location, PrePivot or rotation.</summary>
    private static (Vector3 Min, Vector3 Max, Vector4 Sphere) Bounds(IReadOnlyList<Vector3[]> polys)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3[] p in polys)
        {
            foreach (Vector3 v in p)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        if (polys.Count == 0)
        {
            return (Vector3.Zero, Vector3.Zero, Vector4.Zero);
        }

        Vector3 centre = (min + max) / 2;
        return (min, max, new Vector4(centre, Vector3.Distance(min, max) / 2));
    }

    // ---- the level --------------------------------------------------------------------------------------------

    /// <summary>Level.Actors from the source's: the same order, brushes in the project's CSG order (new ones just
    /// before the next source brush), new actors at the end.</summary>
    private void LevelActorsFromSource(SdcMap map)
    {
        var brushQueue = new Queue<Item>(_layout == SdcLayout.Editor ? _p.Brushes.Select(b => _actors[b.Id]) : []);
        var projectBrushes = new HashSet<Item>(brushQueue);
        var placed = new HashSet<Item>();
        foreach (int objref in map.LevelRecord.Actors)
        {
            if (objref <= 0)
            {
                _levelActors.Add(null!);
                continue;
            }

            Item? it = objref <= _bySrc.Length ? _bySrc[objref - 1] : null;
            if (it is null || placed.Contains(it) || _outsideLevel.Contains(it))
            {
                continue;
            }

            if (projectBrushes.Contains(it))
            {
                // This position held a brush: the project's next brushes up to the next one from the source.
                while (brushQueue.Count > 0)
                {
                    Item next = brushQueue.Dequeue();
                    if (placed.Add(next))
                    {
                        _levelActors.Add(next);
                    }

                    if (next.Src >= 0)
                    {
                        break;
                    }
                }

                continue;
            }

            placed.Add(it);
            _levelActors.Add(it);
        }

        foreach (Item b in brushQueue.Where(placed.Add))
        {
            _levelActors.Add(b);
        }

        foreach (Actor a in _writes)
        {
            if (_actors.TryGetValue(a.Id, out Item? it) && !placed.Contains(it) && !_outsideLevel.Contains(it))
            {
                placed.Add(it);
                _levelActors.Add(it);
            }
        }
    }

    private void WriteLevel(UWriter w)
    {
        ULevel? src = _srcMap?.LevelRecord;
        w.Name("None");
        var actors = _levelActors.Select(a => a is null ? 0 : a.Ref).ToList();
        w.I32(actors.Count);
        w.I32(src is not null && src.Actors.Count == actors.Count ? src.ActorsMax : actors.Count);
        actors.ForEach(w.ObjRef);
        w.FString(src?.UrlProtocol ?? "shadowstrike");
        w.FString(src?.UrlHost ?? "");
        w.FString(src?.UrlMap ?? "Entry.sds");
        w.FString(src?.UrlPortal ?? "");
        IReadOnlyList<string> options = src?.UrlOptions ?? [];
        w.Compact(options.Count);
        foreach (string op in options)
        {
            w.FString(op);
        }

        w.I32(src?.UrlPort ?? 7777);
        w.I32(src?.UrlValid ?? 1);
        int psx2 = _worldPsx2?.Ref ?? 0;
        w.I32(psx2 != 0 ? 1 : 0);
        if (psx2 != 0)
        {
            w.ObjRef(psx2);
        }

        int xbox = _worldXbox?.Ref ?? 0;
        w.I32(xbox != 0 ? 1 : 0);
        if (xbox != 0)
        {
            w.ObjRef(xbox);
        }

        w.F32(src?.TimeSeconds ?? 0);
        w.ObjRef(src is null ? 0 : Remap(src.FirstDeleted));
        for (int i = 0; i < 16; i++)
        {
            w.ObjRef(src is null ? 0 : Remap(src.TextBlocks[i]));
        }

        IReadOnlyList<(string Key, string Value)> travel = src?.TravelInfo ?? [];
        w.Compact(travel.Count);
        foreach ((string key, string value) in travel)
        {
            w.FString(key);
            w.FString(value);
        }

        // The cooked save drops the GEs of other platforms (SDC-FORMAT.md section 5): the PC game is the stock editor's
        // platform 2, so PS2-only GEs go.
        List<SdcGe> ges = SdcGePartition.Records(_layout == SdcLayout.Cooked ? _p.Ges.Where(g => g.Platform != PlatformPs2) : _p.Ges);
        if (src is not null && src.PartitionBytes.Length > 0 && SdcGePartition.Same(ges, src.Ges))
        {
            w.Bytes(src.PartitionBytes);
        }
        else
        {
            SdcGePartition.Write(w, ges);
        }
    }

    private sealed class IgnoreCasePair : IEqualityComparer<(string, string)>
    {
        public static readonly IgnoreCasePair Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
