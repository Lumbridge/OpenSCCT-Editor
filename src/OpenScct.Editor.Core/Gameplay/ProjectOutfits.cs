using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A problem with the project's outfits, for Map Check and the build.</summary>
public sealed record OutfitProblem(bool Error, string Code, string Message);

/// <summary>
/// The project's outfits (<see cref="MapProject.Outfits"/>, SVMap 1.2 <c>render/outfits.json</c>): where their files
/// are, bringing files into the project, the model check and what the build writes.
/// </summary>
public static class ProjectOutfits
{
    /// <summary>The team's stock skeletal mesh, whose clips an outfit model plays.</summary>
    public static string StockAsset(bool spy) => spy ? "SPerso.ATT_01" : "SPerso.DEF_01";

    public static string TeamName(bool spy) => spy ? "spy" : "merc";

    /// <summary>A shared skeletal mesh's glb (<c>shared/chars/&lt;Package&gt;/&lt;Mesh&gt;.glb</c>), or null when missing.</summary>
    public static string? SharedGlb(string asset, string? contentRoot)
    {
        int dot = asset.IndexOf('.', StringComparison.Ordinal);
        if (contentRoot is null || dot <= 0)
        {
            return null;
        }

        string path = Path.Combine(contentRoot, "shared", "chars", asset[..dot], asset[(dot + 1)..] + ".glb");
        return File.Exists(path) ? path : null;
    }

    /// <summary>A path the outfits name: relative to the project folder, or absolute (a map imported from SVMap
    /// points at its source map's files). Null when it cannot be resolved (a relative path in an unsaved map).</summary>
    public static string? Resolve(string path, string? projectFolder) =>
        Path.IsPathRooted(path) ? path : projectFolder is null ? null : Path.GetFullPath(Path.Combine(projectFolder, path));

    /// <summary>
    /// The path to store for <paramref name="source"/>: a file already in the project stays relative, anything else
    /// is to be copied under <c>assets/outfits/</c> (returned with the copies to make). A different file already there
    /// under the same name is never replaced: the copy takes the next free name (<c>body-2.png</c>, a model folder
    /// <c>Hazmat-2/</c>), so two teams' <c>body.png</c> stay two files and an undo still finds the first. Without a
    /// project folder the absolute path is kept (<see cref="Localize"/> brings it in at the first save).
    /// <paramref name="pending"/> are copies already planned and not made yet (an edit adopting several files).
    /// </summary>
    public static (string Stored, List<(string From, string To)> Copies) Adopt(string source, string? projectFolder,
        IReadOnlyCollection<(string From, string To)>? pending = null)
    {
        var copies = new List<(string, string)>();
        string full = Path.GetFullPath(source);
        if (projectFolder is null)
        {
            return (full, copies);
        }

        string root = Path.GetFullPath(projectFolder);
        string relative = Path.GetRelativePath(root, full);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            return (relative.Replace('\\', '/'), copies);
        }

