using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose bake</c>: builds a project's lighting with the project's lightmap settings or ones given for this
/// bake, and reports what the bake made.</summary>
public static class BakeCommands
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    [CliProvider]
    public static IEnumerable<CliCommand> Commands() =>
    [
        new("bake", "bake <project.osmap> [--mode quality|faithful] [--density N] [--samples N] [--light-size uu] [--sun-size deg] " +
            "[--bounce G] [--bounce-samples N] [--atlas 512|1024|2048|4096] [--relight-imported true|false] [--fast] [--save] [--out <folder>]",
            "Builds the map (default <project>/../build/<MapId>) with its lightmaps baked by the project's lighting settings, or by the ones " +
            "given here for this bake (--save also stores them in the project). Prints the settings used, the atlas files, the charts, " +
            "texels and rays, the vertex-lit meshes and the time taken.",
            BakeAsync),
    ];

    private static Task<int> BakeAsync(CliArgs a)
    {
        string path = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        EditorServices.Discover();
        MapProject project = ProjectStore.Load(path);
        LightingSettings settings = Overrides(a, project.Lighting);
        if (settings.Problem() is { } problem)
        {
            throw new ArgumentException(problem);
        }

        project.Lighting = settings;
        if (a.Has("save"))
        {
            ProjectStore.Save(project, path);
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('/', '\\')) ?? ".";
        string output = a.Get("out") ?? Path.Combine(projectDir, "build", project.Id);
        IMapCompiler compiler = EditorServices.Compiler ?? new MapCompiler();
        CompileResult result = compiler.Compile(project, output, new CompileOptions
        {
            FastLighting = a.Has("fast"),
            ContentRoot = a.Get("content") ?? EditorServices.ContentRoot,
            ProjectFolder = Path.GetFullPath(path),
        });

        var stats = new JsonObject();
        foreach ((string key, long value) in result.Stats.Where(kv => kv.Key.StartsWith("lightmap", StringComparison.Ordinal) ||
            kv.Key is "render.meshesVertexLit" or "render.lights").OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            stats[key] = value;
        }

        string textures = Path.Combine(result.OutputFolder, "render", "textures");
        var json = new JsonObject
        {
            ["ok"] = result.Ok,
            ["map"] = project.Id,
            ["out"] = result.OutputFolder,
            ["elapsedMs"] = (long)result.Elapsed.TotalMilliseconds,
            ["settings"] = JsonNode.Parse(CanonicalJson.ToCanonicalString(ProjectStore.LightingToJson(settings))),
            ["saved"] = a.Has("save"),
            ["lightmaps"] = new JsonArray((Directory.Exists(textures)
                ? Directory.EnumerateFiles(textures, $"{project.Id}_*lightmap*.png").Order(StringComparer.Ordinal).Select(f => (JsonNode)f)
                : []).ToArray()),
            ["stats"] = stats,
            ["errors"] = result.Findings.Count(f => f.Severity == Severity.Error),
            ["warnings"] = result.Findings.Count(f => f.Severity == Severity.Warning),
            ["findings"] = new JsonArray(result.Findings.Where(f => f.Code.StartsWith("lighting", StringComparison.Ordinal) || f.Severity == Severity.Error)
                .Select(f => (JsonNode)new JsonObject { ["severity"] = f.Severity.ToString().ToLowerInvariant(), ["code"] = f.Code, ["message"] = f.Message })
                .ToArray()),
        };
        Console.WriteLine(json.ToJsonString(s_compact));
        return Task.FromResult(result.Ok ? 0 : 1);
    }

    /// <summary>The project's settings with the command line's on top.</summary>
    private static LightingSettings Overrides(CliArgs a, LightingSettings current)
    {
        LightingSettings s = current.Clone();
        if (a.Get("mode") is { } mode)
        {
            s.Mode = mode.ToLowerInvariant() switch
            {
                "quality" => LightmapMode.Quality,
                "faithful" => LightmapMode.Faithful,
                _ => throw new ArgumentException($"--mode is quality or faithful, not {mode}"),
            };
        }

        s.Density = Number(a, "density", s.Density);
        s.Samples = (int)Number(a, "samples", s.Samples);
        s.LightSize = Number(a, "light-size", s.LightSize);
        s.SunSize = Number(a, "sun-size", s.SunSize);
        s.Bounce = Number(a, "bounce", s.Bounce);
        s.BounceSamples = (int)Number(a, "bounce-samples", s.BounceSamples);
        s.AtlasSize = (int)Number(a, "atlas", s.AtlasSize);
        if (a.Get("relight-imported") is { } relight)
        {
            s.RelightImported = bool.TryParse(relight, out bool b) ? b : throw new ArgumentException("--relight-imported is true or false");
        }

        return s;
    }

    private static double Number(CliArgs a, string key, double fallback) =>
        a.Get(key) is not { } text ? fallback
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v
        : throw new ArgumentException($"--{key} is a number, not {text}");
}
