using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Content.Maps;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Svmap;

/// <summary>
/// Keeps a map's identity and pictures on import, so a copy of a stock or community map still shows its own name and
/// pictures: a converted map's <c>ui/identity.json</c> first, then the shared catalog's name and pictures for the map
/// code (with a downloaded map's <c>catalog/entry.json</c>), and the dossier and place names from the <c>mapinfo</c>
/// legends. Pictures are named by their full path until the project is first saved, which copies them into its
/// <c>pictures/</c> folder (<see cref="ProjectPictures.Adopt"/>).
/// </summary>
public static class IdentityImport
{
    /// <summary>The <see cref="MapProject.Extra"/> key of the shared catalog's name an import gave the map in place of
    /// the title it came with (<c>{"name", "imported"}</c>, <see cref="FillMissing"/>).</summary>
    public const string CatalogTitleKey = "catalogTitle";

    /// <summary>The <see cref="MapProject.Extra"/> key of the hashes of the pictures the map came with (its own, or the
    /// shared catalog's for a stock map code), so Map Check and the build do not ask an original map for bigger or
    /// reshaped pictures than it shipped with (<see cref="CameWithMap"/>).</summary>
    public const string OriginalPicturesKey = "originalPictures";

    private static readonly Dictionary<(string Path, long Length, DateTime Written), string> s_pictureHashes = new();

    /// <summary>The shared catalog's picture kinds, as <see cref="MapCatalogData.Image"/> names them.</summary>
    private const string Menu = "menu", LoadingSpy = "loading-spy", LoadingMerc = "loading-merc", Briefing = "briefing";

    /// <summary>The identity a converted map folder carries (<c>ui/identity.json</c>, with its translations in
    /// <c>ui/text/</c>); <paramref name="name"/> is its own name, if it has one.</summary>
    public static ProjectIdentity Read(string mapFolder, out string? name)
    {
        MapIdentity own = MapIdentity.Load(mapFolder);
        name = own.Name;
        MapPictures pictures = own.Pictures;
        return new ProjectIdentity
        {
            Family = own.Family,
            Variant = own.Variant,
            Description = own.Description,
            Dossier = own.Dossier is { } d ? new Dossier { When = d.When, Where = d.Where, Summary = d.Summary } : new Dossier(),
            Authors = own.Authors.ToList(),
            Version = own.Version,
            Language = own.Language,
            Texts = Texts(mapFolder, own.Language),
            RecommendedPlayers = own.RecommendedPlayers is { } players ? Math.Min(players, ProjectIdentity.MaxPlayers) : null,
            Tags = own.Tags.ToList(),
            Places = own.Places.Select(p => new Place { Number = p.Number, Name = p.Name, At = new Vec3(p.At.X, p.At.Y, p.At.Z), Floor = p.Floor }).ToList(),
            Pictures = new IdentityPictures
            {
                Menu = File(pictures.Menu),
                LoadingSpy = pictures.LoadingSpy.Select(File).OfType<PictureRecipe>().ToList(),
                LoadingMerc = pictures.LoadingMerc.Select(File).OfType<PictureRecipe>().ToList(),
                // A plan drawn through the snapshot camera is drawn again from the map; any other briefing picture is kept.
                Briefing = pictures.BriefingThroughSnapshot && pictures.Generated.Contains(Briefing) ? PictureRecipe.OfPlan() : File(pictures.Briefing),
                Banner = File(pictures.Banner),
            },
        };
    }

    /// <summary>The translations in a map folder's <c>ui/text/</c>, without the map's own language.</summary>
    private static SortedDictionary<string, IdentityText> Texts(string mapFolder, string language)
    {
        var texts = new SortedDictionary<string, IdentityText>(StringComparer.Ordinal);
        string folder = Path.Combine(mapFolder, "ui", "text");
        if (!Directory.Exists(folder))
        {
            return texts;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            string lang = Path.GetFileNameWithoutExtension(file);
            MapText text = MapText.Load(mapFolder, lang);
            var imported = new IdentityText
            {
                Name = text.Name ?? "",
                Description = text.Description ?? "",
                Dossier = new Dossier { When = text.When ?? "", Where = text.Where ?? "", Summary = text.Summary ?? "" },
                Places = new SortedDictionary<int, string>(text.Places.ToDictionary(p => p.Key, p => p.Value)),
            };
            if (lang != language && !imported.IsEmpty)
            {
                texts[lang] = imported;
            }
        }

        return texts;
    }

