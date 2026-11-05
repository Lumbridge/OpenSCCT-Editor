using System.Collections.Concurrent;
using System.Text;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// Every material, static mesh, texture and sound an authored map can use: the project's own assets, the converted
/// maps' render libraries and the shared content. Materials and meshes resolve as the compiler resolves them (the
/// project first, then maps in name order), so the entry a path finds is the one that builds. The content part is
/// indexed per source and cached under <c>%LOCALAPPDATA%/OpenSCCT-Editor/cache/assets</c>, keyed by each map's
/// manifest digest, so a start with an unchanged content folder reads only small cache files.
/// </summary>
public sealed class AssetCatalog
{
    private static readonly ConcurrentDictionary<string, Lazy<AssetCatalog>> s_content = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(AssetKind, string), AssetEntry> _byPath = new(KeyComparer.Instance);
    private readonly Dictionary<(AssetKind, string), AssetEntry> _byShort = new(KeyComparer.Instance);

    /// <summary>The search key of each entry of <see cref="All"/>, by index.</summary>
    private readonly SearchKey[] _keys;

    /// <summary>The materials that show each image file (full path), with the rest of the catalogue (off the main
    /// thread in the app).</summary>
    private readonly Dictionary<string, List<string>> _materialsByImage;

    private AssetCatalog(IReadOnlyList<AssetSource> sources, string description)
    {
        Sources = sources;
        Description = description;
        var order = new List<(AssetKind, string)>();
        foreach (AssetSource s in sources)
        {
            foreach (AssetEntry e in s.Entries)
            {
                if (_byPath.TryGetValue((e.Kind, e.Path), out AssetEntry? first))
                {
                    // A later map ships it too: it keeps the first copy, and counts this map's uses.
                    _byPath[(e.Kind, e.Path)] = Merge(first, e);
                    continue;
                }

                _byPath[(e.Kind, e.Path)] = e;
                order.Add((e.Kind, e.Path));
            }
        }

        // Package.Name keys after merging, in source order (the first source wins, as in the compiler).
        foreach ((AssetKind kind, string path) in order)
        {
            _byShort.TryAdd((kind, AssetPaths.Short(path)), _byPath[(kind, path)]);
        }

        var all = new List<AssetEntry>(_byPath.Values);
        all.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        All = all;
        // Search keys are made once, here (off the main thread in the app), so a search is a scan of short strings.
        _keys = new SearchKey[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            _keys[i] = SearchKey.Of(all[i]);
        }

        _materialsByImage = IndexMaterialsByImage();
    }

    public static AssetCatalog Empty { get; } = new([], "no content");

    /// <summary>Where the cache files live.</summary>
    public static string CacheRoot => Path.Combine(EditorCache.Root, "assets");

    public IReadOnlyList<AssetSource> Sources { get; }

    /// <summary>Every asset, by kind then path.</summary>
    public IReadOnlyList<AssetEntry> All { get; }

    /// <summary>What was indexed, for the browser's status line.</summary>
    public string Description { get; }

    /// <summary>The catalogue of a content folder plus a project's own assets (either may be null).</summary>
    public static AssetCatalog For(string? contentRoot, string? projectFolder)
    {
        AssetCatalog content = contentRoot is null ? Empty : s_content.GetOrAdd(Path.GetFullPath(contentRoot),
            root => new Lazy<AssetCatalog>(() => BuildContent(root))).Value;
        if (projectFolder is null || ProjectAssets.For(projectFolder) is not { } local || local.Manifest.Count <= 1)
        {
            return content;
        }

        AssetSource project = AssetIndexer.IndexProject(local);
        return new AssetCatalog([project, .. content.Sources], $"{project.Entries.Count} map-local, {content.Description}");
    }

    /// <summary>Forgets the cached content catalogue (after the content folder changed under a running editor).</summary>
    public static void Reset() => s_content.Clear();

