using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Everything one compile shares between its steps: inputs, actor sorting, the world, zones, findings, files.</summary>
public sealed class CompileContext
{
    public const string Generator = "openscct-editor";
    public const int GeneratorVersion = 1;

    public CompileContext(MapProject project, CompileOptions options, IClassCatalog? catalog)
    {
        Project = project;
        Options = options;
        ContentRoot = options.ContentRoot ?? EditorServices.ContentRoot;
        Classes = ClassDefaults.Create(ContentRoot, catalog);
        Rules = new ActorRules(Classes);
        Library = AssetResolver.For(ContentRoot, options.ProjectFolder);
        LevelInfoId = Extra("levelInfoId") ?? "LevelInfo0";
        LevelInfoClass = Extra("levelInfoClass") ?? "Engine.LevelInfo";
    }

    public MapProject Project { get; }

    public CompileOptions Options { get; }

    public string MapId => Project.Id;

    public string? ContentRoot { get; }

    public ClassDefaults Classes { get; }

    public ActorRules Rules { get; }

    /// <summary>Where materials and static meshes come from: the project's own assets, then the converted maps, then
    /// the baked source map (set once it is open).</summary>
    public AssetResolver Library { get; set; }

    public string LevelInfoId { get; }

    public string LevelInfoClass { get; }

    public List<Finding> Findings { get; } = new();

    public SortedDictionary<string, long> Stats { get; } = new(StringComparer.Ordinal);

    /// <summary>Output files by path under the map folder (sim/..., render/..., ui/..., audio/...).</summary>
    public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Gameplay actors in project order (ids checked).</summary>
    public List<Actor> Gameplay { get; } = new();

    /// <summary>Render-only actors in project order.</summary>
    public List<Actor> Render { get; } = new();

    /// <summary>The cooked-out lights of an .sdc import (<see cref="BakedContent"/>): they lit the source map's lightmaps,
    /// so a bake here (a relight of those lightmaps, surfaces and meshes added to the map, or a rebuild without the baked
    /// render) lights with them as the original editor did, but the build ships them nowhere, as the cook dropped them.</summary>
    public List<Actor> BakeOnlyLights { get; } = new();

    /// <summary>Ids of everything in gameplay.json (the LevelInfo included), ignoring case.</summary>
    public HashSet<string> GameplayIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The CSG world the collision and the zones are built from (<see cref="BakedContent.Collision"/>: the
    /// added brushes in an empty outside while the baked collision is kept).</summary>
    public WorldModel World { get; set; } = null!;

    /// <summary>The CSG world the render, the bake and the Lit Preview are built from (<see cref="BakedContent.Render"/>);
    /// the same as <see cref="World"/> unless only one baked layer is kept and the other is rebuilt from the brushes.</summary>
    public WorldModel RenderWorld { get; set; } = null!;

    /// <summary>The baked source map, when the project carries one.</summary>
    public BakedSource? Baked { get; set; }

    /// <summary>True when the baked world stands in for the brushes it holds (its render or its collision is kept).
    /// With neither kept the map is rebuilt from every brush, and the source map only supplies its own materials and
    /// static meshes (through <see cref="Library"/>).</summary>
    public bool BakedWorld => Baked is { KeepRender: true } or { KeepCollision: true };

    /// <summary>The zone index of a point (the world BSP's, or the baked map's).</summary>
    public Func<Vec3, int> ZoneAt { get; set; } = _ => 0;

    /// <summary>Zone actor ids by zone index; zone 0 is the LevelInfo.</summary>
    public List<string?> ZoneActors { get; } = new();

    /// <summary>The strategic map floors and camera (<see cref="StrategicMapBuild.Plan"/>), or null when the map has none.</summary>
    public StrategicMap? StrategicMap { get; set; }

    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);

    public void Error(string code, string message, params ObjectRef[] objects) => Findings.Add(Finding.Error(code, message, objects));

    public void Warn(string code, string message, params ObjectRef[] objects) => Findings.Add(Finding.Warning(code, message, objects));

    public void Info(string code, string message, params ObjectRef[] objects) => Findings.Add(Finding.Info(code, message, objects));

    public void Count(string key, long n) => Stats[key] = n;

    public void Progress(string line) => Options.Progress?.Invoke(line);

    /// <summary>The zone actor of a point, or the LevelInfo.</summary>
    public string ZoneActorAt(Vec3 p)
    {
        int z = ZoneAt(p);
        return z > 0 && z < ZoneActors.Count && ZoneActors[z] is { } id ? id : LevelInfoId;
    }

    /// <summary>True when the point is in a sky zone (a SkyZoneInfo's zone).</summary>
    public bool InSkyZone(Vec3 p)
    {
        int z = ZoneAt(p);
        return z > 0 && z < ZoneActors.Count && ZoneActors[z] is { } id &&
            Gameplay.Find(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) is { } zone && Rules.IsSkyZone(zone);
    }

    private string? Extra(string key) => Project.Extra[key] is SvJsonString s && s.Value.Length > 0 ? s.Value : null;
}
