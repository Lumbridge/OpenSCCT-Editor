using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Bundles referenced sound files and composite dependencies in the compiled map.</summary>
public static class SoundBuild
{
    public static void Write(CompileContext ctx)
    {
        if (!ctx.Files.TryGetValue("audio/audio.json", out var bytes) || JsonNode.Parse(bytes) is not JsonObject audio) return;
        var sounds = (audio["sounds"] ??= new JsonObject()).AsObject();
        var catalog = AssetCatalog.For(ctx.ContentRoot, ctx.Options.ProjectFolder);
        var library = new SoundLibrary(catalog, ctx.ContentRoot, ctx.Options.ProjectFolder);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!visited.Add(path)) return;
            if (library.Resolve(path) is not { } source)
            {
                if (!sounds.ContainsKey(path)) ctx.Warn("SOUND_MISSING", $"Sound {path} is unavailable. Install its content pack before building.");
                return;
            }
            var record = source.Definition.DeepClone().AsObject();
            record["root"] = "map";
            if (source.File is { } file && File.Exists(file))
            {
                string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Path)))[..20].ToLowerInvariant();
                string relative = "sounds/" + name + Path.GetExtension(file).ToLowerInvariant();
                ctx.Files["audio/" + relative] = File.ReadAllBytes(file);
                record["file"] = relative;
            }
            else if (record["entries"] is not JsonArray)
            {
                // A retained source map may already supply a bank stream the catalogue cannot find on disk.
                if (!sounds.ContainsKey(source.Path)) ctx.Warn("SOUND_FILE_MISSING", $"Sound file for {source.Path} is unavailable.");
                return;
            }
            sounds[source.Path] = record;
            if (record["entries"] is JsonArray entries)
                foreach (var child in entries.OfType<JsonObject>())
                    if (child["sound"]?.GetValue<string>() is { } next) Add(next);
        }
        foreach (var actor in ctx.Render.Concat(ctx.Gameplay))
        {
            if (PropValues.AssetPath(ctx.Rules.Value(actor, "AmbientSound")) is { } ambient) Add(ambient);
            foreach (var property in actor.Props)
                if (PropValues.AssetPath(property.Value) is { } path && catalog.Find(AssetKind.Sound, path) is not null) Add(path);
        }
        if (PropValues.AssetPath(ctx.Project.LevelInfo["AmbientSound"]) is { } levelSound) Add(levelSound);
        ctx.Files["audio/audio.json"] = RenderBuild.Json(audio);
        ctx.Count("audio.sounds", sounds.Count);
    }
}
