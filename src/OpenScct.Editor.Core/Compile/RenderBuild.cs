using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The render group: <c>render/world.glb</c>, <c>materials.json</c>, <c>lighting.json</c>, <c>fx.json</c> and textures.
/// <para>The world is drawn as converted maps draw theirs: the CSG's visible surfaces (T-junctions mended) form the
/// <c>role: "bsp"</c> nodes, one primitive per material and lightmap atlas, with the baked lightmap on TEXCOORD_1
/// (<see cref="WorldLighting"/>); the sky zone's surfaces form a second, <c>sky: true</c> node, and unlit surfaces
/// carry no lightmap. Static meshes of placed actors come from the project's own assets or the converted maps'
/// libraries; each lit one is drawn through a <c>#vc</c> variant carrying its baked vertex light. Materials and textures
/// come from the converted map that ships them (matched by path), else a generated grid texture stands in.</para>
/// <para>In baked mode the source map's render files are kept: its glb gains the authored surfaces and meshes, its
/// instance table follows the project's actors, its material table gains the new materials, and its lightmaps are
/// relit in place when the project asks for that.</para>
/// </summary>
public sealed partial class RenderBuild
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    private readonly CompileContext _ctx;
    private readonly Glb _glb;
    private readonly JsonObject _materials = new();
    private readonly JsonObject _gameplay = new();
    private readonly JsonArray _vertexLit = new();
    private readonly JsonArray _instances = new();
    private readonly List<int> _library = new();
    private readonly Dictionary<string, MaterialUse> _materialUses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _importedMeshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _copiedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _instanced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _importedMeshIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _variants = new(StringComparer.Ordinal);
    private readonly JsonArray _lightmapped = new();
    private readonly List<string> _lightmapUris = new();
    private WorldLighting _lighting = null!;
    private List<Poly> _surfaces = new();
    private int _libraryNode = -1;
    private bool _warnedSourceLights;

    private sealed record MaterialUse(string Key, int GltfIndex, double USize, double VSize, bool Unlit, SurfaceLook Look, string Texture);

    private RenderBuild(CompileContext ctx, Glb glb)
    {
        _ctx = ctx;
        _glb = glb;
    }

    public static void Write(CompileContext ctx)
    {
        BakedSource? baked = ctx.Baked is { KeepRender: true } b ? b : null;
        Glb glb = baked is not null && File.Exists(Path.Combine(baked.Folder, "render", "world.glb"))
            ? Glb.Load(File.ReadAllBytes(Path.Combine(baked.Folder, "render", "world.glb")))
            : Glb.Create($"{CompileContext.Generator} {CompileContext.GeneratorVersion}");
        var r = new RenderBuild(ctx, glb) { _lighting = new WorldLighting(ctx) { CaptureWindows = true } };
        if (baked is not null)
        {
            r.StartFromBaked(baked);
        }

        ConvertedWorld? source = r.AddOccluders(baked);
        r.AddWorld(baked is not null);
        if (source is not null && r._lighting.Settings.RelightImported)
        {
            r.RelightSource(source);
        }

        r.AddMeshActors();
        r.AddConveyors();
        r.WriteEmitters(baked);
        r.Finish(baked);
        // Live lights need the pane descriptors even when the map has no baked light paths to recombine.
        if (r._lighting.WindowResponse.Panes.Count > 0)
            ctx.Files[OpenScct.Content.WindowLightTransfer.RelativePath] = r._lighting.WindowResponse.Save();
    }

    // ---- materials ------------------------------------------------------------------------------------------------

    /// <summary>The material of a surface with its polygon flags: library flags with the surface's own bits added
    /// (<see cref="SurfaceLook"/>, which the textured views share).</summary>
    private MaterialUse Material(string? name, int polyFlags)
    {
        SurfaceLook look = SurfaceLook.Resolve(_ctx.Library, name, polyFlags);
        string key = look.Key;
        if (_materialUses.TryGetValue(key, out MaterialUse? use))
        {
            return use;
        }

        JsonObject flags;
        string source;
        if (look.Library is { } lib)
        {
            flags = CopyTextures(lib.Flags.DeepClone().AsObject(), lib.RenderDir, lib.Origin);
            source = lib.Source;
            if (lib.Gameplay is not null && !_gameplay.ContainsKey(lib.Source))
            {
                _gameplay[lib.Source] = lib.Gameplay.DeepClone();
            }
        }
        else
        {
            string file = $"textures/{Safe(look.Name)}.png";
            if (!_ctx.Files.ContainsKey($"render/{file}"))
            {
                _ctx.Files[$"render/{file}"] = GridTexture.Make(look.Name);
                if (!look.Name.Equals(Geometry.Starter.DefaultMaterial, StringComparison.OrdinalIgnoreCase))
                {
                    _ctx.Info("render.material", $"no converted map ships material {look.Name}; it is drawn as a grid");
                }
            }

            flags = new JsonObject { ["texture"] = file };
            source = look.Name;
        }

        flags["masked"] = look.Masked;
        flags["translucent"] = look.Translucent;
        flags["two_sided"] = look.TwoSided;
        flags["unlit"] = look.Unlit;
        string texture = flags["texture"]!.GetValue<string>();
        int index = _glb.AddMaterial(key, texture, look.TwoSided, look.AlphaMode);
        _materials[key] = new JsonObject { ["flags"] = Sorted(flags), ["index"] = index, ["source"] = source };
        use = new MaterialUse(key, index, look.USize, look.VSize, look.Unlit, look, texture);
        _materialUses[key] = use;
        return use;
    }

    /// <summary>Copies every texture a library material names (base, combiner layers, masks) into this map.</summary>
    private JsonObject CopyTextures(JsonObject flags, string renderDir, string origin)
    {
        foreach ((string key, JsonNode? value) in flags.ToList())
        {
            switch (value)
            {
                case JsonValue v when v.TryGetValue(out string? s) && s.StartsWith("textures/", StringComparison.Ordinal):
                    flags[key] = CopyTexture(renderDir, origin, s);
                    break;
                case JsonObject o:
                    CopyTextures(o, renderDir, origin);
                    break;
            }
        }

        return flags;
    }

    private string CopyTexture(string renderDir, string origin, string relative)
    {
        string from = Path.Combine(renderDir, relative);
        if (_copiedTextures.TryGetValue(from, out string? done))
        {
            return done;
        }

        string target = relative;
        string? existing = _ctx.Files.TryGetValue($"render/{target}", out byte[]? bytes) ? Convert.ToHexString(bytes) : null;
        byte[] data = File.Exists(from) ? File.ReadAllBytes(from) : GridTexture.Make(relative);
        if (existing is not null && existing != Convert.ToHexString(data))
        {
            target = $"textures/{origin}_{Path.GetFileName(relative)}";
        }

        _ctx.Files[$"render/{target}"] = data;
        _copiedTextures[from] = target;
        return target;
    }

    private static JsonObject Sorted(JsonObject o)
    {
        var copy = new JsonObject();
        foreach ((string k, JsonNode? v) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            copy[k] = v?.DeepClone();
        }

        return copy;
    }

    private static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return sb.ToString();
    }

    // ---- shadow scene ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything that casts shadows: the world's surfaces, the source map's own world in baked mode, and every placed
    /// static mesh that casts shadows (bShadowCast). Returns the source map's world in baked mode.
    /// </summary>
    private ConvertedWorld? AddOccluders(BakedSource? baked)
    {
        _surfaces = MeshPrep.FixTJunctions(_ctx.RenderWorld.Visible);
        _lighting.AddSurfaces(_surfaces, p => _ctx.InSkyZone(p.Centroid() + (p.Plane.N * 2)));
        ConvertedWorld? source = null;
        if (baked is not null && File.Exists(Path.Combine(baked.Folder, "render", "world.glb")) &&
            File.Exists(Path.Combine(baked.Folder, "render", "lighting.json")))
        {
            source = ConvertedWorld.Load(baked.Folder, castsShadow: null);
            _lighting.AddOccluders(source.Occluders);
        }

        _lighting.AddMeshOccluders();
        return source;
    }

    // ---- world ----------------------------------------------------------------------------------------------------

    /// <summary>The world's surfaces as BSP nodes: lit ones lightmapped, unlit ones without a lightmap.</summary>
    private void AddWorld(bool baked)
    {
        if (_surfaces.Count == 0)
        {
            return;
        }

        _ctx.Progress($"render: {_surfaces.Count} surfaces");
        var lit = new List<LitPoly>();
        var unlit = new List<Poly>();
        foreach (Poly p in _surfaces)
        {
            if (Material(p.Surface.Material, p.Surface.Flags).Unlit)
            {
                unlit.Add(p);
            }
            else
            {
                lit.Add(new LitPoly(p, _ctx.ZoneAt(p.Centroid() + (p.Plane.N * 2))));
            }
        }

        // Baked mode adds its own atlases next to the source map's, under names of their own.
        string atlasName = baked ? $"{_ctx.MapId}_added_lightmap" : $"{_ctx.MapId}_lightmap";
        string materialSuffix = baked ? "__lmadded" : "__lm";
        var parts = new SortedDictionary<(bool Sky, string Material), WorldPart>();
        var pieces = new List<(Poly Poly, LightmapChart Chart)>();
        LightmapResult? result = null;
        if (lit.Count > 0)
        {
            if (baked)
            {
                WarnSourceLights($"{lit.Count} added surfaces are lit");
            }

            _ctx.Progress($"lightmaps: {lit.Count} surfaces, {_lighting.Lights.Count(l => l.Baked)} baked lights");
            // An authored world takes its lights from its leaves' light lists; added surfaces of a baked map do not (its
            // world is the source map's, which this tree does not hold).
            result = _lighting.BakeSurfaces(lit, baked ? null : _lighting.LeafLists());
            for (int a = 0; a < result.Atlases.Count; a++)
            {
                string uri = $"textures/{atlasName}{a}.png";
                _ctx.Files[$"render/{uri}"] = result.Png(a);
                _lightmapUris.Add(uri);
            }

            pieces.AddRange(result.Charts.SelectMany(chart => chart.Pieces.Select(piece => ((Poly)piece.Source!, chart))));
            if (result.WindowResponse is { } response)
                _lighting.WindowResponse.Batches.Add(response.Export(Enumerable.Range(0, result.Atlases.Count).Select(a => $"textures/{atlasName}{a}.png").ToArray(), result.Atlases));
        }

        // Charts cut surfaces larger than an atlas at tile lines; mend the T-junctions those cuts leave against the
        // surfaces next to them, so the drawn world has no cracks.
        List<Poly> mended = MeshPrep.FixTJunctions(pieces.Select(p => p.Poly).Concat(unlit).ToList());
        for (int i = 0; i < pieces.Count; i++)
        {
            Poly poly = mended[i];
            LightmapChart chart = pieces[i].Chart;
            MaterialUse use = Material(poly.Surface.Material, poly.Surface.Flags);
            MaterialUse mapped = LightmappedMaterial(use, $"{use.Key}{materialSuffix}{chart.Atlas}", _lightmapUris[chart.Atlas]);
            bool sky = _ctx.InSkyZone(poly.Centroid() + (poly.Plane.N * 2));
            if (!parts.TryGetValue((sky, mapped.Key), out WorldPart? part))
            {
                parts[(sky, mapped.Key)] = part = new WorldPart(mapped);
            }

            part.Add(poly, v => result!.Uv(chart, v));
        }

        foreach (Poly poly in mended.Skip(pieces.Count))
        {
            MaterialUse use = Material(poly.Surface.Material, poly.Surface.Flags);
            bool sky = _ctx.InSkyZone(poly.Centroid() + (poly.Plane.N * 2));
            if (!parts.TryGetValue((sky, use.Key), out WorldPart? part))
            {
                parts[(sky, use.Key)] = part = new WorldPart(use);
            }

            part.Add(poly, null);
        }

        int triangles = 0;
        foreach (bool sky in new[] { false, true })
        {
            List<Glb.MeshPart> meshParts = parts.Where(kv => kv.Key.Sky == sky).Select(kv => kv.Value.ToPart()).ToList();
            if (meshParts.Count == 0)
            {
                continue;
            }

            triangles += meshParts.Sum(m => m.Indices.Length / 3);
            string name = $"BSP_{_ctx.MapId}{(baked ? "_Added" : "")}{(sky ? "_Sky" : "")}";
            int node = _glb.AddNode(new JsonObject
            {
                ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["role"] = "bsp", ["sky"] = sky } },
                ["mesh"] = _glb.AddMesh(name, meshParts),
                ["name"] = name,
            });
            _glb.AddSceneRoot(node);
        }

        _ctx.Count("render.worldSurfaces", _surfaces.Count);
        _ctx.Count("render.worldTriangles", triangles);
        _ctx.Count("render.materials", _materialUses.Count);
    }

    /// <summary>A material's lightmapped variant (one per atlas, as converted maps name them): the same material table
    /// entry, plus the atlas the game binds to UV2.</summary>
    private MaterialUse LightmappedMaterial(MaterialUse use, string key, string lightmapUri)
    {
        if (_materialUses.TryGetValue(key, out MaterialUse? done))
        {
            return done;
        }

        int index = _glb.AddMaterial(key, use.Texture, use.Look.TwoSided, use.Look.AlphaMode, lightmapUri);
        JsonObject entry = _materials[use.Key]!.DeepClone().AsObject();
        entry["index"] = index;
        _materials[key] = entry;
        _lightmapped.Add(new JsonObject
        {
            ["base"] = use.Key,
            ["lightmap"] = lightmapUri,
            ["masked"] = use.Look.Masked,
            ["name"] = Safe(key),
            ["texture"] = use.Texture,
            ["translucent"] = use.Look.Translucent,
            ["two_sided"] = use.Look.TwoSided,
        });
        MaterialUse mapped = use with { Key = key, GltfIndex = index };
        _materialUses[key] = mapped;
        return mapped;
    }

    /// <summary>One BSP primitive being gathered: vertices in glTF space with texture and lightmap UVs.</summary>
    private sealed class WorldPart(MaterialUse use)
    {
        private readonly List<float> _pos = new();
        private readonly List<float> _nrm = new();
        private readonly List<float> _uv = new();
        private readonly List<float> _uv2 = new();
        private readonly List<uint> _idx = new();
        private readonly float[] _p3 = new float[3];

        public void Add(Poly p, Func<Vec3, (float U, float V)>? lightmapUv)
        {
            (List<(int, int, int)> tris, Vec3? centre) = MeshPrep.Triangulate(p);
            uint baseIndex = (uint)(_pos.Count / 3);
            Surface s = p.Surface;
            foreach (Vec3 v in centre is { } c ? p.Verts.Append(c) : p.Verts)
            {
                GltfSpace.Position(v, _p3, 0);
                _pos.AddRange(_p3);
                GltfSpace.Normal(p.Plane.N, _p3, 0);
                _nrm.AddRange(_p3);
                _uv.Add((float)((Vec3.Dot(v - s.TextureBase, s.TextureU) + s.PanU) / use.USize));
                _uv.Add((float)((Vec3.Dot(v - s.TextureBase, s.TextureV) + s.PanV) / use.VSize));
                if (lightmapUv is not null)
                {
                    (float u, float w) = lightmapUv(v);
                    _uv2.Add(u);
                    _uv2.Add(w);
                }
            }

            foreach ((int a, int b, int cc) in tris)
            {
                // glTF winds the other way round.
                _idx.Add(baseIndex + (uint)a);
                _idx.Add(baseIndex + (uint)cc);
                _idx.Add(baseIndex + (uint)b);
            }
        }

        public Glb.MeshPart ToPart() =>
            new(_pos.ToArray(), _nrm.ToArray(), _uv.ToArray(), _uv2.Count > 0 ? _uv2.ToArray() : null, null, _idx.ToArray(), use.GltfIndex);
    }

    /// <summary>Baked mode, when the project asks for it: the source map's own lightmaps relit by the project's lights
    /// in their original layout. Texels outside its lightmaps keep the source's bytes.</summary>
    private void RelightSource(ConvertedWorld source)
    {
        WarnSourceLights("its relit lightmaps are lit");
        WarnRecovery();
        _ctx.Progress($"lightmaps: relighting {source.Charts.Count} source lightmaps");
        LightmapResult result = _lighting.BakeInPlace(source.Charts, source.Atlases.Select((_, a) => source.BakeSize(a)).ToList(), SourceLists(source));
        if (result.WindowResponse is { } response)
            _lighting.WindowResponse.Batches.Add(response.Export(source.Atlases.Select(a => a.Uri).ToArray(), result.Atlases, source.Compressed));
        for (int a = 0; a < source.Atlases.Count; a++)
        {
            string file = $"render/{source.Atlases[a].Uri}";
            if (!_ctx.Files.TryGetValue(file, out byte[]? original) || Png.Decode(original) is not { } image ||
                image.Width != source.Atlases[a].Width || image.Height != source.Atlases[a].Height)
            {
                continue;
            }

            // A squeezed atlas is baked at twice its size and squeezed as the original did; its charts' places halve.
            bool squeezed = source.Compressed[a];
            byte[] relit = squeezed ? ConvertedWorld.Compress(result.Rgba(a), result.Atlases[a].Width, result.Atlases[a].Height) : result.Rgba(a);
            int shift = squeezed ? 1 : 0;
            foreach (LightmapChart chart in source.Charts.Where(c => c.Atlas == a))
            {
                int x0 = chart.X >> shift, x1 = Math.Min(image.Width, (chart.X + chart.Width + shift) >> shift);
                for (int y = chart.Y >> shift; y < Math.Min(image.Height, (chart.Y + chart.Height + shift) >> shift); y++)
                {
                    int from = 4 * ((y * image.Width) + x0);
                    Array.Copy(relit, from, image.Rgba, from, 4 * Math.Max(0, x1 - x0));
                }
            }

            _ctx.Files[file] = Png.EncodeRgba(image.Width, image.Height, image.Rgba);
        }
    }

    /// <summary>
    /// The light lists of the source map's surfaces (<see cref="SourceLightLists"/>), read from the .sdc the project was
    /// imported from while it is still the file the import read; null (every light in reach lights every surface) for
    /// other projects, with a note when the file is gone or changed.
    /// </summary>
    private LightLists? SourceLists(ConvertedWorld source)
    {
        ProjectSource from = _ctx.Project.Source;
        if (!string.Equals(from.Kind, "sdc", StringComparison.OrdinalIgnoreCase) || from.File is not { } sdc)
        {
            return null;
        }

        if (!File.Exists(sdc) || (from.Sha256 is { } sha &&
            !Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sdc))).Equals(sha, StringComparison.OrdinalIgnoreCase)))
        {
            _ctx.Info("lighting.leaf-lists", $"{Path.GetFileName(sdc)} is missing or changed since the import, so the relit lightmaps take every " +
                "light in reach instead of the lights the original's BSP leaves list for each surface");
            return null;
        }

        try
        {
            SourceLightLists lists = SourceLightLists.Read(sdc, source);
            LightLists bake = lists.For(_lighting.Lights, _lighting.WholeWorldLists);
            _ctx.Count("lightmap.leafLists.charts", lists.MatchedCharts);
            _ctx.Count("lightmap.leafLists.storedLights", bake.Stored);
            _ctx.Count("lightmap.leafLists.runLights", bake.Runs);
            _ctx.Count("lightmap.leafLists.floodLights", bake.Flood);
            _ctx.Count("lightmap.leafLists.freeLights", bake.Free);
            if (bake.WholeWorld is { } whole)
            {
                // Only the lights these lists hold: the others take the original's own lists.
                var flooded = bake.Flooded.ToHashSet(ReferenceEqualityComparer.Instance);
                _lighting.WarnLeafLists(whole, whole.InSolid.Where(flooded.Contains), whole.Cut.Where(flooded.Contains));
            }

            if (bake.Free > 0)
            {
                // A cooked copy's recovered lights were never in its file (the cook stripped them), and it has no brushes.
                int recovered = bake.FreeLights.Count(l => _ctx.Project.FindActor(l.Id) is { } a && BakedContent.Recovered(a) is not null);
                if (recovered > 0)
                {
                    _ctx.Info("lighting.leaf-lists", $"{recovered} lights the import recovered for {Path.GetFileName(sdc)} (a cooked copy, which " +
                        "keeps no light lists for them and no brushes to rebuild its world from) light every surface in reach, through walls too");
                }

                if (bake.Free > recovered)
                {
                    _ctx.Info("lighting.leaf-lists", $"{bake.Free - recovered} baked lights are not where {Path.GetFileName(sdc)} saved them and the " +
                        "project has no baked brushes to rebuild the source world from, so they light every surface in reach, through walls too");
                }
            }

            if (lists.CompareFloodWithStored(bake) is { } flood)
            {
                // How the whole-world flood matches the file's own lists, for the lights still as the file saved them.
                _ctx.Count("lightmap.leafLists.floodCheckLightmaps", flood.Lightmaps);
                _ctx.Count("lightmap.leafLists.floodCheckAgree", flood.Agree);
                _ctx.Count("lightmap.leafLists.floodCheckOnlyStored", flood.OnlyStored);
                _ctx.Count("lightmap.leafLists.floodCheckOnlyOurs", flood.OnlyOurs);
            }
            if (lists.CompareWithStored(bake) is { } check)
            {
                // How the lists worked out from the runs match the file's own, for the lights the runs hold.
                _ctx.Count("lightmap.leafLists.checkLightmaps", check.Lightmaps);
                _ctx.Count("lightmap.leafLists.checkAgree", check.Agree);
                _ctx.Count("lightmap.leafLists.checkOnlyStored", check.OnlyStored);
                _ctx.Count("lightmap.leafLists.checkOnlyOurs", check.OnlyOurs);
            }

            return bake;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _ctx.Info("lighting.leaf-lists", $"the BSP light lists of {Path.GetFileName(sdc)} could not be read ({e.Message}), so the relit " +
                "lightmaps take every light in reach");
            return null;
        }
    }

    /// <summary>
    /// Baked mode: a converted map keeps only its in-game lights (lighting.json), not the lights that only lit the
    /// original's lightmaps. When the project bakes with no light the source map did not already have, what it lights
    /// comes out darker than the original lightmaps around it; say so once.
    /// </summary>
    private void WarnSourceLights(string what)
    {
        // A cooked .sdc import's light recovery says for itself whether its relight matches (WarnRecovery).
        if (_warnedSourceLights || _ctx.Baked is not { } baked || !File.Exists(Path.Combine(baked.Folder, "render", "lighting.json"))
            || _ctx.Project.Extra[Interop.Sdc.Lights.LostLightRecovery.ReportKey] is SvJsonObject)
        {
            return;
        }

        _warnedSourceLights = true;
        JsonNode? lighting = JsonNode.Parse(File.ReadAllBytes(Path.Combine(baked.Folder, "render", "lighting.json")));
        var kept = new HashSet<string>((lighting?["lights"] as JsonArray ?? []).Select(l => l?["name"]?.GetValue<string>() ?? ""),
            StringComparer.OrdinalIgnoreCase);
        // Lights the original's file still held (recovered, not added by the user) do not make up for the dropped ones.
        var recovered = new HashSet<string>(_ctx.Project.Actors.Where(a => BakedContent.Recovered(a) is not null).Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);
        if (_lighting.Lights.Any(l => l.Baked && !kept.Contains(l.Id) && !recovered.Contains(l.Id)))
        {
            return;
        }

        _ctx.Warn("lighting.source-lights", $"{what} only by the lights the converted map {baked.Map} kept, its in-game lights; the lights " +
            "that only lit the original's lightmaps were dropped in conversion, so this light is darker than the original's. Add static " +
            "lights where it matters, or import the map's editor copy as a full project and bake it whole.");
    }

    /// <summary>A relight of a cooked map whose light recovery says it will not match its cooked lightmaps (lost lights
    /// missing, or back only as stand-ins, or not confirmed by the bake): say why, once. Stand-ins left in a project
    /// whose report is gone are still named.</summary>
    private void WarnRecovery()
    {
        const string keep = " Set Imported lightmaps to Keep them to use the cooked lightmaps.";
        if (_ctx.Project.Extra[Interop.Sdc.Lights.LostLightRecovery.ReportKey] is SvJsonObject report)
        {
            if (report["unsafeReason"] is SvJsonString why)
            {
                _ctx.Warn("lighting.recovery-unsafe", $"the relit lightmaps will not match the cooked map's: {why.Value}.{keep}");
            }

            return;
        }

        string fitted = Interop.Sdc.Lights.LostLightRecovery.SourceName(Interop.Sdc.Lights.RecoveredSource.Fitted);
        string stale = Interop.Sdc.Lights.LostLightRecovery.SourceName(Interop.Sdc.Lights.RecoveredSource.StaleTwin);
        int standIns = _ctx.Project.Actors.Count(a => BakedContent.RecoveredSource(a) is { } s && (s == fitted || s == stale));
        if (standIns > 0)
        {
            _ctx.Warn("lighting.recovered-standins", $"the relit lightmaps use {standIns} recovered stand-in lights " +
                $"({Interop.Sdc.Lights.LostLightRecovery.Folder}): they restore the cooked map's look, not its author's lights, so the " +
                $"relight will not match the original exactly.{keep}");
        }
    }

    // ---- placed static meshes -------------------------------------------------------------------------------------

    /// <summary>Static meshes from the library (<see cref="WorldLighting.MeshActors"/>). Placed static meshes that are not gameplay
    /// objects are drawn through a <c>#vc</c> variant carrying their baked vertex light, as converted maps draw
    /// theirs; gameplay objects and movers are lit at run time. A mesh a conveyor scrolls is a gameplay instance, so the
    /// game keeps it as a node of its own (and lights it at run time).</summary>
    private void AddMeshActors()
    {
        int missing = 0, vertexLit = 0;
        foreach ((Actor a, bool gameplay, string path) in _lighting.MeshActors())
        {
            if (_instanced.Contains(a.Id))
            {
                continue;
            }

            string? node = ImportMesh(path);
            if (node is null)
            {
                missing++;
                _ctx.Warn("render.mesh-missing", $"actor {a.Id}: {_ctx.NothingShips($"static mesh {path}")}; it is not drawn", ObjectRef.Actor(a.Id));
                continue;
            }

            bool mover = gameplay && _ctx.Rules.IsA(a, "Engine.Mover");
            bool hidden = _ctx.Rules.Flag(a, "bHidden");
            bool own = gameplay || _lighting.IsScrolled(a);
            string? variant = _lighting.DrawsVertexLit(a, gameplay) ? VertexLitVariant(a, path, node) : null;
            own |= _lighting.WindowResponse.Batches.Any(b => b.Resources.Any(r => r.Kind == "mesh" && r.Name == a.Id));
            vertexLit += variant is null ? 0 : 1;
            JsonObject instance = Instance(a.Id, a.ShortClass, own, hidden, variant ?? node, a.Location, a.Rotation,
                _ctx.Rules.Scale(a), _ctx.Rules.PrePivot(a), variant is not null, mover ? "mover" : "staticmesh");
            ApplyDrawingFlags(instance, a);
            _instances.Add(instance);
            _instanced.Add(a.Id);
        }

        _ctx.Count("render.meshInstances", _instanced.Count);
        _ctx.Count("render.meshesVertexLit", vertexLit);
        _ctx.Count("render.meshesMissing", missing);
    }

    /// <summary>The library node of the mesh with the actor's baked vertex light (shared by every actor that bakes to
    /// the same colours), or null when no baked light reaches it.</summary>
    private string? VertexLitVariant(Actor a, string asset, string node)
    {
        if (_lighting.MeshPrimitives(asset) is not { } prims || _lighting.VertexLight(a, prims) is not { } colours)
        {
            return null;
        }

        var hash = new Hash64();
        foreach ((int index, byte[] rgba) in colours.OrderBy(kv => kv.Key))
        {
            hash.Add(index);
            foreach (byte b in rgba)
            {
                hash.Add((int)b);
            }
        }

        string tag = $"vc{(uint)(hash.Value ^ (hash.Value >> 32)):x8}";
        string name = $"{node}#{tag}";
        if (!_variants.Add(name))
        {
            return name;
        }

        int mesh = _glb.AddColorVariant(_importedMeshIndex[asset], name, colours);
        _library.Add(_glb.AddNode(new JsonObject
        {
            ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = _ctx.Library.FindMesh(asset)!.Asset, ["role"] = "libraryMesh", ["variant"] = tag } },
            ["mesh"] = mesh,
            ["name"] = name,
        }));
        _vertexLit.Add(new JsonObject { ["base"] = node, ["name"] = name });
        return name;
    }

    private string? ImportMesh(string asset)
    {
        if (_importedMeshes.TryGetValue(asset, out string? done))
        {
            return done;
        }

        if (_ctx.Library.FindMesh(asset) is not { } lib || !File.Exists(lib.GlbPath))
        {
            return null;
        }

        Glb src = _ctx.Library.Glb(lib.GlbPath);
        int mesh = _glb.ImportMesh(src, lib.Mesh, uri => CopyTexture(lib.RenderDir, lib.Origin, uri));
        _importedMeshIndex[asset] = mesh;
        _library.Add(_glb.AddNode(new JsonObject
        {
            ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = lib.Asset, ["role"] = "libraryMesh", ["variant"] = "" } },
            ["mesh"] = mesh,
            ["name"] = lib.NodeName,
        }));
        ImportMaterialEntries(src, lib.Mesh, lib.RenderDir, lib.Origin);
        _importedMeshes[asset] = lib.NodeName;
        return lib.NodeName;
    }

    /// <summary>The materials.json entries of an imported mesh's materials, copied from its map's table.</summary>
    private void ImportMaterialEntries(Glb src, int mesh, string renderDir, string origin)
    {
        foreach (JsonNode? p in src.Array("meshes")[mesh]!["primitives"]!.AsArray())
        {
            if (p?["material"] is not JsonValue mv || src.Array("materials")[mv.GetValue<int>()]?["name"]?.GetValue<string>() is not { } name ||
                _materials.ContainsKey(name))
            {
                continue;
            }

            LibraryMaterial? lib = _ctx.Library.FindMaterial(name);
            JsonObject flags = lib is not null && (lib.RenderDir == renderDir || origin == "EditorWindows")
                ? CopyTextures(lib.Flags.DeepClone().AsObject(), lib.RenderDir, lib.Origin)
                : new JsonObject();
            int index = _glb.Array("materials").Select((m, i) => (m, i)).First(t => t.m?["name"]?.GetValue<string>() == name).i;
            _materials[name] = new JsonObject { ["flags"] = Sorted(flags), ["index"] = index, ["source"] = lib?.Source ?? name };
            if (lib?.Gameplay is not null && !_gameplay.ContainsKey(lib.Source))
            {
                _gameplay[lib.Source] = lib.Gameplay.DeepClone();
            }
        }
    }

    private static JsonObject Instance(string actor, string cls, bool gameplay, bool hidden, string mesh, Vec3 location, Rot rotation,
        Vec3 scale, Vec3 prePivot, bool vertexLit, string kind = "staticmesh")
    {
        var o = new JsonObject
        {
            ["actor"] = actor,
            ["class"] = cls,
            ["gameplay"] = gameplay,
            ["hidden"] = hidden,
            ["kind"] = kind,
            ["location"] = Floats(location),
            ["mesh"] = mesh,
        };
        if (prePivot != Vec3.Zero)
        {
            o["prePivot"] = Floats(prePivot);
        }

        o["rotation"] = new JsonObject { ["Pitch"] = rotation.Pitch, ["Roll"] = rotation.Roll, ["Yaw"] = rotation.Yaw };
        o["scale"] = Floats(scale);
        o["vertexLit"] = vertexLit;
        return o;
    }

    private static JsonArray Floats(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private void ApplyDrawingFlags(JsonObject instance, Actor actor)
    {
        // The game puts sky meshes on a separate camera layer. Unlit meshes must also bypass runtime actor
        // lighting; otherwise a cloud dome has neither baked colours nor the sky camera's static geometry path.
        instance["skyZone"] = _ctx.InSkyZone(actor.Location);
        instance["unlit"] = _ctx.Rules.Flag(actor, "bUnlit");
    }

    // ---- baked world ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes over the source map's library, instances and material table. Instances of actors the project has follow
    /// the actor; instances of deleted actors go. Instances an import does not turn into actors stay as they are.
    /// </summary>
    private void StartFromBaked(BakedSource baked)
    {
        string render = Path.Combine(baked.Folder, "render");
        if (File.Exists(Path.Combine(render, "materials.json")) &&
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(render, "materials.json"))) is JsonObject table)
        {
            foreach (string key in new[] { "materials", "gameplay" })
            {
                if (table[key] is JsonObject o)
                {
                    foreach ((string k, JsonNode? v) in o)
                    {
                        (key == "materials" ? _materials : _gameplay)[k] = v?.DeepClone();
                    }
                }
            }

            BakedTable = table;
        }

        foreach (string file in Directory.EnumerateFiles(render, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(baked.Folder, file).Replace('\\', '/');
            if (rel is not ("render/world.glb" or "render/materials.json"))
            {
                _ctx.Files[rel] = File.ReadAllBytes(file);
            }
        }

        JsonArray nodes = _glb.Array("nodes");
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i]?["extras"]?["svmap"]?["role"]?.GetValue<string>() == "library")
            {
                _libraryNode = i;
            }
        }

        JsonArray source = _glb.Json["extras"]?["svmap"]?["instances"] as JsonArray ?? new JsonArray();
        var actors = _ctx.Gameplay.Concat(_ctx.Render).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        static bool IsGameplay(JsonNode? i) => i?["gameplay"]?.GetValue<bool>() == true || i?["kind"]?.GetValue<string>() == "mover";
        foreach (JsonNode? inst in source)
        {
            if (inst is not JsonObject o || o["actor"]?.GetValue<string>() is not { } id)
            {
                continue;
            }

            if (!IsGameplay(o) && !baked.MeshActors.Contains(id))
            {
                // Not drawn by a project actor (projectors, soft body patches...): the source's instance stays as it is.
                _instanced.Add(id);
                _instances.Add(o.DeepClone());
            }
            else if (actors.TryGetValue(id, out Actor? a) && Followed(o, a) is { } followed)
            {
                _instanced.Add(id);
                _instances.Add(followed);
            }
        }
    }

    /// <summary>A source instance updated to the project actor's transform and visibility (and a static mesh actor's
    /// skins); null when the actor now shows another mesh (it is then drawn as a new instance).</summary>
    private JsonObject? Followed(JsonObject source, Actor a)
    {
        string? mesh = source["mesh"]?.GetValue<string>();
        bool gameplay = _ctx.GameplayIds.Contains(a.Id);
        SvJsonNode? meshProp = gameplay ? a.Props["StaticMesh"] : _ctx.Rules.Value(a, "StaticMesh");
        if (PropValues.AssetPath(meshProp) is { } path && !ConvertedWorld.SameMesh(mesh, path))
        {
            return null;
        }

        JsonObject o = source.DeepClone().AsObject();
        ApplyDrawingFlags(o, a);
        o["location"] = Floats(a.Location);
        o["rotation"] = new JsonObject { ["Pitch"] = a.Rotation.Pitch, ["Roll"] = a.Rotation.Roll, ["Yaw"] = a.Rotation.Yaw };
        o["scale"] = Floats(_ctx.Rules.Scale(a));
        // The converter also hides some gameplay objects by rules of its own: theirs changes only with a stored bHidden.
        if (!gameplay || a.Props["bHidden"] is not null)
        {
            o["hidden"] = _ctx.Rules.Flag(a, "bHidden");
        }

        o.Remove("prePivot");
        Vec3 pivot = _ctx.Rules.PrePivot(a);
        if (pivot != Vec3.Zero)
        {
            o["prePivot"] = Floats(pivot);
        }

        // Gameplay objects keep the source's skins: their Skins prop names a material with its group, the instance without.
        if (!gameplay)
        {
            o.Remove("skins");
            if (a.Props["Skins"] is SvJsonArray skins)
            {
                o["skins"] = new JsonArray(skins.Select(s => (JsonNode?)(PropValues.AssetPath(s) ?? "")).ToArray());
            }
        }

        return o;
    }

    private JsonObject? BakedTable { get; set; }

    // ---- output ---------------------------------------------------------------------------------------------------

    private void Finish(BakedSource? baked)
    {
        if (_libraryNode >= 0)
        {
            JsonObject lib = _glb.Array("nodes")[_libraryNode]!.AsObject();
            JsonArray children = (lib["children"] ??= new JsonArray()).AsArray();
            foreach (int n in _library)
            {
                children.Add(n);
            }
        }
        else if (_library.Count > 0)
        {
            int lib = _glb.AddNode(new JsonObject
            {
                ["children"] = new JsonArray(_library.Select(i => (JsonNode)i).ToArray()),
                ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["role"] = "library" } },
                ["name"] = "Library",
            });
            _glb.AddSceneRoot(lib);
        }

        var extras = new JsonObject { ["svmap"] = new JsonObject { ["instances"] = _instances, ["map"] = _ctx.MapId } };
        _ctx.Files["render/world.glb"] = _glb.ToBytes(extras);

        var table = new JsonObject
        {
            ["additive"] = BakedTable?["additive"]?.DeepClone() ?? new JsonArray(),
            ["authoredColorMeshes"] = BakedTable?["authoredColorMeshes"]?.DeepClone() ?? new JsonArray(),
            ["gameplay"] = Sorted(_gameplay),
            ["graph"] = BakedTable?["graph"]?.DeepClone() ?? new JsonObject(),
            ["lightmapped"] = Merge(BakedTable?["lightmapped"] as JsonArray, _lightmapped),
            ["materials"] = Sorted(_materials),
            ["modulate"] = BakedTable?["modulate"]?.DeepClone() ?? new JsonArray(),
            ["vertexLitMeshes"] = Merge(BakedTable?["vertexLitMeshes"] as JsonArray, _vertexLit),
        };
        _ctx.Files["render/materials.json"] = Json(table);
        if (baked is null)
        {
            _ctx.Files["render/lighting.json"] = Json(LightingJson());
        }
        else
        {
            FollowBakedLighting(baked);
        }

        _ctx.Count("render.lights", _lighting.Lights.Count(l => !l.BakeOnly));
        if (_ctx.BakeOnlyLights.Count > 0)
        {
            _ctx.Count("render.bakeOnlyLights", _ctx.BakeOnlyLights.Count);
        }

        _ctx.Count("render.instances", _instances.Count);
    }

    private static JsonArray Merge(JsonArray? a, JsonArray b)
    {
        var result = new JsonArray();
        foreach (JsonNode? n in (a ?? []).Concat(b))
        {
            result.Add(n?.DeepClone());
        }

        return result;
    }

    public static byte[] Json(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(s_compact) + "\n");

    /// <summary>
    /// Baked mode: the source's lighting.json without the lights the project deleted, and with records for new lights.
    /// The converter's kept light records, lightmaps, zones and fog stay as they are.
    /// </summary>
    private void FollowBakedLighting(BakedSource baked)
    {
        var ids = new HashSet<string>(_ctx.Render.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(_ctx.GameplayIds);
        if (_ctx.Files.TryGetValue("render/lighting.json", out byte[]? lightingBytes) && JsonNode.Parse(lightingBytes) is JsonObject lighting &&
            lighting["lights"] is JsonArray sourceLights)
        {
            var lights = new JsonArray();
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? l in sourceLights)
            {
                if (l?["name"]?.GetValue<string>() is { } name && ids.Contains(name))
                {
                    lights.Add(l.DeepClone());
                    kept.Add(name);
                }
            }

            foreach (JsonNode? l in LightingJson()["lights"]!.AsArray())
            {
                if (l?["name"]?.GetValue<string>() is { } name && !kept.Contains(name) && !baked.Actors.Contains(name))
                {
                    lights.Add(l.DeepClone());
                }
            }

            lighting["lights"] = lights;
            JsonArray lightmaps = (lighting["lightmaps"] ??= new JsonArray()).AsArray();
            foreach (string uri in _lightmapUris)
            {
                lightmaps.Add(uri);
            }

            if (_lightmapUris.Count > 0 || _lighting.Settings.RelightImported)
            {
                lighting["bake"] = BakeJson();
            }

            _ctx.Files["render/lighting.json"] = Json(lighting);
        }
    }

    /// <summary>
    /// <c>lighting.json</c> as the converter writes it: every light (render lights and gameplay STriggerLights) with
    /// its effective values, every zone with its ambient and fog, and the sky zone's camera.
    /// </summary>
    private JsonObject LightingJson()
    {
        ActorRules r = _ctx.Rules;
        var lights = new JsonArray();
        foreach (BakeLight l in _lighting.Lights.Where(l => !l.BakeOnly))
        {
            Actor a = l.Actor!;
            LightRgb hsv = ActorLightRules.Colour((byte)l.Hue, (byte)l.Saturation, 255f);
            Vec3 colour = new Vec3(hsv.R, hsv.G, hsv.B) / ActorLightRules.HsvFullBrightness;
            bool echelon = l.Effect == ActorLightRules.LE_ESpotShadow;
            bool triggered = r.IsA(a, "SBase.STriggerLight");
            lights.Add(new JsonObject
            {
                ["actor_shadows"] = r.Flag(a, "bActorShadows"),
                ["apply_ingame"] = r.Flag(a, "bApplyToInGameLighting"),
                ["apply_static"] = l.ApplyStatic,
                ["brightness"] = (float)l.Brightness,
                ["class"] = a.ShortClass,
                ["color"] = new JsonArray((float)colour.X, (float)colour.Y, (float)colour.Z),
                ["cone"] = (int)l.Cone,
                ["dynamic"] = l.Dynamic,
                ["echelon_range"] = echelon ? (float)r.Number(a, "EchelonRange", 1000) : 0f,
                ["effect"] = l.Effect,
                ["gameplay"] = l.Gameplay,
                ["hidden"] = false,
                ["hue"] = l.Hue,
                ["initially_on"] = !triggered || r.Value(a, "bInitialyOn") is null || r.Flag(a, "bInitialyOn"),
                ["kind"] = "light",
                ["light_period"] = (int)r.Number(a, "LightPeriod", 32),
                ["light_phase"] = (int)r.Number(a, "LightPhase"),
                ["light_type"] = l.IsSpot ? "spot" : l.IsSun ? "sun" : "point",
                ["location"] = Floats(a.Location),
                ["ltype"] = l.Type,
                ["name"] = a.Id,
                ["near_plane_ratio"] = (float)r.Number(a, "NearPlaneRatio", 0.1),
                ["own_zone_only"] = l.OwnZoneOnly,
                ["props"] = PropsJson(a),
                // The game's radius: a calibrated bake default (BakeDefaultLightRadius) is for the bake alone.
                ["radius"] = (float)r.Number(a, "LightRadius", 64),
                ["rotation"] = new JsonArray(a.Rotation.Pitch, a.Rotation.Yaw, a.Rotation.Roll),
                ["saturation"] = l.Saturation,
                ["scale"] = Floats(r.Scale(a)),
                ["special_lit"] = r.Flag(a, "bSpecialLit"),
                ["spot_height"] = echelon ? (float)r.Number(a, "SpotHeight", 250) : 0f,
                ["spot_width"] = echelon ? (float)r.Number(a, "SpotWidth", 250) : 0f,
                ["static"] = r.Flag(a, "bStatic"),
                ["tag"] = GameplayBuild.EffectiveTag(_ctx, a),
                ["triggered"] = triggered,
                ["zone"] = _ctx.ZoneActorAt(a.Location),
            });
        }

        var zones = new JsonArray();
        JsonObject? sky = null;
        for (int z = 1; z < _ctx.ZoneActors.Count; z++)
        {
            if (_ctx.ZoneActors[z] is not { } id || _ctx.Gameplay.Find(a => a.Id == id) is not { } zone)
            {
                continue;
            }

            (int ar, int ag, int ab) = _lighting.ZoneAmbient(z);
            bool isSky = r.IsSkyZone(zone);
            zones.Add(new JsonObject
            {
                ["ambient"] = new JsonArray(ar, ag, ab, 255),
                ["class"] = zone.ShortClass,
                ["clear_to_fog"] = r.Flag(zone, "bClearToFogColor"),
                ["fog"] = r.Flag(zone, "bDistanceFog"),
                ["fog_blend_time"] = (float)r.Number(zone, "DistanceFogBlendTime", 1),
                ["fog_color"] = Rgba(r.Value(zone, "DistanceFogColor")),
                ["fog_end"] = (float)r.Number(zone, "DistanceFogEnd"),
                ["fog_start"] = (float)r.Number(zone, "DistanceFogStart"),
                ["hidden"] = true,
                ["kind"] = "zone",
                ["location"] = Floats(zone.Location),
                ["location_name"] = zone.Props.GetString("LocationName") ?? "",
                ["name"] = id,
                ["props"] = PropsJson(zone),
                ["rotation"] = new JsonArray(zone.Rotation.Pitch, zone.Rotation.Yaw, zone.Rotation.Roll),
                ["scale"] = new JsonArray(1.0f, 1.0f, 1.0f),
                ["sky"] = isSky,
                ["tag"] = GameplayBuild.EffectiveTag(_ctx, zone),
                ["zoneIndex"] = z,
            });

            // The sky camera: the first sky zone's actor (the view the sky zone is drawn from).
            sky ??= isSky
                ? new JsonObject
                {
                    ["actor"] = id,
                    ["location"] = Floats(zone.Location),
                    ["rotation"] = new JsonArray(zone.Rotation.Pitch, zone.Rotation.Yaw, zone.Rotation.Roll),
                    ["zone"] = z,
                }
                : null;
        }

        return new JsonObject
        {
            ["bake"] = BakeJson(),
            ["lightmaps"] = new JsonArray(_lightmapUris.Select(u => (JsonNode?)u).ToArray()),
            ["lights"] = lights,
            ["look"] = new JsonObject { ["glow_enabled"] = false, ["glow_threshold"] = 255, ["glow_threshold_stored"] = false },
            ["sky"] = sky,
            ["zones"] = zones,
        };
    }

    /// <summary>How the lightmaps were baked (provenance only: the game does not read it).</summary>
    private JsonObject BakeJson()
    {
        var o = JsonNode.Parse(CanonicalJson.ToCanonicalString(ProjectStore.LightingToJson(_lighting.Settings)))!.AsObject();
        o["fast"] = _ctx.Options.FastLighting;
        o["generator"] = $"{CompileContext.Generator} {CompileContext.GeneratorVersion}";
        foreach (string key in new[] { "charts", "texels", "rays" })
        {
            if (_ctx.Stats.TryGetValue($"lightmap.{key}", out long n))
            {
                o[key] = n;
            }
        }

        return o;
    }

    /// <summary>An actor's stored props plus its Location, sorted (the typed bag the converter keeps per light and zone).</summary>
    private static JsonObject PropsJson(Actor a)
    {
        var props = new JsonObject();
        foreach ((string k, SvJsonNode v) in a.Props.Where(kv => !PropValues.IsRaw(kv.Value)))
        {
            props[k] = JsonNode.Parse(CanonicalJson.ToCanonicalString(v));
        }

        props["Location"] = new JsonObject { ["X"] = (float)a.Location.X, ["Y"] = (float)a.Location.Y, ["Z"] = (float)a.Location.Z };
        return Sorted(props);
    }

    /// <summary>A UE2 FColor struct value as [R, G, B, A] (zero when unset).</summary>
    private static JsonArray Rgba(SvJsonNode? color)
    {
        int C(string key) => color is SvJsonObject o && o[key] is SvJsonNumber n ? (int)PropValues.Num(n) : 0;
        return new JsonArray(C("R"), C("G"), C("B"), C("A"));
    }
}
