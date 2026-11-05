using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>What an asset is.</summary>
public enum AssetKind
{
    /// <summary>A surface for brush faces (and mesh skins): the materials the compiler resolves.</summary>
    Material,

    /// <summary>A static mesh a StaticMeshActor can show.</summary>
    Mesh,

    /// <summary>An image file (base textures, combiner layers, masks, shared textures).</summary>
    Texture,

    /// <summary>A sound (shared WAVs, map sounds and streams).</summary>
    Sound,
}

/// <summary>Asset kind names as the CLI and the scripting API write them.</summary>
public static class AssetKinds
{
    /// <summary>material, mesh, texture or sound (plurals and a few short forms too); null for null.</summary>
    public static AssetKind? Parse(string? text) => text?.ToLowerInvariant() switch
    {
        null => null,
        "material" or "materials" or "mat" => AssetKind.Material,
        "mesh" or "meshes" or "staticmesh" => AssetKind.Mesh,
        "texture" or "textures" or "tex" => AssetKind.Texture,
        "sound" or "sounds" => AssetKind.Sound,
        _ => throw new ArgumentException($"kind must be material, mesh, texture or sound, not '{text}'"),
    };

    public static string Name(AssetKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>One asset of the catalogue: its path, where it comes from, its file and the facts the browser shows.</summary>
public sealed class AssetEntry
{
    public required AssetKind Kind { get; init; }

    /// <summary><c>Package.Group.Name</c> (what a face's material or an actor's StaticMesh stores).</summary>
    public required string Path { get; init; }

    /// <summary>Where it comes from: a converted map's id, <c>shared</c>, or <c>MyLevel</c> for the project's own.</summary>
    public required string Origin { get; init; }

    /// <summary>The file that holds it (texture PNG, glb, WAV/OGG), absolute; null for sounds made of other sounds.</summary>
    public string? File { get; init; }

    /// <summary>The base texture of a material, absolute.</summary>
    public string? Texture { get; init; }

    /// <summary>Texture size (materials and textures).</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>A mesh's index in its glb.</summary>
    public int Mesh { get; init; } = -1;

    /// <summary>A mesh's triangle count.</summary>
    public int Triangles { get; init; }

    /// <summary>A mesh's local bounds in Unreal units.</summary>
    public Vec3 BoundsMin { get; init; }

    public Vec3 BoundsMax { get; init; }

    /// <summary>The materials a mesh's sections use.</summary>
    public IReadOnlyList<string> Materials { get; init; } = [];

    /// <summary>A sound's length in seconds and its kind (sound, stream, random, sequence...).</summary>
    public double Duration { get; init; }

    public string? SoundKind { get; init; }

    /// <summary>Converted maps that ship the asset, with how often each places it (meshes: instances; others: 1).</summary>
    public IReadOnlyDictionary<string, int> UsedIn { get; init; } = new Dictionary<string, int>();

    /// <summary>An English reading of a French (or German, Polish) name, or null.</summary>
    public string? Gloss { get; init; }

    public string Package => AssetPaths.Split(Path).Package;

    public string Group => AssetPaths.Split(Path).Group;

    public string Name => AssetPaths.Split(Path).Name;

    public bool IsLocal => Origin == AssetPaths.LocalPackage;

    public Vec3 Size => BoundsMax - BoundsMin;

    public override string ToString() => $"{Kind} {Path} ({Origin})";
}
