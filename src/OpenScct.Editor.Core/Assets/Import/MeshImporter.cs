using System.Numerics;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>How an imported mesh collides.</summary>
public enum CollisionMode
{
    /// <summary>A convex hull per part (OBJ object or group, glTF node); one part is split into its connected pieces.</summary>
    Parts,

    /// <summary>One convex hull around everything.</summary>
    Hull,

    /// <summary>The bounding box.</summary>
    Box,

    /// <summary>The render triangles themselves (per polygon; costly on big meshes).</summary>
    Mesh,
}

public sealed record MeshImportOptions
{
    /// <summary>Asset name (default: the file name, made safe).</summary>
    public string? Name { get; init; }

    /// <summary>Asset group inside MyLevel (default Meshes).</summary>
    public string Group { get; init; } = "Meshes";

    /// <summary>Extra uniform scale. OBJ is read as Unreal units and glTF as metres before this applies.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>OBJ only: the file is Z-up (Blender axes) instead of Y-up.</summary>
    public bool ZUp { get; init; }

    public CollisionMode Collision { get; init; } = CollisionMode.Parts;

    /// <summary>Replace an existing asset of the same path.</summary>
    public bool Overwrite { get; init; }
}

public sealed record MeshImportResult(string Path, string File, int Triangles, int CollisionTriangles, int Hulls, Vec3 BoundsMin, Vec3 BoundsMax,
    IReadOnlyList<string> Materials, IReadOnlyList<string> Notes);

/// <summary>
/// Imports an OBJ or glTF mesh into a project's own assets: <c>assets/meshes/&lt;Group&gt;_&lt;Name&gt;.glb</c> holds the
/// render mesh (one primitive per material) and a generated collision mesh, <c>assets/textures/</c> its PNG
/// textures, and <c>assets/assets.json</c> lists the mesh and its materials as <c>MyLevel.*</c> paths. The compiler
/// then places it like any library mesh, and collides it with the collision mesh.
/// </summary>
public static class MeshImporter
{
    /// <summary>The original engine's collision triangle cap per mesh (past it, the stock game crashes).</summary>
    public const int OriginalCollisionTriangleCap = 5512;

    private const int MaxHullParts = 64;

    public static MeshImportResult Import(string projectFolder, string sourceFile, MeshImportOptions options)
    {
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException($"{sourceFile} not found", sourceFile);
        }

        string ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        SourceMesh src = ext switch
        {
            ".obj" => ObjReader.Read(sourceFile, options.ZUp),
            ".gltf" or ".glb" => GltfReader.Read(sourceFile),
            _ => throw new NotSupportedException($"{ext} meshes are not supported (OBJ, glTF and GLB are)"),
        };
        if (Math.Abs(options.Scale - 1) > 1e-9)
        {
            src.Scale((float)options.Scale);
        }

        if (!src.RenderParts.Any())
        {
            throw new InvalidDataException($"{Path.GetFileName(sourceFile)} has no triangles to draw");
        }

        string name = AssetPaths.Safe(options.Name ?? Path.GetFileNameWithoutExtension(sourceFile));
        string group = AssetPaths.Safe(options.Group);
        string assetPath = $"{AssetPaths.LocalPackage}.{group}.{name}";
        ProjectAssets assets = ProjectAssets.For(projectFolder);
        JsonObject manifest = assets.Edit();
        JsonObject meshes = (manifest["meshes"] ??= new JsonObject()).AsObject();
        if (meshes.ContainsKey(assetPath) && !options.Overwrite)
        {
            throw new InvalidOperationException($"{assetPath} already exists; import with overwrite to replace it");
        }

        var notes = new List<string>(src.Notes);
        string root = assets.Root;
        JsonObject materials = (manifest["materials"] ??= new JsonObject()).AsObject();
        List<string> keys = src.RenderParts.SelectMany(p => p.Triangles).Select(t => t.Material ?? "Default").Distinct(StringComparer.Ordinal).ToList();
        ReplaceMaterials(materials, meshes[assetPath] as JsonObject, keys.Select(k => MaterialPath(name, k)).ToList(), assetPath);
        Directory.CreateDirectory(Path.Combine(root, "meshes"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));

