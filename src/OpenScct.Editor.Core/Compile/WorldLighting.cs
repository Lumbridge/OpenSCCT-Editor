using System.Collections.Concurrent;
using System.Numerics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The compile's static lighting: the map's lights as the lightmapper sees them, the zones' ambient, the shadow scene
/// (world surfaces and shadow-casting static meshes), the lightmap bake of the world's surfaces and the vertex light of
/// placed static meshes. The terms are the original editor's (<see cref="BakeLight"/>); the sampling follows the
/// project's <see cref="LightingSettings"/>.
/// </summary>
public sealed class WorldLighting
{
    /// <summary>How far a static mesh vertex's shadow ray starts off the surface, uu (UStaticMesh::Illuminate:
    /// vertex + 4 normal).</summary>
    public const double VertexBias = 4;

    /// <summary>The bounce colour of a surface without a texture.</summary>
    private static readonly Vec3 Grey = new(0.5, 0.5, 0.5);

    private readonly CompileContext _ctx;
    private readonly List<OccluderTriangle> _occluders = new();
    private readonly Dictionary<string, AlphaMask?> _masks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Glb.UePrimitive>?> _meshPrimitives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Vec3> _lightmapAmbient = new();
    private HashSet<string>? _scrolled;
    // Bounce samples revisit the same immutable faces millions of times. Resolve each face once for this bake;
    // resolving the material on every sample repeatedly scans project assets and allocates short-name strings.
    private readonly ConcurrentDictionary<Surface, Lazy<Vec3>> _surfaceAlbedo = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, Lazy<Vec3>> _albedo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The leaf light lists of the compiled world (an authored map's; a baked map's world is the source map's,
    /// whose own lists <see cref="SourceLightLists"/> reads), with a note for each baked light standing in solid
    /// space, which lights nothing as in the original.</summary>
    public LeafLightLists LeafLists()
    {
        if (_leafLists is not null)
        {
            return _leafLists;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        // With only the baked collision kept, the world above is that collision's added brushes in an empty world, while
        // the render is rebuilt from every brush: its lights flood the world rebuilt from every brush, with its zones.
        WorldModel world = _ctx.Baked is { KeepCollision: true, KeepRender: false } && RebuiltWorld() is { } whole ? whole : _ctx.World;
        _leafLists = LeafLightLists.Build(world, Lights);
        _ctx.Count("lightmap.leafLists.entries", _leafLists.Listed);
        _ctx.Count("lightmap.leafLists.ms", clock.ElapsedMilliseconds);
        WarnLeafLists(_leafLists, _leafLists.InSolid, _leafLists.Cut);
        return _leafLists;
    }

    private LeafLightLists? _leafLists;

    /// <summary>
    /// The leaf light lists of an imported map's whole world, its baked brushes included, rebuilt from its brushes with
    /// the zones of its zone actors as an authored build of them would be: the lists for its lights that neither the
    /// .sdc's stored lists nor its leaf runs cover (<see cref="SourceLightLists"/>). Null when the project holds no
    /// baked brushes (nothing to rebuild the source world from). Built once per compile.
    /// </summary>
    public LeafLightLists? WholeWorldLists()
    {
        if (_wholeWorldBuilt)
        {
            return _wholeWorldLists;
        }

        _wholeWorldBuilt = true;
        if (RebuiltWorld() is not { } world)
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _wholeWorldLists = LeafLightLists.Build(world, Lights);
        _ctx.Count("lightmap.leafLists.wholeWorldMs", clock.ElapsedMilliseconds);
        _ctx.Count("lightmap.leafLists.wholeWorldEntries", _wholeWorldLists.Listed);
        return _wholeWorldLists;
    }

    private LeafLightLists? _wholeWorldLists;
    private bool _wholeWorldBuilt;

    /// <summary>The map's whole world, baked brushes included, rebuilt with the zones of its zone actors as an authored
    /// build of them would be; null when the project holds no baked brushes. Built once.</summary>
    private WorldModel? RebuiltWorld()
    {
        if (_rebuiltWorldDone)
        {
            return _rebuiltWorld;
        }

        _rebuiltWorldDone = true;
        if (!_ctx.Project.Brushes.Any(BakedContent.IsBaked))
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ctx.Progress($"light lists: rebuilding the source world from {_ctx.Project.Brushes.Count} brushes");
        var seeds = _ctx.Project.Actors.Where(_ctx.Rules.IsZoneActor).Select(a => new ZoneSeed(a.Id, a.Location, _ctx.Rules.IsSkyZone(a))).ToList();
        _rebuiltWorld = WorldModel.Build(_ctx.Project.Brushes, seeds, worldSolid: true, ZoneNumbering.For(_ctx.Options.Target));
        _ctx.Count("lightmap.leafLists.rebuiltWorldMs", clock.ElapsedMilliseconds);
        return _rebuiltWorld;
    }

    private WorldModel? _rebuiltWorld;
    private bool _rebuiltWorldDone;

    /// <summary>Warns for the lights of <paramref name="lists"/> that stand in solid space or whose flood the guard cut.</summary>
    public void WarnLeafLists(LeafLightLists lists, IEnumerable<BakeLight> inSolid, IEnumerable<BakeLight> cut)
    {
        _ctx.Count("lightmap.leafLists.mostSteps", lists.MostSteps);
        foreach (BakeLight l in inSolid)
        {
            _ctx.Warn("lighting.light-in-solid", $"light {l.Id} stands inside solid space, so it lights no surface (the original's light lists " +
                "leave it out too); move it into the room", ObjectRef.Actor(l.Id));
        }

        foreach (BakeLight l in cut)
        {
            _ctx.Warn("lighting.leaf-flood-cut", $"light {l.Id} reaches through so many leaf portals that its flood was stopped early, so " +
                "surfaces it reaches far away may stay unlit by it; a smaller LightRadius or fewer small brushes around it helps",
                ObjectRef.Actor(l.Id));
        }
    }

    /// <summary>The surface of each world occluder triangle's <see cref="OccluderTriangle.Source"/>, so bounce rays find
    /// the lightmap they land on.</summary>
    private readonly List<Surface> _sources = new();

    private readonly LightmapBaker _baker = new();
    private bool _cacheRead;
    private TriangleBvh? _bvh;
    private readonly WindowLightTransmission _windowLight;
    public bool CaptureWindows { get; set; }
    public OpenScct.Content.WindowLightTransfer WindowResponse { get; } = new();

    public WorldLighting(CompileContext ctx)
    {
        _ctx = ctx;
        _windowLight = new WindowLightTransmission(ctx.Project.Actors.Where(a => a.Window is not null && !ctx.Rules.Flag(a, "bHidden")));
        WindowResponse.Panes.AddRange(_windowLight.Panes);
        Settings = ctx.Options.FastLighting ? Fast(ctx.Project.Lighting) : ctx.Project.Lighting;
        Quality = ctx.Options.FastLighting ? new BakeQuality { Shadows = false } : BakeQuality.From(Settings);
        double levelBrightness = LevelBrightness(ctx.Project, ctx.Classes, ctx.LevelInfoClass);

        // Render lights and gameplay lights (STriggerLight) in project order, as lighting.json lists them, and the
        // cooked-out lights that only bake.
        var gameplayActors = new HashSet<Actor>(ctx.Gameplay, ReferenceEqualityComparer.Instance);
        var renderActors = new HashSet<Actor>(ctx.Render, ReferenceEqualityComparer.Instance);
        var bakeOnlyActors = new HashSet<Actor>(ctx.BakeOnlyLights, ReferenceEqualityComparer.Instance);
        ActorRules r = ctx.Rules;
        foreach (Actor a in ctx.Project.Actors)
        {
            bool bakeOnly = bakeOnlyActors.Contains(a);
            bool gameplay = gameplayActors.Contains(a) || (bakeOnly && r.CategoryOf(a) == ActorCategory.Gameplay);
            if (!r.IsLight(a) || (!gameplay && !bakeOnly && !renderActors.Contains(a)))
            {
                continue;
            }

            BakeLight light = LightOf(r, a, ctx.ZoneAt(a.Location), levelBrightness, Settings.BakeDefaultLightRadius) with
            {
                Gameplay = gameplay,
                BakeOnly = bakeOnly,
            };
            Lights.Add(light);
            if (light.Baked && light.Animated)
            {
                ctx.Info("lighting.animated", $"light {a.Id}: its LightType changes over time; the bake takes its starting value",
                    ObjectRef.Actor(a.Id));
            }

            if (light.Baked && light.UnknownEffect)
            {
                ctx.Info("lighting.effect", $"light {a.Id}: LightEffect {light.Effect} has no baked form of its own; it bakes as a point light",
                    ObjectRef.Actor(a.Id));
            }
        }
    }

    public LightingSettings Settings { get; }

    public BakeQuality Quality { get; }

    public List<BakeLight> Lights { get; } = new();

    /// <summary>The shadow scene (built on first use, after every occluder is added).</summary>
    public TriangleBvh Occluders => _bvh ??= TriangleBvh.Build(_occluders);

    /// <summary>The light's LightEffect; without class data a Spotlight is LE_Spotlight and a SunLight LE_Sunlight,
    /// as their class defaults are.</summary>
    public static int EffectOf(CompileContext ctx, Actor a) => EffectOf(ctx.Rules, a);

    public static int EffectOf(ActorRules r, Actor a)
    {
        if (r.Value(a, "LightEffect") is SvJsonNumber n)
        {
            return (int)PropValues.Num(n);
        }

        return r.IsA(a, "Engine.SunLight") ? ActorLightRules.LE_Sunlight
            : r.IsA(a, "Engine.Spotlight") ? ActorLightRules.LE_Spotlight
            : 0;
    }

    /// <summary>
    /// A Light actor as the bake sees it (the compile's lights, and the .sdc light recovery's, come from here, so a
    /// recovered light rebakes as it was fitted). <paramref name="defaultRadius"/> stands in for the class default of
    /// a light that stores no LightRadius (<see cref="LightingSettings.BakeDefaultLightRadius"/>).
    /// </summary>
    public static BakeLight LightOf(ActorRules r, Actor a, int zone, double levelBrightness, double? defaultRadius) => new()
    {
        Actor = a,
        Id = a.Id,
        Location = a.Location,
        Direction = a.Rotation.Axes().Forward,
        Brightness = r.Number(a, "LightBrightness", 64),
        LightRadius = a.Props["LightRadius"] is null && defaultRadius is { } d ? d : r.Number(a, "LightRadius", 64),
        Hue = (int)r.Number(a, "LightHue"),
        Saturation = (int)r.Number(a, "LightSaturation", 255),
        Effect = EffectOf(r, a),
        Cone = (byte)Math.Clamp(r.Number(a, "LightCone", 128), 0, 255),
        Zone = zone,
        OwnZoneOnly = r.Flag(a, "bAffectOwnZoneOnly"),
        Type = (int)r.Number(a, "LightType", 1),
        Period = (int)r.Number(a, "LightPeriod", 32),
        Phase = (int)r.Number(a, "LightPhase"),
        Dynamic = r.Flag(a, "bDynamicLight") || (r.Flag(a, "bMovable") && !r.Flag(a, "bStatic")),
        ApplyStatic = r.Value(a, "bApplyToStaticLighting") is null || r.Flag(a, "bApplyToStaticLighting"),
        SpecialLit = r.Flag(a, "bSpecialLit"),
        LevelBrightness = levelBrightness,
    };

    /// <summary>LevelInfo.Brightness, which scales every light's colour.</summary>
    public static double LevelBrightness(MapProject p, ClassDefaults classes, string levelInfoClass) =>
        p.LevelInfo["Brightness"] is SvJsonNumber lb ? PropValues.Num(lb)
        : classes.Default(levelInfoClass, "Brightness") is SvJsonNumber db ? PropValues.Num(db) : 1;

    /// <summary>`--fast`: a quarter of the density (a sixteenth of the texels) and no shadows, for a quick look in the
    /// game.</summary>
    private static LightingSettings Fast(LightingSettings s)
    {
        LightingSettings fast = s.Clone();
        fast.Mode = LightmapMode.Quality;
        fast.Density = Math.Max(0.25, s.Density / 4);
        fast.Bounce = 0;
        return fast;
    }

    // ---- ambient ----------------------------------------------------------------------------------------------------

    /// <summary>A zone's AmbientHue, AmbientSaturation and AmbientBrightness (the LevelInfo's for zone 0).</summary>
    private (int Hue, int Saturation, int Brightness) AmbientOf(int zone)
    {
        string? id = zone > 0 && zone < _ctx.ZoneActors.Count ? _ctx.ZoneActors[zone] : null;
        Actor? actor = id is null ? null : _ctx.Gameplay.Find(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (actor is not null)
        {
            ActorRules r = _ctx.Rules;
            return ((int)r.Number(actor, "AmbientHue"), (int)r.Number(actor, "AmbientSaturation", 255), (int)r.Number(actor, "AmbientBrightness"));
        }

        PropSet li = _ctx.Project.LevelInfo;
        int brightness = li["AmbientBrightness"] is SvJsonNumber b ? (int)PropValues.Num(b)
            : _ctx.Classes.Default(_ctx.LevelInfoClass, "AmbientBrightness") is SvJsonNumber d ? (int)PropValues.Num(d) : 0;
        return (li.GetInt("AmbientHue"), li["AmbientSaturation"] is null ? 255 : li.GetInt("AmbientSaturation"), brightness);
    }

    /// <summary>A zone's m_RefAmbientColor (lighting.json's zone ambient, which lights pawns and static meshes).</summary>
    public (int R, int G, int B) ZoneAmbient(int zone)
    {
        (int h, int s, int v) = AmbientOf(zone);
        return LightColour.ZoneAmbientBytes(h, s, v);
    }

    /// <summary>A zone's ambient fill of lightmap texels, stored units (asked from every bake thread).</summary>
    public Vec3 LightmapAmbient(int zone) => _lightmapAmbient.GetOrAdd(zone, z =>
    {
        (int h, int s, int v) = AmbientOf(z);
        return LightColour.LightmapAmbient(h, s, v);
    });

    // ---- shadow scene -----------------------------------------------------------------------------------------------

    /// <summary>World surfaces as occluders: masked ones block only where their texture is solid, sky surfaces (the
    /// sky zone, FakeBackdrop) let sunlight in.</summary>
    public void AddSurfaces(IEnumerable<Poly> polys, Func<Poly, bool> inSky)
    {
        foreach (Poly p in polys)
        {
            SurfaceLook look = SurfaceLook.Resolve(_ctx.Library, p.Surface.Material, p.Surface.Flags);
            OccluderFlags flags = inSky(p) || (p.Surface.Flags & PolyFlags.FakeBackdrop) != 0 ? OccluderFlags.Sky : OccluderFlags.None;
            AlphaMask? mask = look.Masked ? Mask(look.TextureFile) : null;
            Surface s = p.Surface;
            int source = _sources.Count;
            _sources.Add(s);
            Vector2 Uv(Vec3 v) => new(
                (float)((Vec3.Dot(v - s.TextureBase, s.TextureU) + s.PanU) / look.USize),
                (float)((Vec3.Dot(v - s.TextureBase, s.TextureV) + s.PanV) / look.VSize));
            for (int i = 1; i + 1 < p.Verts.Length; i++)
            {
                Vec3 a = p.Verts[0], b = p.Verts[i], c = p.Verts[i + 1];
                // An occluder's front is the side its corners' cross product points to: the surface's front.
                if (Vec3.Dot(Vec3.Cross(b - a, c - a), p.Plane.N) < 0)
                {
                    (b, c) = (c, b);
                }

                var t = new OccluderTriangle(a, b, c, flags, source);
                _occluders.Add(mask is null ? t : t with { Mask = mask, UvA = Uv(a), UvB = Uv(b), UvC = Uv(c) });
            }
        }

        _bvh = null;
    }

    /// <summary>Occluders read from elsewhere (a baked source map's own world).</summary>
    public void AddOccluders(IEnumerable<OccluderTriangle> triangles)
    {
        _occluders.AddRange(triangles);
        _bvh = null;
    }

    /// <summary>Actors drawn with a static mesh: render actors', and gameplay actors' that store their own StaticMesh
    /// (as the converter does; the game draws gameplay objects with their class's mesh itself).</summary>
    public IEnumerable<(Actor Actor, bool Gameplay, string Path)> MeshActors()
    {
        foreach (Actor a in _ctx.Render.Concat(_ctx.Gameplay))
        {
            bool gameplay = _ctx.GameplayIds.Contains(a.Id);
            SvJsonNode? meshProp = gameplay ? a.Props["StaticMesh"] : _ctx.Rules.Value(a, "StaticMesh");
            if (a.Shape is null && PropValues.AssetPath(meshProp) is { } path && (int)_ctx.Rules.Number(a, "DrawType") == 8)
            {
                yield return (a, gameplay, path);
            }
        }
    }

    /// <summary>True when a build draws the placed static mesh with its baked vertex light: not a gameplay object, not
    /// hidden and not scrolled by a conveyor (the game lights those at run time).</summary>
    public bool DrawsVertexLit(Actor a, bool gameplay) => !gameplay && !IsScrolled(a) && !_ctx.Rules.Flag(a, "bHidden");

    /// <summary>True for a mesh actor a conveyor scrolls: the game needs it as a node of its own (a gameplay instance).</summary>
    public bool IsScrolled(Actor a)
    {
        if (_scrolled is null)
        {
            _scrolled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Actor c in _ctx.Gameplay.Where(ConveyorDesign.Is))
            {
                _scrolled.UnionWith(ConveyorDesign.Read(c).ScrollTargets.Select(t => t.Target).Where(id => id is { Length: > 0 }));
            }
        }

        return _scrolled.Contains(a.Id);
    }

    /// <summary>A library mesh's primitives in UE space, or null when no converted map or project asset ships it.</summary>
    public List<Glb.UePrimitive>? MeshPrimitives(string asset)
    {
        if (!_meshPrimitives.TryGetValue(asset, out List<Glb.UePrimitive>? prims))
        {
            LibraryMesh? lib = _ctx.Library.FindMesh(asset);
            _meshPrimitives[asset] = prims = lib is not null && File.Exists(lib.GlbPath) ? _ctx.Library.Glb(lib.GlbPath).ReadUePrimitives(lib.Mesh) : null;
        }

        return prims;
    }

    /// <summary>Every placed static mesh that is not a gameplay object, as occluders (<see cref="AddMesh"/>).</summary>
    public void AddMeshOccluders()
    {
        foreach ((Actor a, bool gameplay, string path) in MeshActors())
        {
            if (!gameplay && MeshPrimitives(path) is { } prims)
            {
                AddMesh(a, prims);
            }
        }
    }

    /// <summary>A placed static mesh as an occluder, when it casts shadows (bShadowCast, on by default for static mesh
    /// actors): the original's shadow pass drew those and no others.</summary>
    public void AddMesh(Actor a, IReadOnlyList<Glb.UePrimitive> prims)
    {
        if (!_ctx.Rules.Flag(a, "bShadowCast") || _ctx.Rules.Flag(a, "bHidden"))
        {
            return;
        }

        bool mirrored = MeshMirrored(a);
        foreach (Glb.UePrimitive p in prims)
        {
            LibraryMaterial? lib = p.Material is null ? null : _ctx.Library.FindMaterial(p.Material);
            AlphaMask? mask = lib is not null && Flag(lib.Flags, "masked") && lib.Flags["texture"]?.GetValue<string>() is { } tex
                ? Mask(Path.GetFullPath(Path.Combine(lib.RenderDir, tex)))
                : null;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int i0 = p.Indices[t], i1 = p.Indices[t + 1], i2 = p.Indices[t + 2];
                // Front by the vertex normals, then turned over again by a mirroring scale.
                Vec3 face = Vec3.Cross(p.Positions[i1] - p.Positions[i0], p.Positions[i2] - p.Positions[i0]);
                if ((Vec3.Dot(face, p.Normals[i0] + p.Normals[i1] + p.Normals[i2]) < 0) != mirrored)
                {
                    (i1, i2) = (i2, i1);
                }

                var tri = new OccluderTriangle(_ctx.Rules.LocalToWorld(a, p.Positions[i0]), _ctx.Rules.LocalToWorld(a, p.Positions[i1]),
                    _ctx.Rules.LocalToWorld(a, p.Positions[i2]), OccluderFlags.None, -1);
                if (mask is not null && p.Uvs is { } uv)
                {
                    tri = tri with
                    {
                        Mask = mask,
                        UvA = new Vector2(uv[2 * i0], uv[(2 * i0) + 1]),
                        UvB = new Vector2(uv[2 * i1], uv[(2 * i1) + 1]),
                        UvC = new Vector2(uv[2 * i2], uv[(2 * i2) + 1]),
                    };
                }

                _occluders.Add(tri);
            }
        }

        _bvh = null;
    }

    private bool MeshMirrored(Actor a)
    {
        Vec3 s = _ctx.Rules.Scale(a);
        return s.X * s.Y * s.Z < 0;
    }

    private AlphaMask? Mask(string? textureFile)
    {
        if (textureFile is null)
        {
            return null;
        }

        if (!_masks.TryGetValue(textureFile, out AlphaMask? mask))
        {
            var image = File.Exists(textureFile) ? Png.Decode(File.ReadAllBytes(textureFile)) : null;
            _masks[textureFile] = mask = image is { } im ? AlphaMask.FromRgba(im.Width, im.Height, im.Rgba) : null;
        }

        return mask;
    }

    private static bool Flag(System.Text.Json.Nodes.JsonObject flags, string key) =>
        flags[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out bool b) && b;

    // ---- bakes ------------------------------------------------------------------------------------------------------

    /// <summary>Lightmaps for the lit world surfaces: charts by the settings, packed into atlases, each with the lights
    /// its faces' leaves list when <paramref name="lists"/> is given.</summary>
    public LightmapResult BakeSurfaces(IReadOnlyList<LitPoly> polys, LeafLightLists? lists = null)
    {
        List<LightmapChart> charts = ChartBuilder.Build(polys, Settings);
        LightmapResult result = Bake(charts, "lightmap", scene => _baker.Bake(scene, Quality, Settings.AtlasSize, slot: "surfaces"),
            lists is null ? null : lists.Admits);
        _ctx.Count("lightmap.atlases", result.Atlases.Count);
        return result;
    }

    /// <summary>Lightmaps laid out by <paramref name="layout"/> instead of the project's settings (the .sdc export lays
    /// them out one per brush face, as the stock editor does), each with the lights its faces' leaves list when
    /// <paramref name="lists"/> is given.</summary>
    public LightmapResult BakeSurfaces(IReadOnlyList<LitPoly> polys, LightingSettings layout, LeafLightLists? lists = null)
    {
        List<LightmapChart> charts = ChartBuilder.Build(polys, layout);
        return Bake(charts, "lightmap", scene => _baker.Bake(scene, Quality, layout.AtlasSize, slot: "surfaces"), lists is null ? null : lists.Admits);
    }

    /// <summary>Relights a baked source map's own lightmaps in place (its charts keep their atlas places), each with
    /// the lights its source surfaces list when <paramref name="lists"/> is given.</summary>
    public LightmapResult BakeInPlace(IReadOnlyList<LightmapChart> charts, IReadOnlyList<(int Width, int Height)> atlases, LightLists? lists = null) =>
        Bake(charts, "lightmap.source", scene => _baker.BakeInPlace(scene, Quality, atlases, slot: "source"), lists is null ? null : lists.Admits);

    /// <summary>One bake on the compile's baker. The shadow cache of the project's last build is read first and written
    /// back after, so a build after a light edit retraces only what the edit touched.</summary>
    private LightmapResult Bake(IReadOnlyList<LightmapChart> charts, string stats, Func<BakeScene, LightmapResult> bake,
        Func<LightmapChart, BakeLight, bool>? admits = null)
    {
        string? cache = CacheFile();
        if (cache is not null && !_cacheRead)
        {
            _baker.Load(cache);
            _cacheRead = true;
        }

        LightmapResult result = bake(Scene(charts, admits));
        Count(result.Stats, stats);
        if (cache is not null)
        {
            try
            {
                _baker.Save(cache);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _ctx.Info("lighting.cache", $"the shadow cache was not saved: {e.Message}");
            }
        }

        return result;
    }

    /// <summary>The project's shadow cache file (in the editor's cache folder, named by the project folder), or null for
    /// a build without shadows or a project that has no folder.</summary>
    private string? CacheFile()
    {
        if (!Quality.Shadows || _ctx.Options.ProjectFolder is not { } folder)
        {
            return null;
        }

        byte[] id = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()));
        return Path.Combine(Services.EditorCache.Root, "bake", Convert.ToHexString(id, 0, 8) + ".bin");
    }

    /// <summary>What a bake of <paramref name="charts"/> reads: the lights, the shadow scene, the zones' ambient, and for
    /// bounce light the lightmap each world occluder is and its material's colour.</summary>
    public BakeScene Scene(IReadOnlyList<LightmapChart> charts, Func<LightmapChart, BakeLight, bool>? admits = null)
    {
        var pieces = new Dictionary<Surface, List<(int Chart, int Piece)>>(ReferenceEqualityComparer.Instance);
        for (int c = 0; c < charts.Count; c++)
        {
            for (int p = 0; p < charts[c].Pieces.Count; p++)
            {
                if (charts[c].Pieces[p].Source is not Poly poly)
                {
                    continue;
                }

                if (!pieces.TryGetValue(poly.Surface, out List<(int, int)>? list))
                {
                    pieces[poly.Surface] = list = new List<(int, int)>();
                }

                list.Add((c, p));
            }
        }

        (int Chart, int Piece)? ChartAt(int source, Vec3 at)
        {
            if (source < 0 || source >= _sources.Count || !pieces.TryGetValue(_sources[source], out List<(int Chart, int Piece)>? list))
            {
                return null;
            }

            // A face can be cut into several pieces and tiles: the one under the point.
            (int, int)? best = null;
            double bestD = double.MaxValue;
            foreach ((int c, int p) in list)
            {
                TexelPoint t = charts[c].TexelOf(at), near = charts[c].Pieces[p].Nearest(t);
                double d = ((near.X - t.X) * (near.X - t.X)) + ((near.Y - t.Y) * (near.Y - t.Y));
                if (d < bestD)
                {
                    (best, bestD) = ((c, p), d);
                }
            }

            return best;
        }

        return new BakeScene
        {
            Charts = charts,
            Lights = Lights,
            Occluders = Occluders,
            Transmission = _windowLight.IsEmpty ? null : _windowLight.Filter,
            TransmissionPath = _windowLight.IsEmpty ? null : _windowLight.Path,
            CaptureWindows = CaptureWindows,
            Ambient = LightmapAmbient,
            Albedo = (_, piece) => piece.Source is Poly poly ? Albedo(poly.Surface) : Grey,
            ChartAt = ChartAt,
            Admits = admits,
        };
    }

    /// <summary>The mean colour of a surface's texture (its opaque texels), mid grey without one.</summary>
    private Vec3 Albedo(Surface s) => _surfaceAlbedo.GetOrAdd(s,
        static (surface, lighting) => new Lazy<Vec3>(() => lighting.ResolveAlbedo(surface)), this).Value;

    private Vec3 ResolveAlbedo(Surface s)
    {
        string? file = SurfaceLook.Resolve(_ctx.Library, s.Material, s.Flags).TextureFile;
        // ConcurrentDictionary can run its factory more than once. Only the winning Lazy decodes the texture,
        // so surfaces sharing a large image do not all allocate their own decoder buffers at the first bounce.
        return file is null ? Grey : _albedo.GetOrAdd(file, static f => new Lazy<Vec3>(() => ReadAlbedo(f))).Value;
    }

    private static Vec3 ReadAlbedo(string file)
    {
        if (!File.Exists(file) || Png.Decode(File.ReadAllBytes(file)) is not { } image)
        {
            return Grey;
        }

        double r = 0, g = 0, b = 0;
        long n = 0;
        for (int i = 0; i + 3 < image.Rgba.Length; i += 4)
        {
            if (image.Rgba[i + 3] >= 128)
            {
                (r, g, b, n) = (r + image.Rgba[i], g + image.Rgba[i + 1], b + image.Rgba[i + 2], n + 1);
            }
        }

        return n == 0 ? Grey : new Vec3(r / n / 255, g / n / 255, b / n / 255);
    }

    private void Count(BakeStats stats, string prefix)
    {
        _ctx.Count($"{prefix}.charts", stats.Charts);
        _ctx.Count($"{prefix}.texels", stats.Texels);
        _ctx.Count($"{prefix}.rays", stats.Rays);
        _ctx.Count($"{prefix}.shadowJobs", stats.Jobs);
        _ctx.Count($"{prefix}.cachedJobs", stats.CachedJobs);
        _ctx.Count($"{prefix}.ms", stats.Milliseconds);
    }

    /// <summary>
    /// The baked vertex light of a placed static mesh, RGBA8 per vertex of each primitive (stored units: the game shows
    /// twice the colour, plus the zone's ambient it adds itself). The static mesh term of each baked light of the same
    /// special-lit kind, times the actor's ScaleGlow; one shadow ray per vertex from 4 uu off the surface, only for
    /// actors with bStaticLighting (UStaticMesh::Illuminate, 0x110e8190). Null when no light reaches the mesh.
    /// </summary>
    public Dictionary<int, byte[]>? VertexLight(Actor a, IReadOnlyList<Glb.UePrimitive> prims)
    {
        ActorRules r = _ctx.Rules;
        bool specialLit = r.Flag(a, "bSpecialLit");
        bool shadows = Quality.Shadows && r.Flag(a, "bStaticLighting");
        double glow = r.Number(a, "ScaleGlow", 1);
        Vec3 scale = r.Scale(a);
        var world = prims.Select(p => p.Positions.Select(v => r.LocalToWorld(a, v)).ToArray()).ToList();
        Aabb bounds = Aabb.Of(world.SelectMany(w => w));
        List<BakeLight> lights = Lights.Where(l => l.Baked && l.SpecialLit == specialLit && l.Reaches(bounds)).ToList();
        if (lights.Count == 0 || bounds.Min.X > bounds.Max.X)
        {
            return null;
        }

        TriangleBvh bvh = Occluders;
        var result = new Dictionary<int, byte[]>();
        for (int pi = 0; pi < prims.Count; pi++)
        {
            Glb.UePrimitive p = prims[pi];
            var rgba = new byte[p.Positions.Length * 4];
            var captured = CaptureWindows && shadows && !_windowLight.IsEmpty ? new OpenScct.Content.WindowLightTransfer.Sample?[p.Positions.Length] : null;
            Vec3[] w = world[pi];
            Parallel.For(0, p.Positions.Length, i =>
            {
                // Normals turn with the actor; a non-uniform scale bends them by its inverse.
                Vec3 local = new Vec3(p.Normals[i].X / scale.X, p.Normals[i].Y / scale.Y, p.Normals[i].Z / scale.Z);
                Vec3 n = a.Rotation.Rotate(local).Normalized();
                Vec3 sum = Vec3.Zero;
                Vec3 plain = Vec3.Zero;
                var terms = captured is null ? null : new List<OpenScct.Content.WindowLightTransfer.Term>();
                foreach (BakeLight l in lights)
                {
                    double f = l.Vertex(w[i], n);
                    if (f <= 0 || (l.OwnZoneOnly && l.Zone != _ctx.ZoneAt(w[i] + (n * VertexBias))))
                    {
                        continue;
                    }

                    if (shadows && !VertexLit(l, w[i], n, bvh))
                    {
                        continue;
                    }

                    WindowLightPath? path = shadows ? _windowLight.Path(l, w[i]) : null;
                    sum += l.Colour.Scale(path?.Colour ?? Vec3.One) * f;
                    if (path is { Panes.Length: > 0 })
                    {
                        Vec3 coefficient = l.Colour * (f * glow);
                        terms?.Add(new() { Panes = path.Panes, Colour = [(float)coefficient.X, (float)coefficient.Y, (float)coefficient.Z] });
                    }
                    else plain += l.Colour * (f * glow);
                }

                sum *= glow;
                if (captured is not null && terms is { Count: > 0 })
                    captured[i] = new() { Base = [(float)plain.X, (float)plain.Y, (float)plain.Z],
                        Direct = [(float)sum.X, (float)sum.Y, (float)sum.Z], Initial = [(float)sum.X, (float)sum.Y, (float)sum.Z], Terms = terms };
                rgba[4 * i] = Byte(sum.X);
                rgba[(4 * i) + 1] = Byte(sum.Y);
                rgba[(4 * i) + 2] = Byte(sum.Z);
                rgba[(4 * i) + 3] = 255;
            });
            result[p.Index] = rgba;
            if (captured is not null && captured.Any(s => s is not null))
            {
                var batch = new OpenScct.Content.WindowLightTransfer.Batch();
                batch.Resources.Add(new() { Kind = "mesh", Name = a.Id, Surface = p.Index });
                var used = new HashSet<(Vec3, Vec3)>();
                for (int i = 0; i < captured.Length; i++)
                {
                    if (captured[i] is not { } sample || !used.Add((p.Positions[i], p.Normals[i]))) continue;
                    Vec3 v = p.Positions[i], n = p.Normals[i];
                    sample.Destinations.Add(new() { Element = i, Point = [(float)(v.X / 100), (float)(v.Z / 100), (float)(v.Y / 100)],
                        Normal = [(float)n.X, (float)n.Z, (float)n.Y] });
                    batch.Samples.Add(sample);
                }
                WindowResponse.Batches.Add(batch);
            }
        }

        return result;
    }

    /// <summary>
    /// The baked vertex light at one point of a mesh the editor builds itself (a conveyor's look), in stored units and
    /// without the zone ambient the game adds, as <see cref="VertexLight"/> lights a placed static mesh that is not
    /// special-lit and has bStaticLighting.
    /// </summary>
    public Vec3 VertexAt(Vec3 p, Vec3 n)
    {
        Vec3 sum = Vec3.Zero;
        foreach (BakeLight l in Lights)
        {
            if (!l.Baked || l.SpecialLit)
            {
                continue;
            }

            double f = l.Vertex(p, n);
            if (f <= 0 || (l.OwnZoneOnly && l.Zone != _ctx.ZoneAt(p + (n * VertexBias))))
            {
                continue;
            }

            if (Quality.Shadows && !VertexLit(l, p, n, Occluders))
            {
                continue;
            }

            sum += l.Colour.Scale(Quality.Shadows ? _windowLight.Filter(l, p) : Vec3.One) * f;
        }

        return sum;
    }

    /// <summary>A stored vertex light value as a colour byte.</summary>
    public static byte StoreVertex(double stored) => Byte(stored);

    private static bool VertexLit(BakeLight l, Vec3 v, Vec3 n, TriangleBvh bvh)
    {
        Vec3 origin = v + (n * VertexBias);
        if (l.IsSun)
        {
            return !bvh.Occluded(LightmapBaker.F(origin), LightmapBaker.F(-l.Direction.Normalized()), (float)BakeLight.SunTraceLength, sun: true);
        }

        Vec3 d = l.Location - origin;
        double len = d.Length;
        return len < 1e-3 || !bvh.Occluded(LightmapBaker.F(origin), LightmapBaker.F(d / len), (float)(len - 0.5), sun: false);
    }

    private static byte Byte(double stored) => (byte)Math.Round(Math.Clamp(stored, 0, 1) * 255);
}