    public AssetEntry? Find(AssetKind kind, string path) =>
        _byPath.GetValueOrDefault((kind, path)) ?? _byShort.GetValueOrDefault((kind, AssetPaths.Short(path)));

    /// <summary>An asset of any kind by path (materials first, then meshes, textures, sounds).</summary>
    public AssetEntry? Find(string path) => Enum.GetValues<AssetKind>().Select(k => Find(k, path)).FirstOrDefault(e => e is not null);

    /// <summary>
    /// What a brush face names to show <paramref name="e"/>: a material's own path; for a texture, the material that
    /// shows that image (a face names a material, and the compiler, the views and the game resolve nothing else), the
    /// one named like the image when several do. Null for a texture no material shows, and for meshes and sounds.
    /// </summary>
    public string? SurfaceMaterial(AssetEntry e)
    {
        if (e.Kind == AssetKind.Material)
        {
            return e.Path;
        }

        if (e.Kind != AssetKind.Texture || e.File is not { } file || ImageKey(file) is not { } key
            || !_materialsByImage.TryGetValue(key, out List<string>? materials))
        {
            return null;
        }

        // Converted textures are named Package_Name (a group may be in between): the material of that name is the
        // texture itself, as the game names it.
        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
        return materials.FirstOrDefault(m => AssetPaths.Split(m).Name is var name
            && (stem.Equals(name, StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase))) ?? materials[0];
    }

    /// <summary>The materials by the image they show, in source order. Only a material whose path resolves to that
    /// copy counts (the first source that has a path wins, as in the compiler), so a face given it shows the image.</summary>
    private Dictionary<string, List<string>> IndexMaterialsByImage()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (AssetSource s in Sources)
        {
            foreach (AssetEntry e in s.Entries)
            {
                if (e.Kind != AssetKind.Material || e.Texture is not { } texture || ImageKey(texture) is not { } key
                    || Find(AssetKind.Material, e.Path)?.Texture is not { } resolved || ImageKey(resolved) != key)
                {
                    continue;
                }

                if (!index.TryGetValue(key, out List<string>? list))
                {
                    index[key] = list = [];
                }

                if (!list.Contains(e.Path, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(e.Path);
                }
            }
        }

        return index;
    }

    /// <summary>An image file's full path (index paths mix slashes), or null for one that is not a path.</summary>
    private static string? ImageKey(string file)
    {
        try
        {
            return System.IO.Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Assets matching every word of <paramref name="query"/> (ignoring case, words split at white space) in the name,
    /// package, group, origin or English gloss, or naming the kind (<c>mesh</c>, <c>materials</c>...), best first:
    /// whole name matches, then name starts, then names containing the word, then the rest; ties by path.
    /// </summary>
    public IEnumerable<AssetEntry> Search(string? query, AssetKind? kind = null, string? origin = null)
    {
        string[] words = SearchWords(query);
        if (words.Length == 0)
        {
            return All.Where(e => (kind is null || e.Kind == kind) && (origin is null || e.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase)));
        }

        var found = new List<(AssetEntry Entry, int Score)>();
        for (int i = 0; i < _keys.Length; i++)
        {
            AssetEntry e = All[i];
            if ((kind is null || e.Kind == kind) && (origin is null || e.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase)) &&
                _keys[i].Score(e.Kind, words) is > 0 and var score)
            {
                found.Add((e, score));
            }
        }

        // A stable sort on score keeps the catalogue's kind-then-path order among equals.
        return found.OrderByDescending(x => x.Score).Select(x => x.Entry);
    }