    /// <summary>
    /// Fills what an imported project's identity lacks: the name the menus give its map code (mixed case, split into
    /// family and variant word) when the map has no name of its own, the dossier and place names from its stored
    /// legends, and every empty picture slot from the shared catalog's pictures of its map code
    /// (<paramref name="mapFolder"/>, when the import came from a map folder, adds that map's own catalog entry). Run
    /// after every import.
    /// </summary>
    public static void FillMissing(MapProject project, string? contentRoot, string? mapFolder = null)
    {
        ProjectIdentity id = project.Identity;
        MapCatalogData catalog = MapCatalogData.Load(contentRoot is null ? null : Path.Combine(contentRoot, "shared", "maps"));
        if (mapFolder is not null)
        {
            catalog = catalog.WithMapEntries([mapFolder]);
        }

        bool ownName = mapFolder is not null && MapIdentity.Load(mapFolder).Name is not null;
        if (!ownName && catalog.Names.TryGetValue(project.Id, out string? shared) && shared.Trim() is { Length: > 0 and <= ProjectIdentity.MaxName } name)
        {
            // A stock map's stored title is its capitalised menu name: the shared catalog has the name menus show. The
            // name is noted, so the T3D export gives the original editor the map's own title back until it is renamed.
            if (name != project.Title)
            {
                var named = new SvJsonObject();
                named.Add("name", SvJsonNode.String(name));
                named.Add("imported", SvJsonNode.String(project.Title));
                project.Extra.TryAdd(CatalogTitleKey, named);
            }

            project.Title = name;
            (id.Family, id.Variant) = SplitVariant(name, catalog.VariantWords);
        }

        if (id.Dossier.IsEmpty && SideFiles.DossierOf(project.MapInfo.GetString(SideFiles.LegendMap)) is { } dossier)
        {
            id.Dossier = dossier;
        }

        if (id.Places.Count == 0)
        {
            id.Places = SideFiles.PlacesOf(project.MapInfo.GetString(SideFiles.LegendMapInBrief));
        }

        // A map imported without a strategic map keeps none (the build would otherwise draw one from its floors); the
        // Identity panel's floors set to auto gives it one.
        if (id.Floors is null && project.LevelInfo["MapFloors"] is not SvJsonArray { Count: > 0 })
        {
            id.Floors = [];
        }

        IdentityPictures pictures = id.Pictures;
        pictures.Menu ??= File(catalog.Image(project.Id, Menu));
        pictures.Briefing ??= File(catalog.Image(project.Id, Briefing));
        if (pictures.LoadingSpy.Count == 0 && File(catalog.Image(project.Id, LoadingSpy)) is { } spy)
        {
            pictures.LoadingSpy.Add(spy);
        }

        if (pictures.LoadingMerc.Count == 0 && File(catalog.Image(project.Id, LoadingMerc)) is { } merc)
        {
            pictures.LoadingMerc.Add(merc);
        }

        var originals = new SvJsonArray();
        foreach (string hash in pictures.Slots().Select(s => s.Recipe.File).OfType<string>().Where(System.IO.File.Exists).Select(PictureHash).Distinct())
        {
            originals.Add(SvJsonNode.String(hash));
        }

        if (originals.Count > 0)
        {
            project.Extra.TryAdd(OriginalPicturesKey, originals);
        }
    }

    /// <summary>True when the picture file at <paramref name="path"/> is one the map came with at import (by content, so
    /// a copy into the project's pictures folder still counts and a replaced file does not).</summary>
    public static bool CameWithMap(MapProject project, string path) =>
        project.Extra[OriginalPicturesKey] is SvJsonArray { Count: > 0 } hashes && System.IO.File.Exists(path)
        && hashes.OfType<SvJsonString>().Any(h => h.Value == PictureHash(path));

    /// <summary>A short content hash of a picture file, cached by path, size and write time.</summary>
    private static string PictureHash(string path)
    {
        var info = new FileInfo(path);
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        lock (s_pictureHashes)
        {
            if (s_pictureHashes.TryGetValue(key, out string? known))
            {
                return known;
            }
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(path)).AsSpan(0, 8));
        lock (s_pictureHashes)
        {
            s_pictureHashes[key] = hash;
        }

        return hash;
    }

    /// <summary>The family and variant word of a name, as the game's map catalog splits it ("Enhanced Aquarius 3v3":
    /// Aquarius 3v3, Enhanced); no variant word gives (null, null), the whole name.</summary>
    private static (string? Family, string? Variant) SplitVariant(string name, IReadOnlyList<string> variantWords)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (variantWords.FirstOrDefault(v => string.Equals(v, words[i], StringComparison.OrdinalIgnoreCase)) is not null && words.Length > 1)
            {
                return (string.Join(' ', words.Where((_, k) => k != i)), words[i]);
            }
        }

        return (null, null);
    }

    /// <summary>The title the map came with while it still has the shared catalog's name an import gave it, else its
    /// title.</summary>
    public static string ImportedTitle(MapProject project) =>
        project.Extra[CatalogTitleKey] is SvJsonObject named && named["name"] is SvJsonString name && name.Value == project.Title
            && named["imported"] is SvJsonString imported ? imported.Value : project.Title;

    private static PictureRecipe? File(string? path) => path is null ? null : PictureRecipe.OfFile(Path.GetFullPath(path));
}
