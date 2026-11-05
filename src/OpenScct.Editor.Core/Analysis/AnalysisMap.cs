using System.Text.Json;
using OpenScct.Content;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.AI;
using OpenScct.Game.Lighting;
using OpenScct.Physics.Collision;
using OpenScct.Server;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// A project as OpenSCCT's server sees it, for the analyses that must agree with the game: the project compiled to a
/// scratch SVMap folder, then loaded with the server's own loader (<see cref="MapCatalog.LoadSvMap"/>: actors, collision,
/// GEs, starts) and read with the bots' light model (<see cref="MapAiLight"/>). The scratch folder is deleted once the
/// map is loaded. Collision queries are not thread-safe, so every use goes through <see cref="Gate"/>.
/// </summary>
public sealed class AnalysisMap
{
    private readonly byte[] _lighting;
    private readonly Dictionary<string, NavGraph> _graphs = new(StringComparer.Ordinal);
    private HashSet<string>? _lightNames;
    private List<LightRole>? _lightRoles;

    private AnalysisMap(SvMap svmap, World world, MapInfo info, byte[] lighting, SurfaceMaterials materials, LightmapSurfaces? lightmaps,
        IReadOnlyList<Finding> compileFindings, TimeSpan elapsed)
    {
        SvMap = svmap;
        World = world;
        Info = info;
        _lighting = lighting;
        Albedo = materials.Albedo;
        Unlit = materials.Unlit;
        Lightmaps = lightmaps;
        CompileFindings = compileFindings;
        Elapsed = elapsed;
    }

    /// <summary>The mean albedo (luminance of a texture's opaque texels, 0..1) of each collision material by name, for
    /// the surfaces a spy is seen against; read while the compiled folder still exists.</summary>
    public IReadOnlyDictionary<string, float> Albedo { get; }

    /// <summary>
    /// The collision materials (by name) drawn unlit wherever they are used: their texture at full colour whatever the
    /// light. A BSP surface flagged unlit on its own carries PF_Unlit in its hit's PolyFlags instead.
    /// </summary>
    public IReadOnlySet<string> Unlit { get; }

    /// <summary>
    /// The light the map's lightmaps hold, where they are the map's real bake (null when there is none): a source
    /// map's own lightmaps kept as they are, or a full build's. The analysis compile's fast bake has no shadows, so its
    /// lightmaps are left out and the light model stands in for them.
    /// </summary>
    public LightmapSurfaces? Lightmaps { get; }

    /// <summary>The map's lighting.json (empty when it has none), for the light models built on it.</summary>
    internal ReadOnlySpan<byte> LightingJson => _lighting;

    /// <summary>Held while querying the collision world or building graphs.</summary>
    public object Gate { get; } = new();

    public string MapId => SvMap.Id;

    /// <summary>The compiled map's files as read (gameplay, collision data, traversal).</summary>
    public SvMap SvMap { get; }

    /// <summary>The server world the map was loaded into (never ticked).</summary>
    public World World { get; }

    /// <summary>What the server's loader made: actors by record, starts, physics.</summary>
    public MapInfo Info { get; }

    public MapLoadResult Loaded => Info.Loaded!;

    public CollisionWorld Collision => Info.Physics!.Collision;

    /// <summary>LevelInfo.GEActivationMask: the GE ids that are on at the start of a round.</summary>
    public byte ActivationMask => World.Level.Info.GEActivationMask;

    /// <summary>What the compiler reported (warnings and notes; errors stop the load).</summary>
    public IReadOnlyList<Finding> CompileFindings { get; }

