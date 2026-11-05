using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// How a brush surface looks once built: its material resolved through <see cref="AssetResolver"/> (null: the
/// compiler's grid stands in), the surface's poly flags merged with the material's own flags, and the texture size
/// UVs divide by (u = (dot(v - base, U) + panU) / USize). The compiler and the textured views share this, so the
/// views draw what a build writes.
/// </summary>
public sealed record SurfaceLook(string Name, int PolyBits, LibraryMaterial? Library, bool Masked, bool Translucent, bool TwoSided, bool Unlit,
    double USize, double VSize)
{
    /// <summary>The compiler's material key: the name, plus <c>#f&lt;bits&gt;</c> when the surface adds flags.</summary>
    public string Key => MakeKey(Name, PolyBits);

    /// <summary>glTF alpha mode: translucent wins over masked.</summary>
    public string? AlphaMode => Translucent ? "BLEND" : Masked ? "MASK" : null;

    /// <summary>The base texture file, absolute, or null for the grid.</summary>
    public string? TextureFile => Library?.Texture is { } t ? Path.GetFullPath(Path.Combine(Library.RenderDir, t)) : null;

    /// <summary>The key of a surface's look without resolving it (for caches).</summary>
    public static string KeyOf(string? material, int polyFlags) => MakeKey(material ?? Geometry.Starter.DefaultMaterial, Bits(polyFlags));

    public static SurfaceLook Resolve(AssetResolver resolver, string? material, int polyFlags)
    {
        string name = material ?? Geometry.Starter.DefaultMaterial;
        int bits = Bits(polyFlags);
        bool masked = (bits & 1) != 0, translucent = (bits & 2) != 0, twoSided = (bits & 4) != 0, unlit = (bits & 8) != 0;
        LibraryMaterial? lib = resolver.FindMaterial(name);
        if (lib is null)
        {
            return new SurfaceLook(name, bits, null, masked, translucent, twoSided, unlit, GridTexture.UvSize, GridTexture.UvSize);
        }

        return new SurfaceLook(name, bits, lib, masked || Flag(lib.Flags, "masked"), translucent || Flag(lib.Flags, "translucent"),
            twoSided || Flag(lib.Flags, "two_sided"), unlit || Flag(lib.Flags, "unlit"), Math.Max(1, lib.USize), Math.Max(1, lib.VSize));
    }

    private static string MakeKey(string name, int bits) => bits == 0 ? name : $"{name}#f{bits}";

    /// <summary>The poly flags that change a material: masked 1, translucent 2, two-sided 4, unlit 8.</summary>
    private static int Bits(int polyFlags) =>
        ((polyFlags & PolyFlags.Masked) != 0 ? 1 : 0) | ((polyFlags & PolyFlags.Translucent) != 0 ? 2 : 0) |
        ((polyFlags & PolyFlags.TwoSided) != 0 ? 4 : 0) | ((polyFlags & PolyFlags.Unlit) != 0 ? 8 : 0);

    private static bool Flag(JsonObject flags, string key) => flags[key] is JsonValue v && v.TryGetValue(out bool b) && b;
}
