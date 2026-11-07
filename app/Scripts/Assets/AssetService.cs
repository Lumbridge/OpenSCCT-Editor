using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>
/// The app's asset state: the catalogue of the open map (built off the main thread), the resolver the viewport draws
/// with, and the favourites (kept in the app settings, so they are shared across maps).
/// </summary>
public sealed class AssetService
{
    private readonly HashSet<string> _favourites;
    private AssetResolver? _resolver;
    private string? _resolverFolder;
    private string? _resolverSource;
    private int _generation;
    private bool _redrawAfterIndex;

    private AssetService() => _favourites = new HashSet<string>(AppSettings.GetStrings("assets", "favourites"), StringComparer.OrdinalIgnoreCase);

    public static AssetService Instance { get; } = new();

    /// <summary>The catalogue (empty until the first index finishes).</summary>
    public AssetCatalog Catalog { get; private set; } = AssetCatalog.Empty;

    public bool Loading { get; private set; }

    /// <summary>Raised on the main thread when the catalogue was rebuilt.</summary>
    public event Action? CatalogChanged;

    /// <summary>Raised on the main thread when the project's own assets changed (an import): drawn materials and
    /// meshes must be resolved again.</summary>
    public event Action? AssetsChanged;

    /// <summary>Raised on the main thread when a content pack installation changed the available packs.</summary>
    public event Action? PacksChanged;

    public event Action? FavouritesChanged;

    /// <summary>The project folder assets resolve against (null for an unsaved map).</summary>
    public static string? ProjectFolder => EditorContext.Instance.Session.Folder;

    /// <summary>The source map of the open map's baked world (an imported map), or null.</summary>
    public static string? BakedSourceMap => EditorContext.Instance.Project.Baked?.SourceMap;

    /// <summary>How material and mesh paths resolve for the open map (the compiler's own rules): for a map imported
    /// with its baked world, the source map's own meshes and materials come last, so an actor drawing a mesh of the
    /// map's own package (an editor copy's glass panes and lit signs) shows it as the build does.</summary>
    public AssetResolver Resolver
    {
        get
        {
            string? folder = ProjectFolder;
            string? source = BakedSourceMap;
            if (_resolver is null || !string.Equals(folder, _resolverFolder, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source, _resolverSource, StringComparison.OrdinalIgnoreCase))
            {
                AssetResolver resolver = AssetResolver.For(EditorServices.ContentRoot, folder);
                _resolver = source is not null && BakedSource.FolderOf(source, resolver, folder) is { } map ? resolver.WithSource(map) : resolver;
                _resolverFolder = folder;
                _resolverSource = source;
                if (_resolver.Source is { } sourceLibrary)
                {
                    // Index the source map's materials and meshes off the main thread (its glb JSON can be megabytes),
                    // before the first brush face or mesh actor looks something up in it.
                    BackgroundJobs.Start(_ => sourceLibrary.Warm());
                }
            }

            return _resolver;
        }
    }

    /// <summary>
    /// The material a brush face gets to show a material or texture from the browser, or null and why not. A material
    /// is itself; a texture becomes the material that shows its image (<see cref="AssetCatalog.SurfaceMaterial"/>), as a
    /// face naming the texture file would resolve to nothing and draw (and build) as the missing-material grid.
    /// </summary>
    public (string? Material, string Why) SurfaceMaterial(AssetKind kind, string path)
    {
        if (kind == AssetKind.Material)
        {
            return (path, "");
        }

        string name = AssetPaths.Split(path).Name;
        if (kind != AssetKind.Texture)
        {
            return (null, $"{name} is not a material or texture");
        }

        if (Catalog.Find(AssetKind.Texture, path) is not { } entry)
        {
            return (null, $"the texture {name} is not in the asset index");
        }

        return Catalog.SurfaceMaterial(entry) is { } material
            ? (material, "")
            : (null, $"no material shows the texture {name}; pick a material on the Materials tab instead");
    }

    /// <summary>Indexes the content and the open map's assets in the background (the cache makes this quick).</summary>
    public void Reload()
    {
        int generation = ++_generation;
        string? content = EditorServices.ContentRoot;
        string? folder = ProjectFolder;
        bool redraw = _redrawAfterIndex;
        Loading = true;
        CatalogChanged?.Invoke();
        BackgroundJobs.Start(_ =>
        {
            AssetCatalog catalog;
            try
            {
                catalog = AssetCatalog.For(content, folder);
                if (redraw)
                {
                    ContentLibrary.For(content).Warm();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                BackgroundJobs.Post(() => EditorContext.Instance.Log($"Asset index failed: {ex.Message}", Severity.Warning));
                catalog = AssetCatalog.Empty;
            }

            BackgroundJobs.Post(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                Catalog = catalog;
                Loading = false;
                if (redraw)
                {
                    _redrawAfterIndex = false;
                    AssetsChanged?.Invoke();
                }

                CatalogChanged?.Invoke();
            });
        });
    }

    /// <summary>After an import or external asset refresh: resolve again, redraw and re-index. A session replacement
    /// passes false because the viewport is about to rebuild the scene itself.</summary>
    public void ProjectAssetsChanged(bool redraw = true)
    {
        _resolver = null;
        if (redraw)
        {
            AssetsChanged?.Invoke();
        }

        Reload();
    }

    public void ContentPacksChanged(string root)
    {
        EditorServices.RefreshContent(root);
        EnumTable.Reset();
        _resolver = null;
        _redrawAfterIndex = true;
        Reload();
        PacksChanged?.Invoke();
    }

    public bool IsFavourite(AssetEntry e) => _favourites.Contains(Key(e));

    public void ToggleFavourite(AssetEntry e)
    {
        if (!_favourites.Remove(Key(e)))
        {
            _favourites.Add(Key(e));
        }

        AppSettings.Set("assets", "favourites", _favourites.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        FavouritesChanged?.Invoke();
    }

    private static string Key(AssetEntry e) => $"{e.Kind}|{e.Path}";
}
