using System.Diagnostics;
using OpenScct.Content;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// Compiles a project to an SVMap folder (1.1, or 1.2 with the map's identity, match rules, outfits or conveyors): actor sorting,
/// the CSG world with its BSP and zones, then <c>sim/</c> (collision, gameplay, traversal, match), <c>render/</c> (with
/// outfits), <c>ui/</c> (map info, strategic map, identity and pictures), <c>audio/</c> and the manifest; the result is
/// loaded back with OpenSCCT's strict loader. The same project always gives the same bytes.
/// </summary>
[EditorService]
public sealed class MapCompiler : IMapCompiler
{
    /// <summary>The stock engine's zone limit (FBspNode zone masks are 64 bits).</summary>
    public const int OriginalMaxZones = 64;

    public CompileResult Compile(MapProject project, string outputFolder, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(outputFolder);
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();
        var ctx = new CompileContext(project, options, EditorServices.Classes);
        string folder = Path.GetFullPath(outputFolder);
        try
        {
            Run(ctx, folder, clock);
        }
        catch (IOException e)
        {
            ctx.Error("compile.io", e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            ctx.Error("compile.io", e.Message);
        }
        catch (Exception e) when (e is SvMapFormatException or InvalidDataException or InvalidOperationException or ArgumentException
            or FormatException or System.Text.Json.JsonException)
        {
            // Bad input data (a broken baked source map, a library file that does not parse): report it, do not crash the editor.
            ctx.Error("compile.failed", $"{e.GetType().Name}: {e.Message}");
        }

        ctx.Count("elapsedMs", clock.ElapsedMilliseconds);
        return new CompileResult(!ctx.HasErrors, folder, ctx.Findings, clock.Elapsed, ctx.Stats);
    }

    private static void Run(CompileContext ctx, string folder, Stopwatch clock)
    {
        MapProject project = ctx.Project;
        if (!GameplayBuild.IsIdentifier(project.Id))
        {
            ctx.Error("compile.map-id", $"map id '{project.Id}' is not an identifier (letters, digits and _, at most 63)");
            return;
        }

        if (!string.Equals(Path.GetFileName(folder), project.Id, StringComparison.Ordinal))
        {
            ctx.Warn("compile.folder", $"the output folder is named {Path.GetFileName(folder)}; OpenSCCT finds maps by a folder named {project.Id}");
        }

        ctx.Info("compile.classes", $"class defaults: {ctx.Classes.Source}");
        if (project.Baked is { } bakedSettings)
        {
            ctx.Baked = BakedSource.Open(ctx, bakedSettings);
            if (ctx.Baked is null)
            {
                return;
            }

            // The source map's own materials and meshes (map-local ones no library map ships).
            ctx.Library = ctx.Library.WithSource(ctx.Baked.Folder);
        }

        GameplayBuild.SortActors(ctx);
        BuildWorld(ctx);
        ctx.Count("csgMs", clock.ElapsedMilliseconds);
        CheckLimits(ctx);

        // The collision comes first: the strategic map floors and camera are found from it and go in the LevelInfo.
        ctx.Progress("collision");
        CollisionBuild.Write(ctx);
        StrategicMapBuild.Plan(ctx);
        GameplayBuild.Write(ctx);
        TraversalBuild.Write(ctx);
        ctx.Progress("render");
        RenderBuild.Write(ctx);
        SideFiles.Write(ctx);
        StrategicMapBuild.WritePictures(ctx);
        MatchBuild.Write(ctx);
        MoverLegsBuild.Write(ctx);
        // Camera pictures are keyed on the complete sim and render output.
        IdentityBuild.Write(ctx);
        if (ctx.HasErrors)
        {
            ctx.Info("compile.not-written", "the map was not written because of the errors above");
            return;
        }

        var requires = new List<(string, string)>();
        if (ctx.Options.WriteClassTable && ctx.Files.ContainsKey(ClassTable.RelativePath) && ctx.ContentRoot is { } content)
        {
            requires.Add(("classes", SvMapHash.Sha256Hex(File.ReadAllBytes(Path.Combine(content, "shared", "classes.json")))));
        }

        ctx.Progress("write");
        MapFolder.Write(folder, project.Id, project.Title, GameplayBuild.FormatVersion, ctx.Files, requires);
        ctx.Count("files", ctx.Files.Count + 1);
        ctx.Count("bytes", ctx.Files.Values.Sum(f => (long)f.Length));
        if (ctx.Options.Validate)
        {
            ctx.Progress("validate");
            (List<Finding> findings, _) = MapFolder.Validate(folder);
            ctx.Findings.AddRange(findings);
            ctx.Count("validated", findings.Any(f => f.Severity == Severity.Error) ? 0 : 1);
        }
    }

    /// <summary>The finding code of two zone actors in one unsealed space.</summary>
    public const string ZoneLeakCode = "zones.leak";

    /// <summary>The finding code of a zone actor whose space shares a zone number with another's (more spaces than
    /// zone numbers).</summary>
    public const string ZoneSharedCode = "zones.shared";

    /// <summary>The finding code of a zone portal that seals nothing.</summary>
    public const string ZonePortalCode = "zones.portal-seals-nothing";

    private static List<FindingShape> Shapes(IReadOnlyList<Vec3> path, IReadOnlyList<Vec3[]> outlines)
    {
        var shapes = new List<FindingShape>();
        if (path.Count >= 2)
        {
            shapes.Add(new FindingShape(FindingShapeKind.Path, path));
        }

        shapes.AddRange(outlines.Select(o => new FindingShape(FindingShapeKind.Outline, o)));
        return shapes;
    }

    /// <summary>The CSG worlds, BSP and zones of the brushes; while the baked collision is kept the source map's zones.
    /// The Lit Preview runs it too, with its own <paramref name="cache"/>, so it lights by the same zones as the build.</summary>
    internal static void BuildWorld(CompileContext ctx, WorldCache? cache = null)
    {
        BrushLayer collision = BakedContent.Collision(ctx.Project);
        BrushLayer render = BakedContent.Render(ctx.Project);
        bool twoWorlds = BakedContent.LayersDiffer(ctx.Project);
        var seeds = new List<ZoneSeed>();
        if (collision.WorldSolid)
        {
            foreach (Actor a in ctx.Gameplay)
            {
                if (ctx.Rules.IsZoneActor(a))
                {
                    seeds.Add(new ZoneSeed(a.Id, a.Location, ctx.Rules.IsSkyZone(a)));
                }
            }
        }

        if (ctx.Project.Baked is { OneLayer: true } one)
        {
            int baked = ctx.Project.Brushes.Count(BakedContent.IsBaked);
            if (twoWorlds)
            {
                ctx.Info("baked.one-layer", one.KeepRender
                    ? $"only the baked render is kept: the collision and zones are rebuilt from the {baked} baked brushes and the rest, the "
                        + "zones numbered as the source map's, which the kept lighting names"
                    : $"only the baked collision is kept: the render is rebuilt from the {baked} baked brushes and the rest");
            }
            else
            {
                ctx.Warn("baked.one-layer", one.KeepRender
                    ? "only the baked render is kept and the map has no brushes to rebuild the collision from: it has no world collision"
                    : "only the baked collision is kept and the map has no brushes to rebuild the render from: its world is not drawn");
            }
        }

        // Kept, the baked render keeps its lighting, which names the zones by the source map's numbers: the collision
        // rebuilt from the brushes numbers its spaces by the source's zones, so the two agree.
        ZoneNumbering numbering = ctx.Baked is { KeepRender: true, KeepCollision: false } kept
            ? ZoneNumbering.For(ctx.Options.Target) with { Source = new SourceZones(kept.ZoneAt, kept.ZoneActors.ToList()) }
            : ZoneNumbering.For(ctx.Options.Target);
        ctx.Progress($"csg: {render.Brushes.Count} brushes");
        cache ??= new WorldCache();
        ctx.RenderWorld = WorldModel.Build(render.Brushes, seeds, render.WorldSolid, twoWorlds ? null : numbering, cache.Render);
        ctx.World = twoWorlds ? cache.CollisionWorld(collision.Brushes, seeds, collision.WorldSolid, numbering) : ctx.RenderWorld;
        foreach ((string brush, string problem) in ctx.World.BrushProblems.Union(ctx.RenderWorld.BrushProblems))
        {
            ctx.Warn("csg.brush", $"brush {brush}: {problem}", ObjectRef.Brush(brush));
        }

        ctx.ZoneActors.Add(ctx.LevelInfoId);
        if (ctx.Baked is { KeepCollision: true } source)
        {
            ctx.ZoneAt = source.ZoneAt;
            foreach (string? id in source.ZoneActors.Skip(1))
            {
                ctx.ZoneActors.Add(id is not null && ctx.GameplayIds.Contains(id) ? id : null);
            }
        }
        else
        {
            WorldModel world = ctx.World;
            ctx.ZoneAt = p => world.ZoneAt(p);
            foreach (ZoneEntry z in world.Zones.Zones.Where(z => z.Index > 0).OrderBy(z => z.Index))
            {
                ctx.ZoneActors.Add(z.ActorId);
            }

            foreach (ZoneProblem problem in world.Zones.Problems)
            {
                ctx.Warn("zones", problem.Message, ObjectRef.Actor(problem.ActorId));
            }

            foreach ((string id, string other, int zone) in world.Zones.Shared)
            {
                ctx.Warn(ZoneSharedCode, $"zone actor {id} has no zone of its own: its space shares zone {zone} with {other}'s, because the map has "
                    + $"{world.Zones.Spaces} sealed spaces and {(ctx.Options.Target == TargetGame.Original ? "the original engine" : "an SVMap")} has "
                    + $"{numbering.MaxZone} zone numbers; remove zone portals to merge small spaces",
                    ObjectRef.Actor(id), ObjectRef.Actor(other));
            }

            foreach (ZoneLeak leak in world.Zones.Leaks)
            {
                ctx.Findings.Add(Finding.Warning(ZoneLeakCode, leak.Message, ObjectRef.Actor(leak.ActorId), ObjectRef.Actor(leak.OtherId)).At(leak.Location)
                    with { Shapes = Shapes(leak.Path, leak.Cut) });
            }

            foreach (ZonePortalProblem portal in world.Zones.Portals)
            {
                ctx.Findings.Add(Finding.Warning(ZonePortalCode, portal.Message, ObjectRef.Brush(portal.BrushId)).At(portal.At)
                    with { Shapes = Shapes(portal.Path, portal.Sheets) });
            }

            if (world.Zones.Wrapped)
            {
                int max = numbering.MaxZone;
                ctx.Info("zones.wrapped", $"{world.Zones.Spaces} sealed spaces share the {max} zone numbers: those with a zone actor are numbered "
                    + $"first, the rest share the numbers left (the stock editor wraps them all in its leaf order, space n being zone n % {max} + 1)");
            }
        }

        int built = Math.Max(collision.Brushes.Count, render.Brushes.Count);
        ctx.Count("brushes", built);
        if (built < ctx.Project.Brushes.Count)
        {
            // Imported with the baked world, which already holds them.
            ctx.Count("brushes.baked", ctx.Project.Brushes.Count - built);
        }

        if (twoWorlds)
        {
            ctx.Count("brushes.collision", collision.Brushes.Count);
            ctx.Count("brushes.render", render.Brushes.Count);
        }
        ctx.Count("csg.polygons", ctx.World.Csg.Polys.Count());
        ctx.Count("csg.visible", ctx.RenderWorld.Visible.Count);
        ctx.Count("bsp.nodes", ctx.World.Tree.NodeCount);
        ctx.Count("bsp.leaves", ctx.World.Tree.Leaves.Count);
        ctx.Count("bsp.fixedLeaves", ctx.World.FixedLeaves);
        ctx.Count("zones", ctx.ZoneActors.Count);
    }

    private static void CheckLimits(CompileContext ctx)
    {
        int zones = ctx.ZoneActors.Count;
        bool original = ctx.Options.Target == TargetGame.Original;
        if (original && zones > OriginalMaxZones)
        {
            ctx.Error("limits.zones", $"{zones} zones; the original engine allows {OriginalMaxZones}");
        }
        else if (zones > 255)
        {
            ctx.Error("limits.zones", $"{zones} zones; SVMap zone numbers are bytes (255 at most)");
        }

        if (original)
        {
            // The same limits as Map Check's limit rules.
            if (!Limits.IsMapName(ctx.MapId))
            {
                ctx.Error("limits.map-name", $"map name '{ctx.MapId}' must be exactly {Limits.MapNameLength} letters or digits for the original game's map lists");
            }

            int triggerLights = ctx.Project.Actors.Count(a => ctx.Rules.IsA(a, "SBase.STriggerLight"));
            if (triggerLights > Limits.TriggerLightCap)
            {
                ctx.Error("limits.trigger-lights", $"{triggerLights} STriggerLight actors; the game holds {Limits.TriggerLightCap}");
            }
            else if (triggerLights > Limits.TriggerLightWarning)
            {
                ctx.Warn("limits.trigger-lights", $"{triggerLights} STriggerLight actors; more than {Limits.TriggerLightWarning} needs the raised cap");
            }
        }

        if (ctx.World.Tree.Root is BspLeaf { Empty: false } && !ctx.BakedWorld && ctx.Project.Brushes.Count == 0)
        {
            ctx.Warn("world.empty", "the map has no brushes: the world is solid everywhere");
        }

        foreach (Actor a in ctx.Gameplay)
        {
            if (ctx.World.Csg.WorldSolid && ctx.Rules.IsA(a, "Engine.NavigationPoint") && !ctx.World.Tree.IsEmpty(a.Location))
            {
                ctx.Warn("world.in-solid", $"actor {a.Id} stands in solid world", ObjectRef.Actor(a.Id));
            }
        }
    }
}
