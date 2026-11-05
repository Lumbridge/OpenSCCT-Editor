using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Render;

/// <summary>A static mesh ready to draw: the Godot mesh (one surface per section), its materials and glb primitives in
/// surface order, its bounds in the mesh's own UE space, and its triangles for picking (each section's positions in
/// glTF/Godot space and its triangle indices).</summary>
public sealed record LoadedMesh(ArrayMesh Mesh, IReadOnlyList<ViewMaterial> Materials, IReadOnlyList<int> Primitives, Vec3 Min, Vec3 Max,
    IReadOnlyList<(float[] Positions, int[] Indices)> Triangles);

/// <summary>
/// Static meshes for the views, loaded once per asset path: the glb is read on a worker thread and the Godot mesh is
/// made on the main thread. A path nothing ships (or a broken file) is remembered as missing, so the actor keeps its box.
/// Skeletal meshes load the same way, in their reference pose (<see cref="RequestSkeletal"/>).
/// </summary>
public static class StaticMeshes
{
    private const string SkeletalKey = "skeletal:";
    private static readonly Dictionary<string, LoadedMesh?> s_loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<LoadedMesh>>> s_waiting = new(StringComparer.OrdinalIgnoreCase);
    private static int s_generation;

    /// <summary>The mesh when it is already loaded (null while loading or when missing).</summary>
    public static LoadedMesh? Loaded(string path) => s_loaded.GetValueOrDefault(path);

    /// <summary>True when <paramref name="path"/> was looked for and nothing ships it (or its file is broken).</summary>
    public static bool IsMissing(string path) => s_loaded.TryGetValue(path, out LoadedMesh? m) && m is null;

    /// <summary>Calls <paramref name="ready"/> with the mesh of <paramref name="path"/> on the main thread (at once when
    /// loaded). A missing mesh never calls back.</summary>
    public static void Request(string path, Action<LoadedMesh> ready) =>
        Load(path, "Static mesh", ready, () => AssetService.Instance.Resolver.FindViewMesh(path) is { } lib ? () => MeshSections.Read(lib) : null);

    /// <summary>As <see cref="Request"/>, for the skeletal mesh <paramref name="asset"/> in its reference pose.</summary>
    /// <remarks>The file is read on the worker; its placement (RotOrigin, scale) is cached there for the caller.</remarks>
    public static void RequestSkeletal(string asset, Action<LoadedMesh> ready) =>
        Load(SkeletalKey + asset, "Skeletal mesh", ready, () =>
        {
            AssetResolver resolver = AssetService.Instance.Resolver;
            return resolver.HasSkeletalMesh(asset) ? () => resolver.FindSkeletalMesh(asset) is { } file ? MeshSections.Read(file) : null : null;
        });

    /// <summary>The skeletal mesh when it is already loaded (null while loading or when missing).</summary>
    public static LoadedMesh? LoadedSkeletal(string asset) => Loaded(SkeletalKey + asset);

    /// <summary>Loads the mesh cached as <paramref name="path"/>: <paramref name="find"/> (main thread) gives the reader
    /// that runs on a worker, or null when nothing ships it.</summary>
    private static void Load(string path, string kind, Action<LoadedMesh> ready, Func<Func<List<MeshSection>?>?> find)
    {
        if (s_loaded.TryGetValue(path, out LoadedMesh? done))
        {
            if (done is not null)
            {
                ready(done);
            }

            return;
        }

        if (s_waiting.TryGetValue(path, out List<Action<LoadedMesh>>? list))
        {
            list.Add(ready);
            return;
        }

        s_waiting[path] = [ready];
        Func<List<MeshSection>?>? read = find();
        int generation = s_generation;
        BackgroundJobs.Start(_ =>
        {
            List<MeshSection>? sections = null;
            try
            {
                sections = read?.Invoke();
            }
            catch (Exception e)
            {
                // A broken file must not take the view down: the actor keeps its box.
                BackgroundJobs.Post(() => Core.EditorContext.Instance.Log($"{kind} {path} could not be read: {e.Message}", Severity.Warning));
            }

            BackgroundJobs.Post(() => Finish(path, sections, generation));
        });
    }

    /// <summary>Forgets every mesh (after the project's assets changed); loads in flight are dropped.</summary>
    public static void Clear()
    {
        s_generation++;
        s_loaded.Clear();
        s_waiting.Clear();
    }

    private static void Finish(string path, List<MeshSection>? sections, int generation)
    {
        if (generation != s_generation)
        {
            return;
        }

        LoadedMesh? mesh = sections is null ? null : Build(sections);
        s_loaded[path] = mesh;
        if (s_waiting.Remove(path, out List<Action<LoadedMesh>>? list) && mesh is not null)
        {
            foreach (Action<LoadedMesh> ready in list)
            {
                ready(mesh);
            }
        }
    }

    /// <summary>The Godot mesh of the sections (main thread). glTF space is Godot space, but glTF fronts are
    /// counter-clockwise and Godot's clockwise, so each triangle is flipped. Null when no section has triangles.</summary>
    public static LoadedMesh? Build(IEnumerable<MeshSection> sections)
    {
        var mesh = new ArrayMesh();
        var materials = new List<ViewMaterial>();
        var primitives = new List<int>();
        var triangles = new List<(float[], int[])>();
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (MeshSection s in sections)
        {
            int count = s.Positions.Length / 3;
            if (count == 0 || s.Indices.Length < 3)
            {
                continue;
            }

            var points = new Vector3[count];
            var normals = new Vector3[count];
            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = new Vector3(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]);
                normals[i] = new Vector3(s.Normals[i * 3], s.Normals[i * 3 + 1], s.Normals[i * 3 + 2]);
                uvs[i] = new Vector2(s.Uvs[i * 2], s.Uvs[i * 2 + 1]);
                min = min.Min(points[i]);
                max = max.Max(points[i]);
            }

            int[] indices = new int[s.Indices.Length / 3 * 3];
            for (int i = 0; i < indices.Length; i += 3)
            {
                indices[i] = s.Indices[i];
                indices[i + 1] = s.Indices[i + 2];
                indices[i + 2] = s.Indices[i + 1];
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = points;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            // The glb material's name, so a conveyor that scrolls one material of a mesh can find its surfaces.
            mesh.SurfaceSetName(mesh.GetSurfaceCount() - 1, s.Material ?? "");
            materials.Add(ViewMaterials.Section(s));
            primitives.Add(s.Primitive);
            triangles.Add((s.Positions, s.Indices));
        }

        return materials.Count == 0 ? null : new LoadedMesh(mesh, materials, primitives, Space.ToUe(min), Space.ToUe(max), triangles);
    }
}
