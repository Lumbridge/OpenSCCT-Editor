using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Particle systems are stored with their actor so they travel through undo, copying and prefabs.</summary>
public static class EmitterBuild
{
    public const string SystemsKey = "particleSystems";
    public const string TextureOriginKey = "textureOrigin";

    public static SvJsonArray? SystemsOf(Actor actor, MapProject? project = null)
    {
        if (actor.Source?.Contains(SystemsKey, StringComparison.Ordinal) == true && SourceOf(actor)?[SystemsKey] is SvJsonArray systems)
        {
            return systems;
        }

        var legacy = (project?.Extra[SvMapImport.ExtraKey] as SvJsonObject)?["emitters"] as SvJsonObject;
        return legacy?[actor.Id] is SvJsonArray original ? WithTextureOrigin(original, MapOrigin(project?.Baked?.SourceMap)) : null;
    }

    internal static string? MapOrigin(string? path) => path is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    internal static SvJsonArray WithTextureOrigin(SvJsonArray systems, string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return systems;
        }

        var result = new SvJsonArray();
        foreach (SvJsonNode node in systems)
        {
            if (node is not SvJsonObject system || system.ContainsKey(TextureOriginKey))
            {
                result.Add(node);
                continue;
            }

            var part = new SvJsonObject();
            foreach ((string key, SvJsonNode value) in system)
            {
                part.Add(key, value);
            }

            part.Add(TextureOriginKey, SvJsonNode.String(origin));
            result.Add(part);
        }

        return result;
    }

    /// <summary>Sets the typed particle systems. Imports may also keep their exact original T3D for round trips.</summary>
    public static void WithSystems(Actor actor, SvJsonArray systems, bool preserveOriginalObjects = false)
    {
        var source = new SvJsonObject();
        if (SourceOf(actor) is { } old)
        {
            foreach ((string key, SvJsonNode value) in old)
            {
                if (key != SystemsKey && (preserveOriginalObjects || key != T3dImport.ObjectsKey))
                {
                    source.Add(key, value);
                }
            }
        }

        source.Add(SystemsKey, systems);
        actor.Source = InteropValues.Compact(source);
    }

    public static JsonObject? Record(Actor actor) => Record(actor, SystemsOf(actor));

    internal static JsonObject? Record(Actor actor, SvJsonArray? systems)
    {
        if (systems is null)
        {
            return null;
        }

        var parts = new JsonArray();
        foreach (SvJsonObject system in ValidSystems(systems))
        {
            parts.Add(new JsonObject
            {
                ["class"] = Text(system["class"]),
                ["object"] = Text(system["object"]) ?? $"{actor.Id}.Particle{parts.Count}",
                ["props"] = JsonNode.Parse(InteropValues.Compact(system["props"]!)),
            });
        }

        PropSet props = actor.Props.Clone();
        props.Remove("Emitters");
        props.Set("Location", PropValues.Vector(actor.Location));
        props.Set("Rotation", PropValues.Rotator(actor.Rotation));
        props.Set("DrawScale", SvJsonNode.Float((float)actor.DrawScale));
        props.Set("DrawScale3D", PropValues.Vector(actor.DrawScale3D));
        props.Set("PrePivot", PropValues.Vector(actor.PrePivot));
        props.Set("Tag", SvJsonNode.String(actor.Tag));
        return new JsonObject
        {
            ["class"] = actor.Class,
            ["props"] = JsonNode.Parse(InteropValues.Compact(props.ToJson())),
            ["emitters"] = parts,
        };
    }

    /// <summary>Finds the actual sprite PNG in converted content, preferring the exact source when one is recorded.</summary>
    public static (string RenderDir, string Origin, string Relative)? TextureOf(AssetResolver library, string? contentRoot, SvJsonObject system)
    {
        string? origin = Text(system[TextureOriginKey]);
        string? relative = Text(system["texture"]);
        if (relative is not null)
        {
            string? map = origin is null ? library.Source?.MapDirs().FirstOrDefault()
                : IsFileComponent(origin) ? library.Source?.MapFolder(origin) ?? library.Content.MapFolder(origin) : null;
            if (map is not null && TextureIn(Path.Combine(map, "render"), Path.GetFileName(map), relative) is { } exact)
            {
                return exact;
            }
        }

        if (system["props"] is not SvJsonObject props || PropValues.AssetPath(props["Texture"]) is not { } asset)
        {
            return null;
        }

        if (library.FindMaterial(asset) is { Texture: { } materialTexture } material &&
            TextureIn(material.RenderDir, material.Origin, materialTexture) is { } found)
        {
            return found;
        }

        // Some sprite textures are only used by emitters and have no materials.json entry.
        string shortName = AssetPaths.Short(asset);
        if (IsFileComponent(shortName))
        {
            string safe = string.Concat(shortName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
            string particlePng = $"textures/{safe}.png";
            foreach (string map in library.Content.MapDirs().Concat(library.Source?.MapDirs() ?? []))
            {
                if (TextureIn(Path.Combine(map, "render"), Path.GetFileName(map), particlePng) is { } converted)
                {
                    return converted;
                }
            }
        }

        return SharedTexture(contentRoot, asset);
    }

    internal static IEnumerable<SvJsonObject> ValidSystems(SvJsonArray systems) =>
        systems.OfType<SvJsonObject>().Where(system => Text(system["class"]) is { Length: > 0 } && system["props"] is SvJsonObject);

    internal static string? Text(SvJsonNode? node) => (node as SvJsonString)?.Value;

    private static (string RenderDir, string Origin, string Relative)? SharedTexture(string? contentRoot, string asset)
    {
        string? dir = contentRoot is null ? null : Path.Combine(contentRoot, "shared", "fx");
        string? index = dir is null ? null : Path.Combine(dir, "effects.json");
        if (index is null || !File.Exists(index))
        {
            return null;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(index));
            if (json.RootElement.TryGetProperty("textures", out JsonElement textures) &&
                textures.TryGetProperty(asset, out JsonElement entry) && entry.TryGetProperty("png", out JsonElement png) &&
                png.ValueKind == JsonValueKind.String && png.GetString() is { } relative)
            {
                return TextureIn(dir!, "fx", relative);
            }
        }
        catch (JsonException)
        {
            // The optional shared pack is unavailable. The caller reports unresolved textures.
        }

        return null;
    }

    private static (string RenderDir, string Origin, string Relative)? TextureIn(string render, string origin, string path)
    {
        string[] pieces = path.Split('/');
        if (pieces.Length != 2 || pieces[0] != "textures" || !IsFileComponent(pieces[1]) ||
            !pieces[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(render, path)))
        {
            return null;
        }

        return (render, origin, path);
    }

    private static bool IsFileComponent(string name) =>
        name.Length > 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static SvJsonObject? SourceOf(Actor actor)
    {
        if (actor.Source is not { Length: > 0 } source || source[0] != '{')
        {
            return null;
        }

        try
        {
            return SvJson.Parse(source) as SvJsonObject;
        }
        catch (SvMapFormatException)
        {
            return null;
        }
    }
}
