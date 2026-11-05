namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// An asset being dragged out of a list (the asset browser's tiles and rows): its kind, its package-qualified path
/// (<c>Package.Group.Name</c>, what a face's material or an actor's StaticMesh stores) and the name to show. Every
/// drop target reads the same payload and takes the kinds it can use: the views place a mesh, and a surface can take
/// a material or a texture.
/// </summary>
public sealed record AssetDrag(AssetKind Kind, string Path, string Name)
{
    /// <summary>The payload of a catalogue entry.</summary>
    public static AssetDrag Of(AssetEntry e) => new(e.Kind, e.Path, e.Name);

    /// <summary>True for the kinds a list lets you drag: meshes, materials and textures (sounds have no drop target).</summary>
    public static bool CanDrag(AssetKind kind) => kind is AssetKind.Mesh or AssetKind.Material or AssetKind.Texture;

    /// <summary>A static mesh, placed as a StaticMeshActor where it is dropped.</summary>
    public bool IsMesh => Kind == AssetKind.Mesh;

    /// <summary>Something a surface shows: a material, or a texture.</summary>
    public bool IsSurface => Kind is AssetKind.Material or AssetKind.Texture;
}