    /// <summary>A query's words in lower case (what <see cref="Search"/> matches).</summary>
    public static string[] SearchWords(string? query) =>
        (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>An entry's search text in lower case (path, origin and English gloss, one per line, so a word never
    /// spans two of them) and where its name is in it.</summary>
    private readonly record struct SearchKey(string Text, int NameStart, int NameLength)
    {
        public static SearchKey Of(AssetEntry e)
        {
            string path = e.Path.ToLowerInvariant();
            int name = path.LastIndexOf('.') + 1;
            return new SearchKey($"{path}\n{e.Origin.ToLowerInvariant()}\n{e.Gloss?.ToLowerInvariant()}", name, path.Length - name);
        }

        /// <summary>0 when a word matches nothing, else higher for better matches.</summary>
        public int Score(AssetKind kind, string[] words)
        {
            int score = 1;
            ReadOnlySpan<char> name = Text.AsSpan(NameStart, NameLength);
            foreach (string w in words)
            {
                int at = name.IndexOf(w, StringComparison.Ordinal);
                if (at >= 0)
                {
                    score += w.Length == name.Length ? 8 : at == 0 ? 5 : 3;
                }
                else if (Text.Contains(w, StringComparison.Ordinal))
                {
                    // The package or group, the origin or the gloss.
                    score += 1;
                }
                else if (!IsKindWord(kind, w))
                {
                    return 0;
                }
            }

            return score;
        }

        private static bool IsKindWord(AssetKind kind, string w) => kind switch
        {
            AssetKind.Material => w is "material" or "materials",
            AssetKind.Mesh => w is "mesh" or "meshes" or "staticmesh" or "staticmeshes",
            AssetKind.Texture => w is "texture" or "textures",
            _ => w is "sound" or "sounds",
        };
    }

    private static AssetEntry Merge(AssetEntry first, AssetEntry later)
    {
        var used = new Dictionary<string, int>(first.UsedIn, StringComparer.OrdinalIgnoreCase);
        foreach ((string map, int n) in later.UsedIn)
        {
            used[map] = used.GetValueOrDefault(map) + n;
        }

        return new AssetEntry
        {
            Kind = first.Kind, Path = first.Path, Origin = first.Origin, File = first.File, Texture = first.Texture, Width = first.Width,
            Height = first.Height, Mesh = first.Mesh, Triangles = first.Triangles, BoundsMin = first.BoundsMin, BoundsMax = first.BoundsMax,
            Materials = first.Materials, Duration = first.Duration, SoundKind = first.SoundKind, UsedIn = used, Gloss = first.Gloss,
        };
    }

    private static AssetCatalog BuildContent(string contentRoot)
    {
        var library = ContentLibrary.For(contentRoot);
        string cacheDir = Path.Combine(CacheRoot, AssetIndexer.Hash(Encoding.UTF8.GetBytes(contentRoot.ToUpperInvariant())));
        var sources = new List<AssetSource>();
        int fromCache = 0;
        foreach (string dir in library.MapDirs())
        {
            string digest = AssetIndexer.MapDigest(dir);
            (AssetSource source, bool cached) = AssetIndexCache.Get(cacheDir, Path.GetFileName(dir), digest, dir, () => AssetIndexer.IndexMap(dir));
            sources.Add(source);
            fromCache += cached ? 1 : 0;
        }

        if (Directory.Exists(Path.Combine(contentRoot, "shared")))
        {
            (AssetSource source, bool cached) = AssetIndexCache.Get(cacheDir, "shared", AssetIndexer.SharedDigest(contentRoot), contentRoot,
                () => AssetIndexer.IndexShared(contentRoot));
            sources.Add(source);
            fromCache += cached ? 1 : 0;
        }

        int maps = sources.Count(s => s.Name != "shared");
        return new AssetCatalog(sources, $"{maps} converted maps and shared content ({fromCache}/{sources.Count} from cache)");
    }

    /// <summary>Kind and path keys, the path ignoring case (as the compiler matches paths).</summary>
    private sealed class KeyComparer : IEqualityComparer<(AssetKind Kind, string Path)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((AssetKind Kind, string Path) x, (AssetKind Kind, string Path) y) =>
            x.Kind == y.Kind && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((AssetKind Kind, string Path) k) => HashCode.Combine(k.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path));
    }
}