        // Materials: one MyLevel material per source material, with its texture copied (or a flat colour made).
        var materialPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        Glb glb = Glb.Create("openscct-editor mesh import");
        var materialIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            SourceMaterial sm = src.Materials.GetValueOrDefault(key) ?? new SourceMaterial(key);
            string matPath = MaterialPath(name, key);
            string texture = $"textures/{AssetPaths.LocalPackage}_{name}_{AssetPaths.Safe(key)}.png";
            byte[] png = TexturePng(sm, notes);
            File.WriteAllBytes(Path.Combine(root, texture), png);
            (int w, int h) = Png.SizeOf(Path.Combine(root, texture)) ?? (8, 8);
            materials[matPath] = new JsonObject
            {
                ["flags"] = new JsonObject
                {
                    ["masked"] = sm.AlphaMode == "MASK", ["texture"] = texture, ["translucent"] = sm.AlphaMode == "BLEND",
                    ["two_sided"] = sm.DoubleSided, ["unlit"] = false,
                },
                ["uSize"] = w,
                ["vSize"] = h,
            };
            if (sm.Authored is { } authored)
            {
                authored.Textures["baseColor"] = new() { File = texture, Srgb = true };
                foreach ((string slot, var map) in sm.Maps)
                {
                    byte[]? data = map.Bytes ?? (map.File is { } mapFile && File.Exists(mapFile) ? File.ReadAllBytes(mapFile) : null);
                    if (data is null) { notes.Add($"{matPath}: missing {slot} map; omitted"); authored.Textures.Remove(slot); continue; }
                    byte[] encoded = RgbaImage.FormatOf(data) == "PNG" ? data : RgbaImage.Decode(data).EncodePng();
                    string relative = $"textures/{AssetPaths.LocalPackage}_{name}_{AssetPaths.Safe(key)}_{slot}.png";
                    File.WriteAllBytes(Path.Combine(root, relative), encoded); authored.Textures[slot].File = relative;
                }
                materials[matPath]!["flags"]!["authored"] = authored.ToJson();
                byte[] fallback = LegacyMaterialBake.Base(new LibraryMaterial(root, "MyLevel", matPath, materials[matPath]!["flags"]!.AsObject(), null, w, h)).EncodePng();
                string fallbackFile = $"textures/{AssetPaths.LocalPackage}_{name}_{AssetPaths.Safe(key)}_fallback.png";
                File.WriteAllBytes(Path.Combine(root, fallbackFile), fallback); materials[matPath]!["flags"]!["texture"] = fallbackFile;
                materials[matPath]!["flags"]!["unlit"] = authored.Unlit;
            }
            materialPaths[key] = matPath;
            materialIndex[key] = glb.AddMaterial(matPath, texture, sm.DoubleSided, sm.AlphaMode);
            if (sm.Authored is { } definition) GltfAuthoredMaterial.Apply(glb, materialIndex[key], definition, file => File.ReadAllBytes(Path.Combine(root, file)), (file, bytes) => File.WriteAllBytes(Path.Combine(root, file), bytes));
        }

        int renderMesh = AddRenderMesh(glb, src, materialIndex, assetPath, out int triangles);
        (List<Vector3> collisionPositions, List<int> collisionIndices, int hulls) = Collision(src, options.Collision, notes);
        int? collisionMesh = null;
        if (collisionIndices.Count > 0)
        {
            collisionMesh = AddCollisionMesh(glb, collisionPositions, collisionIndices, materialIndex.Values.First(), assetPath);
        }

        int collisionTriangles = options.Collision == CollisionMode.Mesh && collisionMesh is null ? triangles : collisionIndices.Count / 3;
        if (collisionTriangles > OriginalCollisionTriangleCap)
        {
            notes.Add($"{collisionTriangles} collision triangles: over the original engine's cap of {OriginalCollisionTriangleCap} per mesh");
        }

        glb.AddSceneRoot(glb.AddNode(new JsonObject
        {
            ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = assetPath, ["role"] = "libraryMesh", ["variant"] = "" } },
            ["mesh"] = renderMesh,
            ["name"] = assetPath,
        }));
        if (collisionMesh is int cm)
        {
            glb.AddSceneRoot(glb.AddNode(new JsonObject
            {
                ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = assetPath, ["role"] = "collision" } },
                ["mesh"] = cm,
                ["name"] = assetPath + "#collision",
            }));
        }

        string file = $"meshes/{group}_{name}.glb";
        File.WriteAllBytes(Path.Combine(root, file), glb.ToBytes(null));

        (Vec3 min, Vec3 max) = Bounds(src.RenderParts.SelectMany(p => p.Positions));
        var entry = new JsonObject
        {
            ["bounds"] = new JsonArray(new JsonArray(min.X, min.Y, min.Z), new JsonArray(max.X, max.Y, max.Z)),
            ["collision"] = options.Collision.ToString().ToLowerInvariant(),
            ["collisionTriangles"] = collisionTriangles,
            ["file"] = file,
            ["hulls"] = hulls,
            ["materials"] = new JsonArray(materialPaths.Values.Select(v => (JsonNode)v).ToArray()),
            ["mesh"] = renderMesh,
            ["source"] = Path.GetFileName(sourceFile),
            ["triangles"] = triangles,
        };
        if (collisionMesh is int c)
        {
            entry["collisionMesh"] = c;
        }

        meshes[assetPath] = entry;
        ProjectAssets.Save(projectFolder, manifest);
        return new MeshImportResult(assetPath, Path.GetFullPath(Path.Combine(root, file)), triangles, collisionTriangles, hulls, min, max,
            materialPaths.Values.ToList(), notes);
    }

    /// <summary>A mesh's material path: MyLevel.&lt;mesh name&gt;.&lt;source material&gt;.</summary>
    private static string MaterialPath(string meshName, string key) => $"{AssetPaths.LocalPackage}.{meshName}.{AssetPaths.Safe(key)}";

    /// <summary>Drops the materials of the mesh being replaced, and refuses material paths another mesh owns (a mesh
    /// of the same name in another group).</summary>
    private static void ReplaceMaterials(JsonObject materials, JsonObject? replaced, List<string> wanted, string assetPath)
    {
        if (replaced?["materials"] is JsonArray old)
        {
            foreach (string path in old.Select(n => n?.GetValue<string>()).OfType<string>())
            {
                materials.Remove(path);
            }
        }

        if (wanted.FirstOrDefault(materials.ContainsKey) is { } taken)
        {
            throw new InvalidOperationException($"material {taken} already belongs to another mesh; import {assetPath} under another name");
        }
    }

    /// <summary>Keep source PNGs as-is, transcode other supported images, or make a flat colour swatch.</summary>
    private static byte[] TexturePng(SourceMaterial m, List<string> notes)
    {
        byte[]? data = m.ImageBytes ?? (m.ImageFile is { } f && File.Exists(f) ? File.ReadAllBytes(f) : null);
        if (data is not null)
        {
            if (RgbaImage.FormatOf(data) == "PNG")
            {
                return data;
            }

            try
            {
                RgbaImage image = RgbaImage.Decode(data);
                return Png.EncodeRgba(image.Width, image.Height, image.Pixels);
            }
            catch (InvalidDataException e)
            {
                notes.Add($"material {m.Name}: could not decode {Path.GetFileName(m.ImageFile ?? "embedded image")}: {e.Message}; it uses its colour");
            }
        }
        else if (m.ImageFile is not null)
        {
            notes.Add($"material {m.Name}: image {m.ImageFile} not found; it uses its colour");
        }

        const int size = 8;
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            rgba[i * 4] = m.Authored is null ? (byte)Math.Clamp(m.Color.X * 255, 0, 255) : (byte)255;
            rgba[i * 4 + 1] = m.Authored is null ? (byte)Math.Clamp(m.Color.Y * 255, 0, 255) : (byte)255;
            rgba[i * 4 + 2] = m.Authored is null ? (byte)Math.Clamp(m.Color.Z * 255, 0, 255) : (byte)255;
            rgba[i * 4 + 3] = 255;
        }

        return Png.EncodeRgba(size, size, rgba);
    }

    /// <summary>One mesh with a primitive per material (glTF space, as read).</summary>
    private static int AddRenderMesh(Glb glb, SourceMesh src, Dictionary<string, int> materialIndex, string name, out int triangles)
    {
        int first = -1;
        triangles = 0;
        foreach ((string key, int material) in materialIndex)
        {
            var pos = new List<float>();
            var nrm = new List<float>();
            var uv = new List<float>();
            var idx = new List<uint>();
            foreach (SourcePart part in src.RenderParts)
            {
                var remap = new Dictionary<int, uint>();
                foreach ((int a, int b, int c, string? m) in part.Triangles)
                {
                    if ((m ?? "Default") != key)
                    {
                        continue;
                    }

                    foreach (int v in new[] { a, b, c })
                    {
                        if (!remap.TryGetValue(v, out uint i))
                        {
                            i = (uint)(pos.Count / 3);
                            remap[v] = i;
                            Vector3 p = part.Positions[v], n = part.Normals[v];
                            pos.AddRange([p.X, p.Y, p.Z]);
                            nrm.AddRange([n.X, n.Y, n.Z]);
                            uv.AddRange([part.Uvs[v].X, part.Uvs[v].Y]);
                        }

                        idx.Add(i);
                    }

                    triangles++;
                }
            }

            int mesh = glb.AddMesh(name, pos.ToArray(), nrm.ToArray(), uv.ToArray(), null, idx.ToArray(), material);
            if (first < 0)
            {
                first = mesh;
                continue;
            }

            // Move this primitive into the first mesh, so the asset is one mesh with a section per material.
            JsonArray meshes = glb.Array("meshes");
            JsonNode prim = meshes[mesh]!["primitives"]![0]!;
            meshes[mesh]!["primitives"]!.AsArray().RemoveAt(0);
            meshes[first]!["primitives"]!.AsArray().Add(prim);
            meshes.RemoveAt(mesh);
        }

        return first;
    }

    private static int AddCollisionMesh(Glb glb, List<Vector3> positions, List<int> indices, int anyMaterial, string name)
    {
        var pos = positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        var nrm = new float[pos.Length];
        var uv = new float[positions.Count * 2];
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            Vector3 n = Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]);
            foreach (int v in indices.Skip(t).Take(3))
            {
                nrm[v * 3] += n.X;
                nrm[v * 3 + 1] += n.Y;
                nrm[v * 3 + 2] += n.Z;
            }
        }

        for (int v = 0; v < positions.Count; v++)
        {
            var n = new Vector3(nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
            (nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]) = (n.X, n.Y, n.Z);
        }

        int mesh = glb.AddMesh(name + "#collision", pos, nrm, uv, null, indices.Select(i => (uint)i).ToArray(), anyMaterial);
        // Collision has no material of its own; the compiler gives its triangles the default surface.
        glb.Array("meshes")[mesh]!["primitives"]![0]!.AsObject().Remove("material");
        return mesh;
    }

    /// <summary>The collision triangles for the chosen mode (glTF space). UCX_/MCD parts, when present, are the
    /// hulls whatever the mode (except Mesh).</summary>
    private static (List<Vector3> Positions, List<int> Indices, int Hulls) Collision(SourceMesh src, CollisionMode mode, List<string> notes)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        int hulls = 0;
        void Add(ConvexHull.Result? hull)
        {
            if (hull is null)
            {
                return;
            }

            int start = positions.Count;
            positions.AddRange(hull.Points);
            foreach ((int a, int b, int c) in hull.Triangles)
            {
                indices.AddRange([start + a, start + b, start + c]);
            }

            hulls++;
        }

        if (mode == CollisionMode.Mesh)
        {
            // No separate mesh: the compiler collides the render triangles (per polygon).
            return (positions, indices, 0);
        }

        List<SourcePart> ucx = src.CollisionParts.ToList();
        if (ucx.Count > 0)
        {
            foreach (SourcePart p in ucx)
            {
                Add(ConvexHull.Of(p.Positions));
            }

            notes.Add($"{ucx.Count} UCX_/MCD collision part(s) used as hulls");
            return (positions, indices, hulls);
        }

        List<Vector3> all = src.RenderParts.SelectMany(p => p.Positions).ToList();
        switch (mode)
        {
            case CollisionMode.Box:
                (Vector3 lo, Vector3 hi) = (all.Aggregate(Vector3.Min), all.Aggregate(Vector3.Max));
                Add(ConvexHull.Box(lo, hi));
                break;
            case CollisionMode.Hull:
                Add(ConvexHull.Of(all));
                break;
            default:
                List<List<Vector3>> pieces = Pieces(src);
                if (pieces.Count > MaxHullParts)
                {
                    notes.Add($"{pieces.Count} separate pieces is too many hulls; one hull per part instead");
                    pieces = src.RenderParts.Select(p => p.Positions).ToList();
                }

                if (pieces.Count > MaxHullParts)
                {
                    notes.Add("still too many parts; one hull around everything");
                    pieces = [all];
                }

                foreach (List<Vector3> piece in pieces)
                {
                    Add(ConvexHull.Of(piece));
                }

                break;
        }

        return (positions, indices, hulls);
    }

    /// <summary>The pieces to hull: each part, and a lone part split into its connected pieces (by shared positions).</summary>
    private static List<List<Vector3>> Pieces(SourceMesh src)
    {
        List<SourcePart> parts = src.RenderParts.ToList();
        if (parts.Count > 1)
        {
            return parts.Select(p => p.Positions).ToList();
        }

        SourcePart part = parts[0];
        var key = new Dictionary<(long, long, long), int>();
        int[] parent = new int[part.Positions.Count];
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
            Vector3 p = part.Positions[i];
            var k = ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
            if (key.TryGetValue(k, out int same))
            {
                parent[Find(i)] = Find(same);
            }
            else
            {
                key[k] = i;
            }
        }

        foreach ((int a, int b, int c, _) in part.Triangles)
        {
            parent[Find(b)] = Find(a);
            parent[Find(c)] = Find(a);
        }

        return part.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct()
            .GroupBy(Find).Select(g => g.Select(i => part.Positions[i]).ToList()).ToList();
    }

    /// <summary>Bounds in Unreal units of glTF-space points.</summary>
    private static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<Vector3> points)
    {
        Vec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        foreach (Vector3 p in points)
        {
            var ue = new Vec3(Math.Round(p.X * 100.0, 3), Math.Round(p.Z * 100.0, 3), Math.Round(p.Y * 100.0, 3));
            min = Vec3.Min(min, ue);
            max = Vec3.Max(max, ue);
        }

        return (min, max);
    }
}
