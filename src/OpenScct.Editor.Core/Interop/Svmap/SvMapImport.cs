using OpenScct.Content;
using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using EditorActor = OpenScct.Editor.Core.Model.Actor;

namespace OpenScct.Editor.Core.Interop.Svmap;

/// <summary>
/// Turns a converted (or any) SVMap 1.x folder into a project. Gameplay actors come from <c>sim/gameplay.json</c> with
/// every stored property, volumes get their shape back from their brush collider, GEs come from
/// <c>sim/traversal.json</c>, and the render-only actors the converter keeps (lights and zones in lighting.json,
/// emitters in fx.json, static meshes in the world.glb instance table, ambient sounds in audio.json) become actors
/// again. The compiled world itself (BSP render and collision) stays in the source folder as
/// <see cref="MapProject.Baked"/>, drawn as a locked layer and compiled through unchanged, so the map stays playable.
/// The map's identity and pictures are kept as well (<see cref="IdentityImport"/>).
/// <para>A converted map keeps the original's BSP nodes, so its brushes are recovered from them as a cooked .sdc's are
/// (<see cref="SvMapBsp"/>, <see cref="BrushRecoveryImport"/>): editable brushes in the Recovered brushes layer, marked
/// baked, beside the baked world that still plays as the original. <see cref="ImportOptions.BrushRecovery"/> Off, or a
/// map with no BSP nodes (a native one), leaves the baked world only.</para>
/// <para>The render actors' <see cref="EditorActor.Source"/> is JSON with <c>"baked": &lt;file&gt;</c>, the source
/// map's file that already holds them (render/lighting.json, render/fx.json, render/world.glb, audio/audio.json), so a
/// compiler that keeps the baked render can tell them from actors added in the editor.</para>
/// <para>SVMap 1.2's match rules and win targets go onto their actors (<see cref="EditorActor.Match"/>); its outfits
/// become the project's, pointing at the source map's files until they are replaced.</para>
/// </summary>
public static class SvMapImport
{
    /// <summary>The key of the SVMap leftovers in <see cref="MapProject.Extra"/> (animations and level info).</summary>
    public const string ExtraKey = "svmap";

    /// <summary>The layer name the baked world is shown on.</summary>
    public const string BakedLayer = "Baked world";

    public const string LightsFolder = "Render/Lights";
    public const string MeshesFolder = "Render/Static meshes";
    public const string EmittersFolder = "Render/Emitters";
    public const string SoundsFolder = "Render/Sounds";
    public const string ZonesFolder = "Render/Zones";

    /// <summary>The key, in a render actor's source JSON, of the source map file that holds it.</summary>
    public const string BakedKey = "baked";

