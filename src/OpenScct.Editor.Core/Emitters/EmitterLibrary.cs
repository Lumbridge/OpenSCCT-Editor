using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Launcher.Install;

namespace OpenScct.Editor.Core.Emitters;

public sealed record EmitterPreset(string Id, string Name, string Category, string Description, string Mode,
    IReadOnlyList<Actor> Actors, string Group = "Stock", string? SourceMap = null)
{
    public string? UnavailableReason { get; init; }
}

public sealed record EmitterLibraryResult(IReadOnlyList<EmitterPreset> Presets, IReadOnlyList<string> Warnings);

/// <summary>The stock catalogue, read-only RE+ effect packs and the editor's user library.</summary>
public static class EmitterLibrary
{
    public const string UserGroup = "Your effects";
    private const int MaxPackBytes = 64 * 1024 * 1024;
    private const int MaxActors = 256;
    private const string PacksFolder = "EmitterPacks";
    private const string CustomFolder = "Custom";

    public static string UserRoot => Path.Combine(UserFolders.Settings, "emitters");

    public static EmitterLibraryResult Load(string? contentRoot, string? userRoot = null)
    {
        userRoot ??= UserRoot;
        UeSchema? schema = UeSchema.Load(contentRoot);
        var presets = StockEmitters.Load(contentRoot, schema).ToList();
        var warnings = new List<string>();
        var seenPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? game = FindOriginalGame();
        IEnumerable<string> packFiles = Files(Path.Combine(userRoot, PacksFolder), warnings);
        if (game is not null)
        {
            packFiles = packFiles.Concat(Files(Path.Combine(game, "System", "ReloadedEditor", PacksFolder), warnings));
        }

        var packs = new List<Pack>();
        foreach (string path in packFiles)
        {
            try
            {
                Pack pack = ReadPack(ReadFile(path), path, schema, game, warnings);
                if (seenPacks.Add(pack.Id))
                {
                    packs.Add(pack);
                }
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        foreach (Pack pack in packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            presets.AddRange(pack.Presets);
        }

        foreach (string path in Files(Path.Combine(userRoot, CustomFolder), warnings))
        {
            try
            {
                EmitterPreset saved = ReadUser(path, schema);
                presets.Add(saved);
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new EmitterLibraryResult(presets, warnings);
    }

    /// <summary>Saves an existing user preset or writes a new copy with its own identifier.</summary>
    public static EmitterPreset Save(EmitterPreset preset, string? userRoot = null)
    {
        ValidateActors(preset.Actors);
        string id = preset.Group == UserGroup && Guid.TryParseExact(preset.Id, "N", out Guid saved)
            ? saved.ToString("N") : Guid.NewGuid().ToString("N");
        string name = Clean(preset.Name, 120, "name");
        string category = Clean(string.IsNullOrWhiteSpace(preset.Category) ? "Other" : preset.Category, 48, "category");
        string description = Clean(preset.Description, 2000, "description", optional: true);
        var data = new SvJsonObject();
        data.Add("version", SvJsonNode.Integer(1));
        data.Add("id", SvJsonNode.String(id));
        data.Add("name", SvJsonNode.String(name));
        data.Add("category", SvJsonNode.String(category));
        data.Add("description", SvJsonNode.String(description));
        data.Add("mode", SvJsonNode.String(preset.Mode));
        if (preset.SourceMap is { } map)
        {
            data.Add("sourceMap", SvJsonNode.String(map));
        }

        data.Add("actors", new SvJsonArray(preset.Actors.Select(a => ModelJson.ActorToJson(a, includeEditor: false))));
        AtomicFile.WriteAllBytes(Path.Combine(userRoot ?? UserRoot, CustomFolder, id + ".json"), CanonicalJson.ToFileBytes(data));
        return preset with { Id = id, Name = name, Category = category, Description = description, Group = UserGroup, UnavailableReason = null };
    }

    /// <summary>Imports a version 1 RE+ JSON effect pack into the read-only library. Related game packages are installed separately.</summary>
    public static string ImportPack(string file, string? contentRoot, string? userRoot = null)
    {
        byte[] bytes = ReadFile(file);
        var warnings = new List<string>();
        Pack pack = ReadPack(bytes, file, UeSchema.Load(contentRoot), FindOriginalGame(), warnings);
        if (pack.Presets.Count == 0)
        {
            throw new InvalidDataException(warnings.FirstOrDefault() ?? "The effect pack has no presets.");
        }

        AtomicFile.WriteAllBytes(Path.Combine(userRoot ?? UserRoot, PacksFolder, pack.Id + ".json"), bytes);
        return pack.Name;
    }

    private static EmitterPreset ReadUser(string path, UeSchema? schema)
    {
        SvJsonObject data = ReadDocument(ReadFile(path), path);
        string id = Required(data, "id");
        if (!Guid.TryParseExact(id, "N", out Guid guid) || guid.ToString("N") != Path.GetFileNameWithoutExtension(path))
        {
            throw new InvalidDataException("The saved effect's identifier does not match its file.");
        }

        Actor[] actors = Objects(data, "actors").Select(ModelJson.ActorFromJson).ToArray();
        ValidateActors(actors, schema);
        return new EmitterPreset(id, Required(data, "name"), Required(data, "category"), Text(data["description"]) ?? "",
            Required(data, "mode"), actors, UserGroup, Text(data["sourceMap"]));
    }

    private static Pack ReadPack(byte[] bytes, string path, UeSchema? schema, string? game, List<string> warnings)
    {
        SvJsonObject data = ReadDocument(bytes, path);
        var header = data["pack"] as SvJsonObject ?? throw new InvalidDataException("The effect pack header is missing.");
        string id = Required(header, "id");
        if (id.Length > 32 || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'))
        {
            throw new InvalidDataException("The effect pack identifier must use lowercase letters, digits, underscores or hyphens.");
        }

        string name = Clean(Required(header, "name"), 120, "pack name");
        string[] requires = header["requires"] is SvJsonArray list ? list.Select(RequirePackageName).ToArray() : [];
        if (data["emitters"] is not SvJsonArray entries)
        {
            throw new InvalidDataException("The pack must contain an emitters list.");
        }

        string prefix = $"pack.{id}.";
        var presets = new List<EmitterPreset>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SvJsonNode node in entries)
        {
            try
            {
                var entry = node as SvJsonObject ?? throw new InvalidDataException("An effect is not a JSON object.");
                string entryId = Required(entry, "id");
                if (entryId.Length <= prefix.Length || entryId.Length > 64 || !entryId.StartsWith(prefix, StringComparison.Ordinal) ||
                    !entryId[prefix.Length..].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' or '.') ||
                    ids.Contains(entryId))
                {
                    throw new InvalidDataException($"Effect '{entryId}' has an invalid or repeated identifier.");
                }

                string title = Clean(Required(entry, "name"), 120, "name");
                Actor[] actors = ReadPackActors(entry, schema);
                string[] missing = MissingPackages(entry, requires, game);
                presets.Add(new EmitterPreset(entryId, title, Text(entry["category"]) ?? "Other", Text(entry["description"]) ?? "",
                    ModeOf(actors), actors, name)
                {
                    UnavailableReason = missing.Length == 0 ? null : $"Install the required RE+ packages: {string.Join(", ", missing)}.",
                });
                ids.Add(entryId);
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new Pack(id, name, presets);
    }

    private static Actor[] ReadPackActors(SvJsonObject entry, UeSchema? schema)
    {
        SvJsonObject[] records = Objects(entry, "actors");
        if (records.Length is < 1 or > MaxActors)
        {
            throw new InvalidDataException($"An effect needs 1 to {MaxActors} actors.");
        }

        var doc = new T3dDocument();
        foreach (SvJsonObject record in records)
        {
            string name = Required(record, "name");
            T3dDocument parsed = T3dReader.Read(Required(record, "text"));
            if (parsed.Actors.Count != 1 || !name.Equals(parsed.Actors[0].Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Effect actor '{name}' does not match its T3D.");
            }

            doc.Actors.Add(parsed.Actors[0]);
        }

        Actor[] actors = T3dImport.ToProject(doc, "Assembly", schema, [], hasBuilder: false).Actors.ToArray();
        if (actors.Length != records.Length)
        {
            throw new InvalidDataException("An effect contains a map-only actor.");
        }

        for (int i = 0; i < actors.Length; i++)
        {
            Actor actor = actors[i];
            SvJsonObject record = records[i];
            if (record["position"] is not SvJsonArray { Count: 3 } position || position.Any(p => p is not SvJsonNumber))
            {
                throw new InvalidDataException($"Effect actor '{actor.Id}' has an invalid position.");
            }

            if (record["rotation"] is not SvJsonArray { Count: 3 } rotation || rotation.Any(p => p is not SvJsonNumber))
            {
                throw new InvalidDataException($"Effect actor '{actor.Id}' has an invalid rotation.");
            }

            actor.Location = ModelJson.ReadVec(position);
            actor.Rotation = ModelJson.ReadRot(rotation);
            actor.Editor.Folder = SvMapImport.EmittersFolder;
        }

        ValidateActors(actors, schema);
        return actors;
    }

    internal static string ModeOf(IEnumerable<Actor> actors)
    {
        bool periodic = false;
        foreach (Actor actor in actors)
        {
            periodic |= actor.Props.GetBool("AutoReset");
            if (EmitterBuild.SystemsOf(actor) is not { } parts)
            {
                continue;
            }

            foreach (SvJsonObject part in parts.OfType<SvJsonObject>())
            {
                if (part["props"] is SvJsonObject props)
                {
                    if (props["Disabled"] is SvJsonLiteral { Kind: SvJsonKind.True })
                    {
                        return "triggered";
                    }

                    periodic |= props["AutoReset"] is SvJsonLiteral { Kind: SvJsonKind.True };
                }
            }
        }

        return periodic ? "periodic" : "continuous";
    }

    private static string[] MissingPackages(SvJsonObject entry, string[] requires, string? game)
    {
        if (requires.Length == 0 || entry["dependencies"] is not SvJsonArray dependencies)
        {
            return [];
        }

        var packages = dependencies.OfType<SvJsonString>().Select(x => x.Value.Split('.')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requires.Where(name => packages.Contains(Path.GetFileNameWithoutExtension(name)) &&
            (game is null || !File.Exists(Path.Combine(game, "Packages", PackageFolder(name)!, name)))).ToArray();
    }

    private static string RequirePackageName(SvJsonNode node)
    {
        if (Text(node) is not { } name || name != Path.GetFileName(name) || PackageFolder(name) is null ||
            Path.GetFileNameWithoutExtension(name) is not { Length: > 0 and <= 64 } stem ||
            !stem.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new InvalidDataException("The effect pack contains an invalid required package name.");
        }

        return name;
    }

    private static string? PackageFolder(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".utx" => "Textures",
        ".usx" => "StaticMeshes",
        ".uax" => "Sounds",
        ".ukx" => "Animations",
        _ => null,
    };

    private static string? FindOriginalGame()
    {
        try
        {
            return SdcImporter.GameRoot(null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void ValidateActors(IReadOnlyList<Actor> actors, UeSchema? schema = null)
    {
        if (actors.Count is < 1 or > MaxActors ||
            !actors.Any(a => a.ShortClass.Equals("Emitter", StringComparison.OrdinalIgnoreCase) ||
                schema?.IsA(a.Class, "Engine.Emitter") == true || EmitterBuild.SystemsOf(a) is { Count: > 0 }))
        {
            throw new InvalidDataException("An effect needs at least one emitter and at most 256 actors.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (actors.Any(a => !InteropValues.IsValidName(a.Id) || !ids.Add(a.Id) ||
            !double.IsFinite(a.Location.X) || !double.IsFinite(a.Location.Y) || !double.IsFinite(a.Location.Z)))
        {
            throw new InvalidDataException("Effect actors need distinct valid names and finite positions.");
        }
    }

    private static string[] Files(string folder, List<string> warnings)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            warnings.Add($"{folder}: {ex.Message}");
            return [];
        }
    }

    private static byte[] ReadFile(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > MaxPackBytes)
        {
            throw new InvalidDataException("An effect file must be 64 MB or smaller.");
        }

        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static SvJsonObject ReadDocument(byte[] bytes, string path) =>
        SvJson.Parse(bytes, path) is SvJsonObject data && data["version"] is SvJsonNumber v && PropValues.Num(v) == 1
            ? data : throw new InvalidDataException("The effect file must use version 1.");

    private static SvJsonObject[] Objects(SvJsonObject data, string key) =>
        data[key] is SvJsonArray a && a.All(n => n is SvJsonObject)
            ? a.Cast<SvJsonObject>().ToArray() : throw new InvalidDataException($"The effect file has an invalid {key} list.");

    private static bool IsReadError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or SvMapFormatException or ArgumentException;

    private static string Clean(string value, int max, string field, bool optional = false)
    {
        string trimmed = value.Trim();
        if ((!optional && trimmed.Length == 0) || trimmed.Length > max || trimmed.Any(c => char.IsControl(c) && (!optional || c is not ('\r' or '\n' or '\t'))))
        {
            throw new InvalidDataException($"Invalid effect {field}. Use up to {max} characters.");
        }

        return trimmed;
    }

    private static string? Text(SvJsonNode? node) => (node as SvJsonString)?.Value;

    internal static string Required(SvJsonObject data, string key) =>
        Text(data[key]) is { Length: > 0 } value ? value : throw new InvalidDataException($"The effect file is missing {key}.");

    private sealed record Pack(string Id, string Name, List<EmitterPreset> Presets);
}
