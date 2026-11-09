using System.Numerics;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>
/// A mesh read from an OBJ or glTF file, in glTF space (metres, Y up, right-handed, counter-clockwise fronts), split
/// into parts (OBJ objects and groups, glTF nodes). Parts named <c>UCX_</c> or <c>MCD</c> are collision hulls, as
/// in UE2 and the RE+ OBJ importer.
/// </summary>
public sealed class SourceMesh
{
    public List<SourcePart> Parts { get; } = new();

    /// <summary>Materials by name as the source names them.</summary>
    public Dictionary<string, SourceMaterial> Materials { get; } = new(StringComparer.Ordinal);

    /// <summary>Notes for the import report (skipped primitives, missing images).</summary>
    public List<string> Notes { get; } = new();

    public IEnumerable<SourcePart> RenderParts => Parts.Where(p => !p.IsCollision);

    public IEnumerable<SourcePart> CollisionParts => Parts.Where(p => p.IsCollision);

    /// <summary>Applies a uniform scale to every position.</summary>
    public void Scale(float s)
    {
        foreach (SourcePart p in Parts)
        {
            for (int i = 0; i < p.Positions.Count; i++)
            {
                p.Positions[i] *= s;
            }
        }
    }
}

/// <summary>One part: triangles grouped by material.</summary>
public sealed class SourcePart(string name)
{
    public string Name { get; } = name;

    public bool IsCollision => Name.StartsWith("UCX_", StringComparison.OrdinalIgnoreCase) || Name.StartsWith("MCD", StringComparison.OrdinalIgnoreCase);

    public List<Vector3> Positions { get; } = new();

    public List<Vector3> Normals { get; } = new();

    public List<Vector2> Uvs { get; } = new();

    /// <summary>Triangles: three vertex indices and a material name (null: the default material).</summary>
    public List<(int A, int B, int C, string? Material)> Triangles { get; } = new();

    public int AddVertex(Vector3 p, Vector3 n, Vector2 uv)
    {
        Positions.Add(p);
        Normals.Add(n);
        Uvs.Add(uv);
        return Positions.Count - 1;
    }
}

/// <summary>A source material: its base colour and the image it uses (a file, or PNG bytes embedded in a glb).</summary>
public sealed class SourceMaterial(string name)
{
    public OpenScct.Content.Materials.AuthoredMaterial? Authored { get; set; }
    public Dictionary<string, (string? File, byte[]? Bytes)> Maps { get; } = new();
    public string Name { get; } = name;

    public Vector3 Color { get; set; } = new(0.7f, 0.7f, 0.7f);

    public string? ImageFile { get; set; }

    public byte[]? ImageBytes { get; set; }

    public bool DoubleSided { get; set; }

    /// <summary>MASK or BLEND, or null for opaque.</summary>
    public string? AlphaMode { get; set; }
}
