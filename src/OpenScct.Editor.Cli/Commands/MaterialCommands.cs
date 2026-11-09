using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;

namespace OpenScct.Editor.Cli.Commands;

internal static class MaterialCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("texture-import", "texture-import <project.osmap> <images...> [--name N] [--group Textures] [--alpha auto|opaque|masked|translucent] [--conflict rename|replace|skip] [--sequence] [--fps 12] [--json]",
            "Import images or texture sets. --sequence packs numbered frames into an animated atlas. Names are unique by default; replacement is explicit.", Import),
        new("material", "material <show|create|update> <project.osmap> <MyLevel.Group.Name> [--definition material.json] [--json]",
            "Inspect or edit an authored material using the shared versioned JSON definition. Texture paths are relative to the project's assets directory.", Material),
    ];

    private static Task<int> Import(CliArgs args)
    {
        string folder = args.Positional(0) ?? throw new ArgumentException("Give a saved .osmap project.");
        var files = new List<string>();
        for (int i = 1; args.Positional(i) is { } file; i++) files.Add(file);
        if (files.Count == 0) throw new ArgumentException("Give one or more images.");
        List<TextureSetDraft> drafts = args.Has("sequence") ? [TextureImporter.PreviewSequence(files, float.Parse(args.Get("fps") ?? "12", System.Globalization.CultureInfo.InvariantCulture))] : TextureImporter.Preview(files).ToList();
        if (args.Get("name") is { } name) { if (drafts.Count != 1) throw new ArgumentException("--name needs exactly one texture set."); drafts[0].Name = name; }
        if (!Enum.TryParse(args.Get("conflict") ?? "rename", true, out TextureConflict conflict) || !Enum.IsDefined(conflict)) throw new ArgumentException("--conflict must be rename, replace or skip.");
        string alpha = args.Get("alpha") ?? "auto";
        if (alpha is not ("auto" or "opaque" or "masked" or "translucent")) throw new ArgumentException("--alpha must be auto, opaque, masked or translucent.");
        var results = new List<TextureImportResult>(); var errors = new List<string>();
        foreach (TextureSetDraft draft in drafts)
        {
            if (alpha != "auto") draft.Material.Surface = alpha;
            try { results.Add(TextureImporter.Import(folder, draft, args.Get("group") ?? "Textures", conflict)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { errors.Add(draft.Name + ": " + ex.Message); }
        }
        if (args.Has("json")) Console.WriteLine(JsonSerializer.Serialize(new { imported = results, errors }, AuthoredMaterial.JsonOptions));
        else { foreach (var r in results) Console.WriteLine($"{(r.Skipped ? "Skipped" : "Imported")} {r.Path} ({r.Width} × {r.Height})"); foreach (string error in errors) Console.Error.WriteLine(error); }
        return Task.FromResult(errors.Count == 0 ? 0 : 1);
    }

    private static Task<int> Material(CliArgs args)
    {
        string operation = args.Positional(0) ?? "show";
        string folder = args.Positional(1) ?? throw new ArgumentException("Give the project folder.");
        string path = args.Positional(2) ?? throw new ArgumentException("Give MyLevel.Group.Name.");
        JsonObject manifest = ProjectAssets.For(folder).Edit();
        JsonObject materials = (manifest["materials"] ??= new JsonObject()).AsObject();
        if (operation == "show")
        {
            Console.WriteLine(materials[path]?.ToJsonString(AuthoredMaterial.JsonOptions) ?? throw new ArgumentException("No map-local material " + path));
            return Task.FromResult(0);
        }
        if (operation is not ("create" or "update")) throw new ArgumentException("Choose show, create or update.");
        AuthoredMaterial definition = args.Get("definition") is { } file ? AuthoredMaterial.From(JsonNode.Parse(File.ReadAllBytes(file))) ?? throw new InvalidDataException("The definition is not an object.") : new();
        string[] parts = path.Split('.');
        if (parts.Length < 3 || !parts[0].Equals("MyLevel", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use MyLevel.Group.Name.");
        if (operation == "update" && ProjectAssets.For(folder).FindMaterial(path) is null) throw new ArgumentException("No map-local material " + path);
        var draft = new TextureSetDraft { Name = parts[^1], Material = definition };
        TextureImportResult result = TextureImporter.Import(folder, draft, string.Join('.', parts.Skip(1).SkipLast(1)), operation == "update" ? TextureConflict.Replace : TextureConflict.Rename);
        Console.WriteLine(JsonSerializer.Serialize(result, AuthoredMaterial.JsonOptions));
        return Task.FromResult(0);
    }
}
