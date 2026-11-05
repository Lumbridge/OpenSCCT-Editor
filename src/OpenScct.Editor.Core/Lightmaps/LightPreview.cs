using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>A lit polygon of a preview pass: its corners (a large surface may be cut into atlas-sized pieces), the
/// CSG polygon it came from (material, texture mapping), its lightmap UVs in <see cref="Atlas"/> and the size of its
/// lightmap texels in uu.</summary>
public sealed record LitPreviewPolygon(PreviewPolygon Source, IReadOnlyList<Vec3> Vertices, int Atlas, IReadOnlyList<(float U, float V)> LightmapUvs,
    double TexelSize);

/// <summary>The light a placed static mesh shows in game, per vertex of each glb primitive (by primitive index): RGBA8
/// in the game's half-intensity units, so the view draws texture times twice the colour.</summary>
public sealed record LitPreviewMesh(IReadOnlyDictionary<int, byte[]> Colours);

/// <summary>A triangle of an imported map's own lightmaps, for the density view: its corners, normal, UVs in its atlas
/// (of <see cref="AtlasWidth"/> x <see cref="AtlasHeight"/> texels as the original baked it) and its texel size in uu.</summary>
public sealed record SourceLightmapTriangle(Vec3[] Vertices, Vec3 Normal, (float U, float V)[] Uvs, int Atlas, int AtlasWidth, int AtlasHeight,
    double TexelSize);

/// <summary>The placed static meshes of a pass: how many got light, how many of those were baked here (the rest keep an
/// imported map's stored light) and the time it took.</summary>
public readonly record struct MeshLightStats(int Meshes, int Baked, long Milliseconds);

/// <summary>One pass of the Lit Preview's bake: lit polygons with their atlases (RGBA8, the game's half-intensity
/// bytes), the unlit polygons drawn without light, the light of placed static meshes by actor id (the last pass only;
/// meshes the game lights at run time are left out), an imported map's own lightmap triangles (the last pass only,
/// for the density view) and the pass's numbers. <see cref="Final"/> marks the last pass. <see cref="DefaultTexel"/>
/// is the texel size of a face at the default LightMapScale in the map's lighting mode, which the density view
/// measures against.</summary>
public sealed record LightPreviewPass(string Name, bool Final, IReadOnlyList<LitPreviewPolygon> Lit, IReadOnlyList<PreviewPolygon> Unlit,
    IReadOnlyList<(int Width, int Height, byte[] Rgba)> Atlases, IReadOnlyDictionary<string, LitPreviewMesh> Meshes,
    IReadOnlyList<SourceLightmapTriangle> SourceLightmaps, double DefaultTexel, BakeStats Stats, MeshLightStats MeshStats);

/// <summary>What changed since the bake the view shows, which decides the passes a new bake runs.</summary>
public enum LightPreviewStart
{
    /// <summary>Nothing baked yet: light at once without shadows, then shadows, then full density.</summary>
    Cold,

    /// <summary>Brushes changed: a coarse pass with shadows first, so the new surfaces show at once, then full density.</summary>
    Geometry,

    /// <summary>Only lights, meshes or settings changed: straight to full density, so the shadows on show are not swapped
    /// for coarser ones (the shadow cache keeps that quick).</summary>
    Lights,
}

/// <summary>
/// The Lit Preview's bake: the build's lightmapper on the build's own world polygons, zones and shadow scene, refined
/// over several passes so the view shows light at once and shadows soon after. One instance keeps a shadow cache per
/// pass between runs, so moving one light or brush retraces only the lights and charts it touches.
/// </summary>
public sealed class LightPreview
{
    private readonly LightmapBaker _baker = new();
    private readonly WorldCache _world = new();
    private readonly object _worldLock = new();
    private ConvertedWorld? _source;
    private (ConvertedWorld World, List<SourceLightmapTriangle> Triangles)? _sourceLightmaps;