    /// <summary>How long compiling and loading took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Compiles <paramref name="project"/> (fast lighting, no strict validation) and loads it the way the server does.
    /// Throws <see cref="InvalidOperationException"/> when the compile or the load fails.
    /// </summary>
    public static AnalysisMap Build(MapProject project, string? projectFolder, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string scratch = Path.Combine(Path.GetTempPath(), "OpenSCCT Editor", "analysis", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        string folder = Path.Combine(scratch, project.Id);
        try
        {
            progress?.Invoke("analysis: compiling");
            IMapCompiler compiler = EditorServices.Compiler ?? new MapCompiler();
            CompileResult result = compiler.Compile(project, folder, new CompileOptions
            {
                Validate = false,
                FastLighting = true,
                ContentRoot = EditorServices.ContentRoot,
                ProjectFolder = projectFolder,
            });
            if (!result.Ok)
            {
                Finding first = result.Findings.First(f => f.Severity == Severity.Error);
                throw new InvalidOperationException($"the map does not compile: {first.Code}: {first.Message}");
            }

            progress?.Invoke("analysis: loading");
            // Only a kept source map's own atlases are a real bake here (the baked mode adds its own under other names).
            bool kept = project.Baked is { KeepRender: true } && !project.Lighting.RelightImported;
            return Load(folder, result.Findings.Where(f => f.Severity != Severity.Error).ToList(), clock,
                uri => kept && !uri.Contains("_added_lightmap", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>Loads an SVMap folder that is already compiled, the way the server does.</summary>
    public static AnalysisMap Load(string folder)
    {
        bool fast = false;
        string lightingPath = Path.Combine(folder, "render", "lighting.json");
        if (File.Exists(lightingPath))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(lightingPath));
            fast = json.RootElement.TryGetProperty("bake", out JsonElement bake) && bake.TryGetProperty("fast", out JsonElement f) &&
                f.ValueKind == JsonValueKind.True;
        }

        return Load(folder, [], System.Diagnostics.Stopwatch.StartNew(), _ => !fast);
    }

    private static AnalysisMap Load(string folder, IReadOnlyList<Finding> compileFindings, System.Diagnostics.Stopwatch clock,
        Func<string, bool> trustedLightmap)
    {
        SvMap svmap;
        try
        {
            svmap = SvMap.Load(folder, new SvMapLoadOptions { VerifySimHash = false });
        }
        catch (SvMapFormatException e)
        {
            throw new InvalidOperationException($"OpenSCCT cannot load the compiled map: {e.File}: {e.Message}", e);
        }

        var world = new World(0, ENetMode.NM_DedicatedServer);
        MapInfo info = MapCatalog.LoadSvMap(world, svmap, isClient: false);
        string lightingPath = Path.Combine(folder, "render", "lighting.json");
        byte[] lighting = File.Exists(lightingPath) ? File.ReadAllBytes(lightingPath) : [];
        return new AnalysisMap(svmap, world, info, lighting, ReadMaterials(folder), LightmapSurfaces.Load(folder, trustedLightmap), compileFindings,
            clock.Elapsed);
    }

    private sealed record SurfaceMaterials(Dictionary<string, float> Albedo, HashSet<string> Unlit);

    /// <summary>
    /// The mean albedo of each material by its source name (the name the collision materials carry), from the texture
    /// materials.json gives it, and the materials every use of which is drawn unlit. Materials without a readable
    /// texture are left out of the albedo (mid grey to the readers).
    /// </summary>
    private static SurfaceMaterials ReadMaterials(string folder)
    {
        var result = new SurfaceMaterials(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        string path = Path.Combine(folder, "render", "materials.json");
        if (!File.Exists(path))
        {
            return result;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!json.RootElement.TryGetProperty("materials", out JsonElement materials) || materials.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var byTexture = new Dictionary<string, float?>(StringComparer.OrdinalIgnoreCase);
        var lit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty m in materials.EnumerateObject())
        {
            if (!m.Value.TryGetProperty("source", out JsonElement source) || source.GetString() is not { } name ||
                !m.Value.TryGetProperty("flags", out JsonElement flags))
            {
                continue;
            }

            // Each entry is a material as one set of surfaces draws it: a surface's own unlit flag makes only that one unlit.
            (flags.TryGetProperty("unlit", out JsonElement unlit) && unlit.ValueKind == JsonValueKind.True ? result.Unlit : lit).Add(name);

            if (result.Albedo.ContainsKey(name) || !flags.TryGetProperty("texture", out JsonElement texture) || texture.GetString() is not { } file)
            {
                continue;
            }

            if (!byTexture.TryGetValue(file, out float? mean))
            {
                byTexture[file] = mean = MeanLuminance(Path.Combine(folder, "render", file));
            }

            if (mean is { } a)
            {
                result.Albedo[name] = a;
            }
        }

        result.Unlit.ExceptWith(lit);
        return result;
    }

    private static float? MeanLuminance(string png)
    {
        if (!File.Exists(png) || Compile.Output.Png.Decode(File.ReadAllBytes(png)) is not { } image)
        {
            return null;
        }

        double sum = 0;
        long n = 0;
        byte[] rgba = image.Rgba;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] >= 128)
            {
                sum += (0.2126 * rgba[i]) + (0.7152 * rgba[i + 1]) + (0.0722 * rgba[i + 2]);
                n++;
            }
        }

        return n == 0 ? null : (float)(sum / n / 255);
    }

    /// <summary>The names (actor ids) of the lights in the map's lighting, which light states can switch.</summary>
    public IReadOnlySet<string> LightNames => _lightNames ??= ReadLightNames(_lighting);

    /// <summary>The lights that light only the world or only pawns (<see cref="LightRole"/>), in lighting.json order.</summary>
    public IReadOnlyList<LightRole> LightRoles => _lightRoles ??= ReadLightRoles(_lighting);

    private static List<LightRole> ReadLightRoles(byte[] lighting)
    {
        var roles = new List<LightRole>();
        if (lighting.Length == 0)
        {
            return roles;
        }

        using JsonDocument json = JsonDocument.Parse(lighting);
        if (!json.RootElement.TryGetProperty("lights", out JsonElement lights) || lights.ValueKind != JsonValueKind.Array)
        {
            return roles;
        }

        foreach (JsonElement l in lights.EnumerateArray())
        {
            bool Flag(string name) => l.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
            int Int(string name, int fallback) => l.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : fallback;
            int type = Int("ltype", 1), effect = Int("effect", 0);
            bool dynamic = Flag("dynamic");
            double brightness = l.TryGetProperty("brightness", out JsonElement b) && b.TryGetDouble(out double bv) ? bv : 0;
            // The world takes a light baked or drawn live at run time; pawns by the game's actor light rule.
            bool bakes = BakeLight.Bakes(type, Flag("apply_static"), dynamic, Flag("gameplay"), effect);
            bool world = bakes || BakeLight.LightsWorldLive(dynamic, effect, brightness);
            bool pawns = ActorLightRules.LightsActors(Flag("apply_ingame"), dynamic, type, effect);
            if (world == pawns || !l.TryGetProperty("location", out JsonElement at) || at.ValueKind != JsonValueKind.Array ||
                !l.TryGetProperty("name", out JsonElement name))
            {
                continue;
            }

            float radius = l.TryGetProperty("radius", out JsonElement r) ? r.GetSingle() : 64f;
            roles.Add(new LightRole(name.GetString() ?? "", new Vec3(at[0].GetDouble(), at[1].GetDouble(), at[2].GetDouble()),
                ActorLightRules.WorldRadius(radius), world ? LightRoleKind.WorldOnly : LightRoleKind.PawnsOnly, effect == ActorLightRules.LE_Sunlight));
        }

        return roles;
    }

    private static HashSet<string> ReadLightNames(byte[] lighting)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lighting.Length == 0)
        {
            return names;
        }