        string name = Path.GetFileNameWithoutExtension(full);
        string extension = Path.GetExtension(full);
        bool model = string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase);
        string folder, stored;
        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? "" : $"-{n}";
            // A model keeps its own folder, so the textures it names beside it come along at the same relative paths.
            folder = model ? $"{ProjectStore.OutfitsFolder}/{name}{suffix}" : ProjectStore.OutfitsFolder;
            stored = model ? $"{folder}/{Path.GetFileName(full)}" : $"{folder}/{name}{suffix}{extension}";
            string target = Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar));
            bool planned = pending?.Any(c => string.Equals(c.To, stored, StringComparison.OrdinalIgnoreCase) && !SameBytes(c.From, full)) ?? false;
            if (!planned && (!File.Exists(target) || SameBytes(full, target)))
            {
                break;
            }
        }

        copies.Add((full, stored));
        if (model)
        {
            string from = Path.GetDirectoryName(full)!;
            foreach (string uri in GlbFiles(full))
            {
                copies.Add((Path.Combine(from, uri), $"{folder}/{uri}"));
            }
        }

        return (stored, copies);
    }

    /// <summary>
    /// The outfits with every file outside <paramref name="projectFolder"/> copied into its <c>assets/outfits/</c>
    /// (<see cref="Adopt"/>) and named by its project path. A file that is missing keeps its path; Map Check reports it.
    /// </summary>
    public static MapOutfits Localize(MapOutfits outfits, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(outfits);
        string Own(string path)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path))
            {
                return path;
            }

            (string stored, List<(string From, string To)> copies) = Adopt(path, projectFolder);
            Copy(projectFolder, copies);
            return stored;
        }

        TeamOutfit? Team(TeamOutfit? t) => t is null ? null : t with
        {
            Body = t.Body is { } b ? b with { Texture = Own(b.Texture) } : null,
            Head = t.Head is { } h ? h with { Texture = Own(h.Texture) } : null,
            Model = t.Model is { Glb: { } glb } m ? m with { Glb = Own(glb) } : t.Model,
        };
        return outfits.IsEmpty ? outfits : new MapOutfits(Team(outfits.Spy), Team(outfits.Merc));
    }

    /// <summary>Copies files into the project folder (<see cref="Adopt"/> names them so that only the same file is replaced).</summary>
    public static void Copy(string projectFolder, IEnumerable<(string From, string To)> copies)
    {
        foreach ((string from, string to) in copies)
        {
            string target = Path.Combine(projectFolder, to.Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(Path.GetFullPath(from), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(from, target, overwrite: true);
            }
        }
    }

    /// <summary>The files a glb names beside itself: its images and the texture paths in its material extras.</summary>
    public static List<string> GlbFiles(string glb)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        if (GlbJson.Read(glb) is not SvJsonObject root)
        {
            return [];
        }

        foreach (SvJsonNode image in root["images"] as SvJsonArray ?? [])
        {
            if (image is SvJsonObject i && i["uri"] is SvJsonString { Value: { Length: > 0 } uri } && !uri.StartsWith("data:", StringComparison.Ordinal))
            {
                files.Add(uri);
            }
        }

        foreach (SvJsonNode material in root["materials"] as SvJsonArray ?? [])
        {
            if (material is SvJsonObject m && m["extras"] is SvJsonObject extras)
            {
                foreach (KeyValuePair<string, SvJsonNode> kv in extras)
                {
                    if (kv.Value is SvJsonString { Value: { } path } && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }
            }
        }

        return [.. files];
    }

    /// <summary>The bone names a glb's skin binds (its joints).</summary>
    public static HashSet<string> Bones(string glb)
    {
        var bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (GlbJson.Read(glb) is SvJsonObject root && root["nodes"] is SvJsonArray nodes && root["skins"] is SvJsonArray { Count: > 0 } skins &&
            skins[0] is SvJsonObject skin && skin["joints"] is SvJsonArray joints)
        {
            foreach (SvJsonNode j in joints)
            {
                if (j is SvJsonNumber n && n.TryGetInt32(out int index) && index >= 0 && index < nodes.Count &&
                    nodes[index] is SvJsonObject node && node["name"] is SvJsonString name)
                {
                    bones.Add(name.Value);
                }
            }
        }

        return bones;
    }

    /// <summary>The bones a glb's clips drive: the nodes its animation channels target.</summary>
    public static SortedSet<string> DrivenBones(string glb)
    {
        var bones = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (GlbJson.Read(glb) is not SvJsonObject root || root["nodes"] is not SvJsonArray nodes)
        {
            return bones;
        }

        foreach (SvJsonNode clip in root["animations"] as SvJsonArray ?? [])
        {
            foreach (SvJsonNode channel in (clip as SvJsonObject)?["channels"] as SvJsonArray ?? [])
            {
                if (channel is SvJsonObject c && c["target"] is SvJsonObject t && t["node"] is SvJsonNumber n && n.TryGetInt32(out int index) &&
                    index >= 0 && index < nodes.Count && nodes[index] is SvJsonObject node && node["name"] is SvJsonString name)
                {
                    bones.Add(name.Value);
                }
            }
        }

        return bones;
    }

    /// <summary>
    /// The bones the team's clips drive that the model lacks (the model check: an outfit model plays the team's own
    /// clips by bone name, so it needs every bone they drive; other bones and their order are free).
    /// </summary>
    public static List<string> MissingBones(string modelGlb, string stockGlb)
    {
        HashSet<string> have = Bones(modelGlb);
        return [.. DrivenBones(stockGlb).Where(b => !have.Contains(b))];
    }

    /// <summary>What is wrong with the outfits: files that are missing, models that lack bones, stock meshes not found.</summary>
    public static List<OutfitProblem> Problems(MapOutfits outfits, string? projectFolder, string? contentRoot)
    {
        ArgumentNullException.ThrowIfNull(outfits);
        var problems = new List<OutfitProblem>();
        foreach (bool spy in new[] { true, false })
        {
            if (outfits.For(spy) is not { IsEmpty: false } team)
            {
                continue;
            }

            string name = TeamName(spy);
            foreach ((string slot, OutfitSlot? s) in new[] { ("body", team.Body), ("head", team.Head) })
            {
                // A project path in a map not saved yet cannot be looked up; it is checked once the map has a folder.
                if (s is not null && Resolve(s.Texture, projectFolder) is not null && !FileOk(s.Texture, projectFolder, ".png"))
                {
                    problems.Add(new OutfitProblem(true, "outfit.file", $"the {name} {slot} texture {s.Texture} is not a PNG file the map can find"));
                }
            }

            if (team.Model is not { } model)
            {
                continue;
            }

            if (model.Glb is not null && (team.Body is not null || team.Head is not null))
            {
                problems.Add(new OutfitProblem(false, "outfit.slots",
                    $"the {name} outfit has its own model: its body and head textures apply only where the model draws the stock materials, which a model of its own normally does not"));
            }

            if (team.Cosmetics == OutfitCosmetics.Player)
            {
                problems.Add(new OutfitProblem(false, "outfit.cosmetics", $"the {name} outfit has a model: players' own cosmetics only apply to texture outfits, so the model is worn"));
            }

            if (model.Glb is { } path && Resolve(path, projectFolder) is null)
            {
                continue;
            }

            string? stock = SharedGlb(StockAsset(spy), contentRoot);
            string? glb = model.Glb is { } own ? (FileOk(own, projectFolder, ".glb") ? Resolve(own, projectFolder) : null) : SharedGlb(model.Stock ?? "", contentRoot);
            if (glb is null)
            {
                problems.Add(new OutfitProblem(true, "outfit.file",
                    model.Glb is { } g ? $"the {name} model {g} is not a .glb file the map can find" : $"the {name} model {model.Stock} is not a shared skeletal mesh"));
                continue;
            }

            if (stock is null)
            {
                problems.Add(new OutfitProblem(false, "outfit.skeleton", $"the {name} model's bones are not checked: {StockAsset(spy)} is not in the shared content"));
                continue;
            }

            List<string> missing = MissingBones(glb, stock);
            if (missing.Count > 0)
            {
                problems.Add(new OutfitProblem(true, "outfit.skeleton",
                    $"the {name} model lacks {missing.Count} bone{(missing.Count == 1 ? "" : "s")} the {name} animations drive: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")}"));
            }
        }

        return problems;
    }

    /// <summary>
    /// The files an SVMap folder's own outfits bring (map paths: <c>render/outfits.json</c>'s slot textures and model
    /// glbs, and the files beside a glb that no other mesh of its folder names), for a build that replaces them.
    /// None when the map has no outfits or they cannot be read.
    /// </summary>
    public static List<string> SourceFiles(string mapRoot)
    {
        MapOutfits? outfits;
        try
        {
            outfits = OutfitsDoc.Load(mapRoot);
        }
        catch (SvMapFormatException)
        {
            return [];
        }

        var files = new List<string>();
        string render = Path.Combine(mapRoot, "render");
        foreach (TeamOutfit team in new[] { outfits?.Spy, outfits?.Merc }.OfType<TeamOutfit>())
        {
            files.AddRange(new[] { team.Body, team.Head }.OfType<OutfitSlot>().Select(s => "render/" + s.Texture));
            if (team.Model is not { Glb: { } glb })
            {
                continue;
            }

            files.Add("render/" + glb);
            string full = Path.Combine(render, glb);
            string folder = Path.GetDirectoryName(full)!;
            // A texture another mesh of the folder also names (a map prop's) stays.
            HashSet<string> shared = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.glb").Where(g => !string.Equals(Path.GetFullPath(g), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase))
                    .SelectMany(GlbFiles).ToHashSet(StringComparer.Ordinal)
                : [];
            // Map paths use '/', and the converter puts a model in render/chars/<Map>/.
            string glbFolder = glb.Contains('/') ? glb[..glb.LastIndexOf('/')] : "";
            files.AddRange(GlbFiles(full).Where(f => !shared.Contains(f)).Select(f => glbFolder.Length > 0 ? $"render/{glbFolder}/{f}" : $"render/{f}"));
        }

        return files;
    }

    /// <summary>
    /// The files of <c>render/</c> the outfits need, and <c>render/outfits.json</c> with their map paths: slot textures
    /// under <c>textures/</c>, models with the files beside them under <c>chars/&lt;Map&gt;/&lt;team&gt;/</c>. Missing
    /// files are left out (Map Check reports them).
    /// </summary>
    public static SortedDictionary<string, byte[]> Build(MapOutfits outfits, string mapId, string? projectFolder)
    {
        ArgumentNullException.ThrowIfNull(outfits);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        TeamOutfit? Team(TeamOutfit? team, string name)
        {
            if (team is not { IsEmpty: false })
            {
                return null;
            }

            OutfitSlot? Slot(OutfitSlot? slot, string which)
            {
                if (slot is null || Resolve(slot.Texture, projectFolder) is not { } path || !File.Exists(path))
                {
                    return null;
                }

                string rel = $"textures/outfit_{name}_{which}_{Path.GetFileName(path)}";
                files[$"render/{rel}"] = File.ReadAllBytes(path);
                return slot with { Texture = rel };
            }

            OutfitModel? model = team.Model;
            if (team.Model is { Glb: { } own } authored)
            {
                model = null;
                if (Resolve(own, projectFolder) is { } glb && File.Exists(glb))
                {
                    // Per team, so two models of the same file name (and the files beside them) stay apart.
                    string folder = $"chars/{mapId}/{name}";
                    files[$"render/{folder}/{Path.GetFileName(glb)}"] = File.ReadAllBytes(glb);
                    foreach (string uri in GlbFiles(glb))
                    {
                        string beside = Path.Combine(Path.GetDirectoryName(glb)!, uri);
                        if (File.Exists(beside))
                        {
                            files[$"render/{folder}/{uri}"] = File.ReadAllBytes(beside);
                        }
                    }

                    model = authored with { Glb = $"{folder}/{Path.GetFileName(glb)}" };
                }
            }

            return new TeamOutfit(Slot(team.Body, "body"), Slot(team.Head, "head"), model, team.Cosmetics) is { IsEmpty: false } built ? built : null;
        }

        var map = new MapOutfits(Team(outfits.Spy, "spy"), Team(outfits.Merc, "merc"));
        if (!map.IsEmpty)
        {
            files[OutfitsDoc.RelativePath] = CanonicalJson.ToFileBytes(OutfitsDoc.ToJson(map));
        }

        return files;
    }

    private static bool SameBytes(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static bool FileOk(string path, string? projectFolder, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) && Resolve(path, projectFolder) is { } full && File.Exists(full);
}
