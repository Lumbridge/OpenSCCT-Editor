using OpenScct.Editor.Core.Compile.Output;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// How a material or static mesh path becomes files: the project's own assets first (<c>assets/assets.json</c>),
/// then the converted maps' render libraries in map-name order, and for a baked project (a compile, and the views) its
/// source map.
/// The compiler, the asset catalogue and the viewport all resolve through this, so what the browser offers is exactly
/// what builds. The views also draw gameplay actors' class-default meshes from OpenSCCT's gameplay pack
/// (<see cref="FindViewMesh"/>), which the game draws from its own copy, so a build takes only their collision from
/// there (CollisionBuild), never their render mesh.
/// </summary>
public sealed class AssetResolver
{
    private AssetResolver(ContentLibrary content, ProjectAssets? project, ContentLibrary? source = null, GameplayPack? gameplay = null,
        SkeletalLibrary? skeletal = null)
    {
        Content = content;
        Project = project;
        Source = source;
        Gameplay = gameplay ?? GameplayPack.Empty;
        Skeletal = skeletal ?? SkeletalLibrary.Empty;
    }

    public static AssetResolver Empty { get; } = new(ContentLibrary.Empty, null);

    public ContentLibrary Content { get; }

    /// <summary>The project's own assets, or null when the project has no folder yet.</summary>
    public ProjectAssets? Project { get; }

    /// <summary>A baked project's source map, searched last: its map-local assets are in no library map.</summary>
    public ContentLibrary? Source { get; }

    /// <summary>The class-default meshes of gameplay actors (cameras, lasers, sensors...), for the views.</summary>
    public GameplayPack Gameplay { get; }

    /// <summary>The skeletal meshes the views draw in their reference pose (animated props, fish).</summary>
    public SkeletalLibrary Skeletal { get; }

    /// <summary>This resolver with a converted map folder searched after the library.</summary>
    public AssetResolver WithSource(string mapDir) => new(Content, Project, ContentLibrary.ForMap(mapDir), Gameplay, Skeletal);

    /// <param name="contentRoot">OpenSCCT's content folder (null: no library).</param>
    /// <param name="projectFolder">The .osmap folder (null: no map-local assets).</param>
    public static AssetResolver For(string? contentRoot, string? projectFolder) =>
        new(ContentLibrary.For(contentRoot), projectFolder is null ? null : ProjectAssets.For(projectFolder), gameplay: GameplayPack.For(contentRoot),
            skeletal: SkeletalLibrary.For(contentRoot));

    /// <summary>The folder of a map in the content library, or null.</summary>
    public string? MapFolder(string map) => Content.MapFolder(map) ?? Source?.MapFolder(map);

    public LibraryMaterial? FindMaterial(string name) => Project?.FindMaterial(name) ?? Content.FindMaterial(name) ?? Source?.FindMaterial(name) ?? Windows.WindowAssets.FindMaterial(name);

    public LibraryMesh? FindMesh(string asset) => Project?.FindMesh(asset) ?? Content.FindMesh(asset) ?? Source?.FindMesh(asset) ?? Windows.WindowAssets.FindMesh(asset);

    /// <summary>The mesh the views draw for <paramref name="asset"/>: <see cref="FindMesh"/>, else the gameplay pack's
    /// copy (a security camera's or laser trigger's class mesh, which no converted map's library carries).</summary>
    public LibraryMesh? FindViewMesh(string asset) => FindMesh(asset) ?? Gameplay.FindMesh(asset);

    /// <summary>Whether a skeletal mesh for the views exists (cheap: no file is read).</summary>
    public bool HasSkeletalMesh(string asset) => Skeletal.Has(asset);

    /// <summary>A skeletal mesh for the views, or null (reads its glTF JSON the first time).</summary>
    public SkeletalMeshFile? FindSkeletalMesh(string asset) => Skeletal.FindMesh(asset);

    /// <summary>A glb file (loaded once per version).</summary>
    public Glb Glb(string glbPath) => GlbCache.Load(glbPath);
}
