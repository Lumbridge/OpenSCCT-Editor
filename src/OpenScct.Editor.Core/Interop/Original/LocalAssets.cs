using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>An asset of the map's own package with the file the export makes it from.</summary>
public sealed record LocalAsset(AssetKind Kind, string? File, LibraryMaterial? Material = null, LibraryMesh? Mesh = null)
{
    /// <summary>True when the export has something to import it from.</summary>
    public bool HasSource => File is not null || Material?.Texture is not null || Mesh is not null;
}

/// <summary>
/// Where the assets of a map's own package (MyLevel, or the map's name as an .sdc import keeps it) come from: the
/// project's imported assets, the converted map an import was made from, or the content library.
/// </summary>
public static class LocalAssets
{
    /// <summary>Properties that hold objects the editor makes, not assets (the level summary, mesh lighting).</summary>
    private static readonly HashSet<string> s_notAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Summary", "StaticMeshInstance", "StaticMeshInstanceOnPSX2", "StaticMeshInstanceOnXBOX", "Brush", "Region",
    };

    /// <summary>The resolver for a project: its own assets, the content library and the converted map it was imported
    /// from (when that folder is still there).</summary>
    public static AssetResolver Resolver(MapProject p, string? contentRoot, string? projectFolder)
    {
        AssetResolver resolver = AssetResolver.For(contentRoot, projectFolder);
        return SourceMap(p) is { } source ? resolver.WithSource(source) : resolver;
    }

    /// <summary>The key of <see cref="MapProject.Extra"/> naming the converted map a map imported from an export for
    /// the original editor took its own assets from (the map that was exported had it as its baked source).</summary>
    public const string SourceMapKey = "assetSourceMap";

    /// <summary>The converted map the map's own assets come from, while that folder is still there: the one it was
    /// imported from, or the one the map an export was made from had.</summary>
    public static string? SourceMap(MapProject p)
    {
        string? source = p.Baked?.SourceMap is { Length: > 0 } baked ? baked : (p.Extra[SourceMapKey] as OpenScct.Content.Json.SvJsonString)?.Value;
        return source is not null && Directory.Exists(source) ? source : null;
    }

    /// <summary>True for a property whose value is an asset reference the original map needs (not an object the
    /// editor makes).</summary>
    public static bool IsAssetProperty(string key) => !s_notAssets.Contains(key);

    /// <summary>The path in the original editor's map package: MyLevel.Group.Name.</summary>
    public static string LevelPath(string path)
    {
        (string _, string group, string name) = AssetPaths.Split(path);
        return group.Length > 0 ? $"{AssetPaths.LocalPackage}.{group}.{name}" : $"{AssetPaths.LocalPackage}.{name}";
    }

    /// <summary>A local asset and its source, or null when nothing is known of it.</summary>
    /// <param name="key">The property that refers to it (a StaticMesh property refers to a mesh).</param>
    public static LocalAsset? Find(MapProject p, AssetCatalog catalog, AssetResolver resolver, string path, string key)
    {
        string level = LevelPath(path);
        string own = p.Id + level[AssetPaths.LocalPackage.Length..];
        AssetEntry? e = RoundTripLint.FindAsset(p, catalog, path, key);
        bool mesh = e?.Kind == AssetKind.Mesh || (e is null && key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
        if (mesh)
        {
            return (resolver.FindMesh(path) ?? resolver.FindMesh(level) ?? resolver.FindMesh(own)) is { } m
                ? new LocalAsset(AssetKind.Mesh, m.GlbPath, Mesh: m)
                : e is null ? null : new LocalAsset(AssetKind.Mesh, null);
        }

        if (e?.Kind == AssetKind.Sound)
        {
            return new LocalAsset(AssetKind.Sound, e.File);
        }

        if ((resolver.FindMaterial(path) ?? resolver.FindMaterial(level) ?? resolver.FindMaterial(own)) is { } material)
        {
            return new LocalAsset(AssetKind.Material, material.Texture is { } t ? Path.GetFullPath(Path.Combine(material.RenderDir, t)) : null, material);
        }

        if (e is { Kind: AssetKind.Texture or AssetKind.Material })
        {
            return new LocalAsset(e.Kind, e.File ?? e.Texture);
        }

        return StrategicMapFloor(p, level) is { } floor ? new LocalAsset(AssetKind.Texture, floor) : null;
    }

    /// <summary>A strategic map floor (LevelInfo's Minimap list names MyLevel.Minimap.Floor&lt;n&gt;): the picture the
    /// converted map it was imported from holds for it.</summary>
    private static string? StrategicMapFloor(MapProject p, string level)
    {
        (string _, string group, string name) = AssetPaths.Split(level);
        if (!group.Equals("Minimap", StringComparison.OrdinalIgnoreCase) || !name.StartsWith("Floor", StringComparison.OrdinalIgnoreCase)
            || SourceMap(p) is not { } source)
        {
            return null;
        }

        string file = Path.Combine(source, "ui", "strategicmap", name.ToLowerInvariant() + ".png");
        return File.Exists(file) ? file : null;
    }
}