    public static bool IsSvMapFolder(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, SvMapHash.ManifestFile)) && File.Exists(Path.Combine(path, GameplayDoc.RelativePath));

    public static ImportResult Import(string folder, ImportOptions options)
    {
        string root = Path.GetFullPath(folder);
        var findings = new List<Finding>();
        options.Progress?.Invoke($"loading {Path.GetFileName(root)}");
        SvMap map;
        try
        {
            map = SvMap.Load(root, new SvMapLoadOptions { VerifySimHash = false, StrictCollision = false, CheckReferences = false });
        }
        catch (SvMapFormatException ex)
        {
            throw new InvalidDataException($"{root}: {ex.Message}", ex);
        }

        if (map.SimHash != map.Manifest.SimHash)
        {
            findings.Add(Finding.Warning("import.simHash", $"the sim files differ from the manifest (sim hash {map.SimHash[..16]}, manifest {map.Manifest.SimHash[..16]})"));
        }

        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        // The document's own JSON form: every stored property exactly as the file has it.
        SvJsonObject gameplay = map.Gameplay.ToJson();
        var p = new MapProject
        {
            Id = options.MapId ?? map.Id,
            Title = map.Manifest.Title,
            Source = new ProjectSource { Kind = "svmap", File = root, Sha256 = map.Manifest.SimHash },
            LevelInfo = PropSet.FromJson((gameplay["levelInfo"] as SvJsonObject)?["props"]),
            Baked = new BakedWorld { SourceMap = root, KeepRender = true, KeepCollision = true },
        };
        p.Layers.Add(new Layer("Default"));
        p.Layers.Add(new Layer(BakedLayer) { Locked = true, Color = "#5A6270" });
        if (gameplay["mapSettings"] is SvJsonObject settings)
        {
            foreach (KeyValuePair<string, SvJsonNode> mode in settings)
            {
                p.MapSettings[mode.Key] = PropSet.FromJson(mode.Value);
            }
        }

        if (ReadJson(root, "ui/mapinfo.json") is SvJsonObject mapInfo)
        {
            p.MapInfo = PropSet.FromJson(mapInfo);
        }

        p.Identity = IdentityImport.Read(root, out string? ownName);
        if (ownName is not null)
        {
            p.Title = ownName;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int shapes = AddGameplayActors(map, gameplay, p, ids);
        AddGes(map, root, p);
        AddMatch(map.Match, p, findings);
        AddMoverLegs(map, p, findings);
        if (map.MoverPaths is { } paths)
            foreach (var (moverId, path) in paths)
                if (p.FindActor(moverId) is { } a)
                {
                    var keys = path.Keys.Select(k => new Gameplay.MoverKey(new(k.Offset.X, k.Offset.Y, k.Offset.Z), new(k.Turn.Pitch, k.Turn.Yaw, k.Turn.Roll))).ToList();
                    Gameplay.MoverEdits.SetKeys(p, moverId, keys, startKey: path.Start).Do(p);
                }

        p.Outfits = SourceOutfits(root, findings);

        var extra = new SvJsonObject();
        extra.Add("levelInfo", SvJsonNode.String(map.Gameplay.LevelInfo.Id));
        if (gameplay["animations"] is { } animations)
        {
            extra.Add("animations", animations);
        }

        var counts = new RenderCounts();
        options.Progress?.Invoke("reading render actors");
        AddLighting(root, p, ids, schema, counts);
        AddEmitters(root, p, ids, schema, counts);
        AddStaticMeshes(root, p, ids, map.Collision, counts);
        AddSounds(root, p, ids, schema, counts);
        p.Extra.Add(ExtraKey, extra);
        T3d.T3dImport.CameraFromLevelInfo(p);
        RecoverBrushes(root, map.Collision, p, options, findings);

        int brushVolumes = p.Actors.Count(a => a.Props.Contains("Brush"));
        findings.Add(Finding.Info("import.summary",
            $"{map.Gameplay.Actors.Count} gameplay actors ({shapes} of {brushVolumes} volume shapes recovered), {p.Ges.Count} GEs, "
            + $"render actors added: {counts.Lights} lights, {counts.Zones} zones, {counts.Emitters} emitters, {counts.Meshes} static meshes, "
            + $"{counts.Sounds} sounds; "
            + $"world BSP ({map.Collision.Zones.Length} zones) kept as the baked layer"));
        if (brushVolumes > shapes)
        {
            findings.Add(Finding.Info("import.volumeShapes",
                $"{brushVolumes - shapes} brush actors have no brush collider (non-colliding volumes): their shape is only in the baked render"));
        }

        if (options.MapId is { } id && !string.Equals(id, map.Id, StringComparison.Ordinal))
        {
            findings.Add(Finding.Warning("import.renamed", $"the map is renamed from {map.Id} to {id}; references to {map.Id}.* assets still point at the source map's package"));
        }

        return new ImportResult(p, findings);
    }

    /// <summary>The brushes of a converted map, recovered from the BSP nodes its collision keeps. A map with none, or
    /// brush recovery turned off, keeps the baked world only; a failure does too, with a warning.</summary>
    private static void RecoverBrushes(string root, CollisionData collision, MapProject p, ImportOptions options, List<Finding> findings)
    {
        if (options.BrushRecovery == BrushRecoveryMode.Off)
        {
            return;
        }

        options.Progress?.Invoke("recovering brushes: reading the world BSP");
        var notes = new List<string>();
        CookedBsp? bsp;
        try
        {
            bsp = SvMapBsp.Read(root, collision, notes);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            BrushRecoveryImport.Failed(p, ex, findings);
            return;
        }

        if (bsp is null)
        {
            return;
        }

        int before = p.Brushes.Count;
        BrushRecoveryImport.Run(p, bsp, options.BrushRecovery, options.Progress, findings);
        if (p.Brushes.Count > before)
        {
            findings.AddRange(notes.Select(note => Finding.Info(BrushRecoveryImport.RecoveredCode, note)));
        }
    }

    /// <summary>sim/match.json's rules and win targets onto the actors they name.</summary>
    private static void AddMatch(MatchRuleSet? rules, MapProject p, List<Finding> findings)
    {
        if (rules is null)
        {
            return;
        }

        foreach (MatchRule r in rules.Rules)
        {
            if (p.FindActor(r.Actor) is { } a)
            {
                a.Match = (a.Match ?? new ActorMatch(PlayerRange.Any)) with { Players = r.Players, Outside = r.Outside, Inside = r.Inside };
            }
            else
            {
                findings.Add(Finding.Warning("import.match", $"sim/match.json names {r.Actor}, which is not in the map"));
            }
        }

        foreach (WinTargets w in rules.Win)
        {
            if (p.FindActor(w.Mission) is { } a)
            {
                a.Match = (a.Match ?? new ActorMatch(PlayerRange.Any)) with { Win = [.. w.BySize] };
            }
        }
    }

    /// <summary>sim/movers.json's per-key timing onto the movers it names.</summary>
    private static void AddMoverLegs(SvMap map, MapProject p, List<Finding> findings)
    {
        if (map.Movers is null)
        {
            return;
        }

        foreach ((string id, OpenScct.Core.Engine.MoverLegs legs) in map.Movers)
        {
            if (p.FindActor(id) is { } a)
            {
                // Stored as every other path stores it: an entry that sets nothing is no timing.
                a.Legs = MoverTiming.OrNull(legs.Keys);
            }
            else
            {
                findings.Add(Finding.Warning("import.movers", $"sim/movers.json names {id}, which is not in the map"));
            }
        }
    }

    /// <summary>render/outfits.json with its paths made absolute into the source map.</summary>
    private static MapOutfits SourceOutfits(string root, List<Finding> findings)
    {
        MapOutfits? outfits;
        try
        {
            outfits = OutfitsDoc.Load(root);
        }
        catch (SvMapFormatException ex)
        {
            findings.Add(Finding.Warning("import.outfits", ex.Message));
            return MapOutfits.None;
        }

        if (outfits is null)
        {
            return MapOutfits.None;
        }

        string render = Path.Combine(root, "render");
        string Full(string path) => Path.GetFullPath(Path.Combine(render, path)).Replace('\\', '/');
        TeamOutfit? Team(TeamOutfit? t) => t is null ? null : t with
        {
            Body = t.Body is { } b ? b with { Texture = Full(b.Texture) } : null,
            Head = t.Head is { } h ? h with { Texture = Full(h.Texture) } : null,
            Model = t.Model is { Glb: { } glb } m ? m with { Glb = Full(glb) } : t.Model,
        };
        return new MapOutfits(Team(outfits.Spy), Team(outfits.Merc));
    }

    private static int AddGameplayActors(SvMap map, SvJsonObject gameplay, MapProject p, HashSet<string> ids)
    {
        // The first brush collider of each actor: volumes get their editable shape back from it.
        var brushColliders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        CollisionData c = map.Collision;
        for (int i = 0; i < c.Colliders.Length; i++)
        {
            if (c.Colliders[i].Kind == ColliderKind.Brush && c.ActorOf(c.Colliders[i]) is { } owner)
            {
                brushColliders.TryAdd(owner, i);
            }
        }

        int shapes = 0;
        foreach (SvJsonObject r in (gameplay["actors"] as SvJsonArray ?? []).OfType<SvJsonObject>())
        {
            if (Str(r["id"]) is not { } id || Str(r["class"]) is not { } cls)
            {
                continue;
            }

            var a = new EditorActor(id, cls);
            PropSet props = PropSet.FromJson(r["props"]);
            InteropValues.TakeTransform(props, a);
            a.Props = props;
            if (r["src"] is SvJsonObject src)
            {
                a.Source = InteropValues.Compact(src);
            }

            if (props.Contains("Brush") && brushColliders.TryGetValue(id, out int ci) && BrushRecovery.FromCollider(c, ci, a) is { } shape)
            {
                a.Shape = shape;
                shapes++;
            }

            p.Actors.Add(a);
            ids.Add(id);
        }

        return shapes;
    }

    private static void AddGes(SvMap map, string root, MapProject p)
    {
        // TraversalDoc drops the derived flag word; read it from the file so it round-trips.
        var flags = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ReadJson(root, TraversalDoc.RelativePath) is SvJsonObject raw && raw["ges"] is SvJsonArray list)
        {
            foreach (SvJsonObject ge in list.OfType<SvJsonObject>())
            {
                if (ge["id"] is SvJsonString id && ge["flags"] is SvJsonNumber f && f.TryGetInt32(out int v))
                {
                    flags[id.Value] = v;
                }
            }
        }

        foreach (GERecord r in map.Traversal.Ges)
        {
            var ge = new Ge(r.Id, r.Kind.ToString())
            {
                Mask = r.Mask,
                Platform = (int)r.Platform,
                Flags = flags.GetValueOrDefault(r.Id),
            };
            foreach (OpenScct.Core.FVector pt in r.Points)
            {
                ge.Points.Add(new Vec3(pt.X, pt.Y, pt.Z));
            }

            var src = new SvJsonObject();
            src.Add("kind", SvJsonNode.String(r.Source.Kind));
            if (r.Source.Index is int index)
            {
                src.Add("index", SvJsonNode.Integer(index));
            }

            if (r.Source.Node is { } node)
            {
                src.Add("node", SvJsonNode.String(node));
            }

            if (r.Source.Note is { } note)
            {
                src.Add("note", SvJsonNode.String(note));
            }

            ge.Source = InteropValues.Compact(src);
            p.Ges.Add(ge);
        }
    }

    /// <summary>Lights and zones of render/lighting.json that are not gameplay actors.</summary>
    private static void AddLighting(string root, MapProject p, HashSet<string> ids, UeSchema? schema, RenderCounts counts)
    {
        if (ReadJson(root, "render/lighting.json") is not SvJsonObject lighting)
        {
            return;
        }

        foreach ((string key, string folder) in new[] { ("lights", LightsFolder), ("zones", ZonesFolder) })
        {
            if (lighting[key] is not SvJsonArray list)
            {
                continue;
            }

            foreach (SvJsonObject l in list.OfType<SvJsonObject>())
            {
                if (Str(l["name"]) is not { } id || Str(l["class"]) is not { } cls || ids.Contains(id))
                {
                    continue;
                }

                EditorActor a = FromProps(id, InteropValues.ResolveClass(schema, cls), l["props"], folder, "render/lighting.json");
                if (l["props"] is null)
                {
                    // A static light the converter keeps for the baked lighting alone has no properties: it still has
                    // its place (location and pitch, yaw, roll).
                    a.Location = Vec(l["location"]);
                    if (l["rotation"] is SvJsonArray { Count: 3 } r)
                    {
                        a.Rotation = new Rot((int)PropValues.Num(r[0]), (int)PropValues.Num(r[1]), (int)PropValues.Num(r[2]));
                    }
                }

                p.Actors.Add(a);
                ids.Add(id);
                if (key == "lights")
                {
                    counts.Lights++;
                }
                else
                {
                    counts.Zones++;
                }
            }
        }
    }

    /// <summary>Emitter actors of render/fx.json; every emitter's particle systems are kept for the compile.</summary>
    private static void AddEmitters(string root, MapProject p, HashSet<string> ids, UeSchema? schema, RenderCounts counts)
    {
        if (ReadJson(root, "render/fx.json") is not SvJsonObject fx || fx["emitters"] is not SvJsonObject emitters)
        {
            return;
        }

        string? origin = EmitterBuild.MapOrigin(root);
        foreach ((string id, SvJsonNode node) in emitters)
        {
            if (node is not SvJsonObject e || Str(e["class"]) is not { } cls)
            {
                continue;
            }

            EditorActor? actor = p.FindActor(id);
            if (actor is null)
            {
                if (!ids.Add(id))
                {
                    continue;
                }

                actor = FromProps(id, InteropValues.ResolveClass(schema, cls), e["props"], EmittersFolder, "render/fx.json");
                p.Actors.Add(actor);
                counts.Emitters++;
            }

            if (e["emitters"] is SvJsonArray parts)
            {
                EmitterBuild.WithSystems(actor, EmitterBuild.WithTextureOrigin(parts, origin));
            }
        }
    }

    /// <summary>Plain static mesh actors of the world.glb instance table (gameplay meshes are gameplay actors already).
    /// A mesh the source map gives no collider (decals and the like) gets bCollideActors=False, so a rebuild keeps it
    /// non-colliding.</summary>
    private static void AddStaticMeshes(string root, MapProject p, HashSet<string> ids, CollisionData collision, RenderCounts counts)
    {
        SvJsonNode? gltf = GlbJson.Read(Path.Combine(root, "render", "world.glb"));
        if (gltf is not SvJsonObject g || g["extras"] is not SvJsonObject extras || extras["svmap"] is not SvJsonObject svmap
            || svmap["instances"] is not SvJsonArray instances)
        {
            return;
        }

        string?[] sources = Compile.BakedSource.ColliderSources(collision);
        var colliding = new HashSet<string>(sources.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (SvJsonObject i in instances.OfType<SvJsonObject>())
        {
            if (!IsMeshActor(i) || Str(i["actor"]) is not { } id || ids.Contains(id) || Str(i["mesh"]) is not { } mesh)
            {
                continue;
            }

            var a = new EditorActor(id, "Engine.StaticMeshActor") { Source = BakedSource("render/world.glb", i) };
            a.Editor.Folder = MeshesFolder;
            a.Location = Vec(i["location"]);
            if (PropValues.TryRotator(i["rotation"], out Rot rot))
            {
                a.Rotation = rot;
            }

            if (i["scale"] is { } scale)
            {
                a.DrawScale3D = Vec(scale, Vec3.One);
            }

            if (i["prePivot"] is { } pivot)
            {
                a.PrePivot = Vec(pivot);
            }

            int variant = mesh.IndexOf('#', StringComparison.Ordinal);
            a.Props.Set("StaticMesh", PropValues.AssetRef(variant >= 0 ? mesh[..variant] : mesh));
            if (i["skins"] is SvJsonArray skins)
            {
                a.Props.Set("Skins", new SvJsonArray(skins.Select(s => s is SvJsonString { Value.Length: > 0 } path ? PropValues.AssetRef(path.Value) : SvJsonNode.Null)));
            }

            if (i["hidden"]?.Kind == SvJsonKind.True)
            {
                a.Props.Set("bHidden", SvJsonNode.True);
            }

            if (i["unlit"]?.Kind == SvJsonKind.True)
            {
                a.Props.Set("bUnlit", SvJsonNode.True);
            }

            if (i["ambientGlow"] is SvJsonNumber glow)
            {
                a.Props.Set("AmbientGlow", glow);
            }

            if (colliding.Count > 0 && !colliding.Contains(id))
            {
                a.Props.Set("bCollideActors", SvJsonNode.False);
            }

            p.Actors.Add(a);
            ids.Add(id);
            counts.Meshes++;
        }
    }

    /// <summary>
    /// A world.glb instance that becomes a StaticMeshActor: a placed static mesh. One flagged <c>gameplay</c> is one too
    /// when gameplay.json does not hold its actor (a mesh a conveyor scrolls is its own node for the client to move its
    /// textures); callers leave out the gameplay actors.
    /// </summary>
    public static bool IsMeshActor(SvJsonObject instance) =>
        Str(instance["actor"]) is not null && Str(instance["class"]) == "StaticMeshActor" && Str(instance["kind"]) == "staticmesh"
        && Str(instance["mesh"]) is not null;

    /// <summary>Ambient sounds of audio/audio.json: AmbientSound actors, or the sound of a static mesh.</summary>
    private static void AddSounds(string root, MapProject p, HashSet<string> ids, UeSchema? schema, RenderCounts counts)
    {
        if (ReadJson(root, "audio/audio.json") is not SvJsonObject audio || audio["actors"] is not SvJsonArray actors)
        {
            return;
        }

        foreach (SvJsonObject s in actors.OfType<SvJsonObject>())
        {
            if (Str(s["category"]) != "render" || Str(s["id"]) is not { } id || Str(s["class"]) is not { } cls)
            {
                continue;
            }

            EditorActor? a = ids.Contains(id) ? p.FindActor(id) : null;
            if (a is null)
            {
                a = new EditorActor(id, cls) { Location = Vec(s["location"]), Source = BakedSource("audio/audio.json") };
                a.Editor.Folder = SoundsFolder;
                p.Actors.Add(a);
                ids.Add(id);
                counts.Sounds++;
            }

            if (Str(s["ambientSound"]) is { } sound && !a.Props.Contains("AmbientSound"))
            {
                a.Props.Set("AmbientSound", PropValues.AssetRef(sound));
            }

            SetIfNotDefault(a, schema, "SoundRadius", s["soundRadius"]);
            SetIfNotDefault(a, schema, "SoundVolume", s["soundVolume"]);
            SetIfNotDefault(a, schema, "SoundPitch", s["soundPitch"]);
        }
    }

    private static void SetIfNotDefault(EditorActor a, UeSchema? schema, string key, SvJsonNode? value)
    {
        if (value is null || a.Props.Contains(key))
        {
            return;
        }

        SvJsonNode? def = schema?.DefaultOf(a.Class, key);
        if (def is null || PropValues.Num(def) != PropValues.Num(value))
        {
            a.Props.Set(key, value);
        }
    }

    private static EditorActor FromProps(string id, string cls, SvJsonNode? props, string folder, string file)
    {
        var a = new EditorActor(id, cls) { Source = BakedSource(file) };
        PropSet set = PropSet.FromJson(props);
        InteropValues.TakeTransform(set, a);
        a.Props = set;
        a.Editor.Folder = folder;
        return a;
    }

    /// <summary>The source JSON of a render actor: the file that holds it, then any details of the record.</summary>
    private static string BakedSource(string file, SvJsonObject? details = null)
    {
        var o = new SvJsonObject();
        o.Add(BakedKey, SvJsonNode.String(file));
        foreach (KeyValuePair<string, SvJsonNode> kv in details ?? [])
        {
            o.TryAdd(kv.Key, kv.Value);
        }

        return InteropValues.Compact(o);
    }

    /// <summary>A vector from <c>[x, y, z]</c> or <c>{"X","Y","Z"}</c>.</summary>
    private static Vec3 Vec(SvJsonNode? node, Vec3 fallback = default) => node switch
    {
        SvJsonArray { Count: 3 } a => new Vec3(PropValues.Num(a[0]), PropValues.Num(a[1]), PropValues.Num(a[2])),
        SvJsonObject o => InteropValues.VectorOr(o, fallback),
        _ => fallback,
    };

    private static string? Str(SvJsonNode? n) => n is SvJsonString s ? s.Value : null;

    private static SvJsonNode? ReadJson(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        return File.Exists(path) ? SvJson.Parse(File.ReadAllBytes(path), relative) : null;
    }

    private sealed class RenderCounts
    {
        public int Lights { get; set; }

        public int Zones { get; set; }

        public int Emitters { get; set; }

        public int Meshes { get; set; }

        public int Sounds { get; set; }
    }
}

/// <summary>Imports an SVMap folder (a converted original map or a native one).</summary>
[EditorService]
public sealed class SvMapImporter : IMapImporter
{
    public string Name => "svmap";

    public bool CanImport(string path) => SvMapImport.IsSvMapFolder(path);

    public ImportResult Import(string path, ImportOptions options) => SvMapImport.Import(path, options);
}