    /// <summary>The passes, each baked when asked for (so a cancelled run stops between and inside passes). Brushes in
    /// <paramref name="hidden"/> still cast shadows, as in a build, but are not drawn.</summary>
    public IEnumerable<LightPreviewPass> Passes(MapProject project, string? projectFolder, IReadOnlySet<string> hidden, LightPreviewStart start,
        CancellationToken cancel)
    {
        var ctx = new CompileContext(project, new CompileOptions { ProjectFolder = projectFolder }, EditorServices.Classes);
        (WorldLighting lighting, ConvertedWorld? sourceMap) = World(ctx);
        cancel.ThrowIfCancellationRequested();

        var unlit = new List<PreviewPolygon>();
        var lit = new List<LitPoly>();
        foreach (Poly poly in ctx.RenderWorld.Visible)
        {
            if (hidden.Contains(poly.Surface.BrushId))
            {
                continue;
            }

            if (SurfaceLook.Resolve(ctx.Library, poly.Surface.Material, poly.Surface.Flags).Unlit)
            {
                unlit.Add(WorldPreview.ToPreview(poly));
            }
            else
            {
                lit.Add(new LitPoly(poly, ctx.ZoneAt(poly.Centroid() + (poly.Plane.N * 2))));
            }
        }

        LightingSettings s = lighting.Settings;
        BakeQuality full = lighting.Quality, plain = new();
        var passes = new List<(string Name, double Density, BakeQuality Quality, LightmapMode Mode)>();
        if (start != LightPreviewStart.Lights)
        {
            passes.Add(("coarse", 0.25, start == LightPreviewStart.Cold ? plain with { Shadows = false } : plain, LightmapMode.Quality));
        }

        if (s.Mode == LightmapMode.Faithful)
        {
            passes.Add(("faithful", 1, full, LightmapMode.Faithful));
        }
        else
        {
            if (start == LightPreviewStart.Cold)
            {
                passes.Add(("shadows", 0.5, plain, LightmapMode.Quality));
            }

            passes.Add(("full", 1, plain, LightmapMode.Quality));
            if (full != plain)
            {
                passes.Add(("final", 1, full, LightmapMode.Quality));
            }
        }

        // Each surface takes only the lights listed in the leaves it borders, as a build of these brushes bakes it. With
        // the baked render kept, the world here holds only the added brushes, not the source map's rooms, so its added
        // surfaces take every light in reach, as its build bakes them; with only the baked collision kept, the render is
        // rebuilt from every brush and its lights flood that whole world, as in the build (WorldLighting.LeafLists).
        Func<LightmapChart, BakeLight, bool>? admits = ctx.Baked is { KeepRender: true } ? null : lighting.LeafLists().Admits;
        var sources = new Dictionary<Poly, PreviewPolygon>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < passes.Count; i++)
        {
            (string name, double density, BakeQuality q, LightmapMode mode) = passes[i];
            LightingSettings passSettings = s.Clone();
            passSettings.Mode = mode;
            List<LightmapChart> charts = ChartBuilder.Build(lit, passSettings, density);
            LightmapResult result = _baker.Bake(lighting.Scene(charts, admits), q, s.AtlasSize, cancel, slot: name);
            var litPolygons = new List<LitPreviewPolygon>();
            foreach (LightmapChart chart in result.Charts)
            {
                foreach (ChartPiece piece in chart.Pieces)
                {
                    var poly = (Poly)piece.Source!;
                    if (!sources.TryGetValue(poly, out PreviewPolygon? source))
                    {
                        sources[poly] = source = WorldPreview.ToPreview(poly);
                    }

                    litPolygons.Add(new LitPreviewPolygon(source, poly.Verts, chart.Atlas, poly.Verts.Select(v => result.Uv(chart, v)).ToArray(),
                        chart.TexelSize));
                }
            }

            var atlases = Enumerable.Range(0, result.Atlases.Count).Select(a => (result.Atlases[a].Width, result.Atlases[a].Height, result.Rgba(a))).ToList();
            bool final = i == passes.Count - 1;
            var meshClock = System.Diagnostics.Stopwatch.StartNew();
            int baked = 0;
            IReadOnlyDictionary<string, LitPreviewMesh> meshes = final ? MeshLight(ctx, lighting, sourceMap, ref baked, cancel) : new Dictionary<string, LitPreviewMesh>();
            var meshStats = new MeshLightStats(meshes.Count, baked, final ? meshClock.ElapsedMilliseconds : 0);
            IReadOnlyList<SourceLightmapTriangle> sourceLightmaps = final && sourceMap is not null ? SourceLightmaps(sourceMap) : [];
            double defaultTexel = s.Mode == LightmapMode.Faithful ? BrushFace.DefaultLightMapScale : BrushFace.DefaultLightMapScale / s.Density;
            yield return new LightPreviewPass(name, final, litPolygons, unlit, atlases, meshes, sourceLightmaps, defaultTexel, result.Stats, meshStats);
        }
    }

    /// <summary>
    /// The light each placed static mesh that bakes vertex light shows in game: the vertex light a build stores for it
    /// plus what OpenSCCT's vertex-lit shader adds, all in half units: the AmbientGlow term (2 x glow / 255, capped at
    /// 1) and the zone ambient (m_RefAmbientColor / 255 at a gain of 2), the sum capped at the 2x stage. The zone is
    /// the actor's. On an imported map a build keeps the source map's instance while the actor shows the same mesh,
    /// with the vertex light the source baked (or none: the game then lights it at run time), so those are shown
    /// as stored; only meshes added or swapped since the import are baked here.
    /// </summary>
    private static Dictionary<string, LitPreviewMesh> MeshLight(CompileContext ctx, WorldLighting lighting, ConvertedWorld? source,
        ref int baked, CancellationToken cancel)
    {
        var meshes = new Dictionary<string, LitPreviewMesh>(StringComparer.OrdinalIgnoreCase);
        foreach ((Actor a, bool gameplay, string path) in lighting.MeshActors())
        {
            cancel.ThrowIfCancellationRequested();
            if (!lighting.DrawsVertexLit(a, gameplay))
            {
                continue;
            }

            IReadOnlyDictionary<int, byte[]>? stored;
            if (source?.Instances.GetValueOrDefault(a.Id) is { } kept && ConvertedWorld.SameMesh(kept.Mesh, path))
            {
                stored = kept.VertexLight;
            }
            else
            {
                stored = lighting.MeshPrimitives(path) is { } prims ? lighting.VertexLight(a, prims) : null;
                baked += stored is null ? 0 : 1;
            }

            if (stored is null)
            {
                continue;
            }

            double glow = Math.Min(1, 2 * ctx.Rules.Number(a, "AmbientGlow") / 255) / 2;
            (int r, int g, int b) = lighting.ZoneAmbient(ctx.ZoneAt(a.Location));
            var add = new Vec3(glow + (r / 255.0), glow + (g / 255.0), glow + (b / 255.0));
            meshes[a.Id] = new LitPreviewMesh(stored.ToDictionary(p => p.Key, p => Shown(p.Value, add)));
        }

        return meshes;
    }

    /// <summary>The imported map's lightmap triangles (built once per source map).</summary>
    private List<SourceLightmapTriangle> SourceLightmaps(ConvertedWorld source)
    {
        if (_sourceLightmaps is { } cached && ReferenceEquals(cached.World, source))
        {
            return cached.Triangles;
        }

        var triangles = new List<SourceLightmapTriangle>();
        foreach (LightmapChart chart in source.Charts)
        {
            (int w, int h) = source.BakeSize(chart.Atlas);
            foreach (ChartPiece piece in chart.Pieces)
            {
                TexelPoint[] c = piece.Corners;
                triangles.Add(new SourceLightmapTriangle(c.Select(chart.World).ToArray(), chart.Normal,
                    c.Select(t => ((float)((chart.X + t.X) / w), (float)((chart.Y + t.Y) / h))).ToArray(), chart.Atlas, w, h, chart.TexelSize));
            }
        }

        _sourceLightmaps = (source, triangles);
        return triangles;
    }

    private static byte[] Shown(byte[] stored, Vec3 add)
    {
        var shown = new byte[stored.Length];
        for (int i = 0; i + 3 < stored.Length; i += 4)
        {
            shown[i] = Shown(stored[i], add.X);
            shown[i + 1] = Shown(stored[i + 1], add.Y);
            shown[i + 2] = Shown(stored[i + 2], add.Z);
            shown[i + 3] = stored[i + 3];
        }

        return shown;
    }

    private static byte Shown(byte stored, double add) => (byte)Math.Round(Math.Min(1, (stored / 255.0) + add) * 255);

    /// <summary>The build's world steps for the preview: actors sorted, the CSG world with its BSP and zones (or a baked
    /// map's zones and its own world as occluders), and the shadow scene with sky zones letting sunlight in.</summary>
    private (WorldLighting Lighting, ConvertedWorld? Source) World(CompileContext ctx)
    {
        if (ctx.Project.Baked is { } baked)
        {
            ctx.Baked = BakedSource.Open(ctx, baked);
            if (ctx.Baked is not null)
            {
                // The source map's own materials, as the build finds them (alpha masks and bounce colours).
                ctx.Library = ctx.Library.WithSource(ctx.Baked.Folder);
            }
        }

        GameplayBuild.SortActors(ctx);
        ConvertedWorld? source = null;
        // A run that a newer edit cancelled may still be in here; the world cache is not thread safe.
        lock (_worldLock)
        {
            MapCompiler.BuildWorld(ctx, _world);
            if (ctx.Baked is { KeepRender: true } b && File.Exists(Path.Combine(b.Folder, "render", "world.glb")) &&
                File.Exists(Path.Combine(b.Folder, "render", "lighting.json")))
            {
                source = _source = _source?.Folder == b.Folder ? _source : ConvertedWorld.Load(b.Folder, castsShadow: null);
            }
        }

        var lighting = new WorldLighting(ctx);
        lighting.AddSurfaces(ctx.RenderWorld.Visible, p => ctx.InSkyZone(p.Centroid() + (p.Plane.N * 2)));
        if (source is not null)
        {
            lighting.AddOccluders(source.Occluders);
        }

        lighting.AddMeshOccluders();
        return (lighting, source);
    }
}