        using JsonDocument json = JsonDocument.Parse(lighting);
        if (json.RootElement.TryGetProperty("lights", out JsonElement lights) && lights.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement l in lights.EnumerateArray())
            {
                if (l.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } n)
                {
                    names.Add(n);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A new light field of the map (every light in its starting state), as the bots read it: zone lookups and spot
    /// shadow lines through the collision world. Fields are cheap; each analysis takes its own, so light switches it
    /// flips never leak into another.
    /// </summary>
    public AiLightField NewLightField()
    {
        if (_lighting.Length == 0)
        {
            return AiLightField.Unknown;
        }

        using JsonDocument json = JsonDocument.Parse(_lighting);
        return MapAiLight.Parse(json.RootElement, Collision);
    }

    /// <summary>Where built graphs are kept between runs, by map content hash, profile and mask.</summary>
    public static string NavCacheRoot => Path.Combine(EditorCache.Root, "nav");

    /// <summary>The most the graph cache keeps on disk (a large map's graph is about 6 MB, and every collision edit
    /// makes new ones); the graphs used longest ago go first.</summary>
    private const long NavCacheBytes = 256L << 20;

    /// <summary>
    /// The navigation graph of <paramref name="profile"/> (call under <see cref="Gate"/>). Built once per profile and
    /// mask, and kept on disk under the map's sim hash: a large map takes tens of seconds to sample, and an unchanged
    /// map (or one whose edits touched only its looks) loads its graphs in a moment.
    /// </summary>
    public NavGraph Graph(ReachProfile profile, byte mask)
    {
        NavProfile nav = profile.Nav;
        string mapKey = $"{MapId}-{SvMap.SimHash}-m{mask}";
        string key = mapKey + "|" + nav.Key;
        if (_graphs.TryGetValue(key, out NavGraph? graph))
        {
            return graph;
        }

        string file = Path.Combine(NavCacheRoot, SafeName(mapKey + "-" + nav.Key) + ".svmnav");
        graph = ReadGraph(file, mapKey, nav);
        if (graph is null)
        {
            (FVector min, FVector max) = PlayBox();
            graph = NavBuilder.Build(Collision, min, max, nav, GeList(), mask);
            WriteGraph(file, graph, mapKey);
        }

        _graphs[key] = graph;
        return graph;
    }

    private static NavGraph? ReadGraph(string file, string mapKey, NavProfile nav)
    {
        try
        {
            NavGraph? graph;
            using (FileStream s = File.OpenRead(file))
            {
                graph = NavGraph.Read(s, mapKey, nav);
            }

            if (graph is not null)
            {
                // Marks it used, so pruning drops older graphs first.
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            }

            return graph;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteGraph(string file, NavGraph graph, string mapKey)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Environment.ProcessId + ".tmp";
            using (FileStream s = File.Create(temp))
            {
                graph.Write(s, mapKey);
            }

            File.Move(temp, file, overwrite: true);
            PruneNavCache();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A graph that cannot be kept is built again next time.
        }
    }

    /// <summary>Deletes the graphs used longest ago until the cache fits <see cref="NavCacheBytes"/>.</summary>
    private static void PruneNavCache()
    {
        long kept = 0;
        foreach (FileInfo f in new DirectoryInfo(NavCacheRoot).GetFiles("*.svmnav").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            kept += f.Length;
            if (kept > NavCacheBytes)
            {
                f.Delete();
            }
        }
    }

    private static string SafeName(string name)
    {
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] == '|')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    /// <summary>The GEs as the physics world indexed them.</summary>
    public List<GERecord> GeList()
    {
        var list = new List<GERecord>(Info.Physics!.GEs.Count);
        for (int i = 0; i < Info.Physics.GEs.Count; i++)
        {
            list.Add(Info.Physics.GEs[i]);
        }

        return list;
    }

    /// <summary>
    /// The box the graphs sample: every player start, gameplay actor and GE point, widened as the bots' cache widens
    /// it (1200 across, 700 up and down), so rooms with nothing in them near the edge of the map are still covered.
    /// </summary>
    public (FVector Min, FVector Max) PlayBox()
    {
        var points = new List<FVector>();
        foreach (ActorRecord record in Loaded.Gameplay.Actors)
        {
            if (Loaded.ActorOf(record) is { } a && a is not (Light or OpenScct.Core.Engine.Info))
            {
                points.Add(a.Location);
            }
        }

        foreach (GERecord ge in GeList())
        {
            points.Add(ge.P0);
            points.Add(ge.P1);
        }

        if (points.Count == 0)
        {
            points.Add(default);
        }

        return NavBuilder.BoundsOf(points, new FVector(1200f, 1200f, 700f));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A scratch folder left behind is harmless; the next run uses a new one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static Vec3 V(FVector v) => new(v.X, v.Y, v.Z);

    public static FVector F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
