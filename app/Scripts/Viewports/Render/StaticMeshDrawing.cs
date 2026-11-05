using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Gameplay;
using ClassDefaults = OpenScct.Editor.Core.Compile.ClassDefaults;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// Draws static mesh actors with their real library meshes in the solid views, by the compiler's rule: no brush
/// shape, DrawType 8 (DT_StaticMesh) and a StaticMesh (the actor's own or its class default), placed with the actor's
/// DrawScale, DrawScale3D and PrePivot or else their class defaults, as the game places it. Gameplay actors that draw
/// their class's mesh (cameras, laser triggers, sensors, mines) find it in OpenSCCT's gameplay pack
/// (<see cref="AssetResolver.FindViewMesh"/>), and a security camera also shows the wall mount the game spawns for it
/// (<see cref="Mount"/>). The actor's outline becomes the mesh's bounds once the mesh has loaded, so picking and
/// selection follow the drawn mesh. Actors drawn with a skeletal mesh (DT_Mesh: animated props, fish) show it in its
/// reference pose, placed as the game places it; they keep their box for picking.
/// </summary>
public static class StaticMeshDrawing
{
    private const int DrawTypeMesh = 2;
    private const int DrawTypeStaticMesh = 8;
    private const string HolderName = "StaticMesh";
    private const string SkeletalName = "SkeletalMesh";
    private const string LitName = "Lit";
    private const string MountName = "Mount";
    private const string CameraClass = "SBase.SCamera";
    private static readonly Dictionary<(Vec3 Location, Rot Rotation), (Vec3 Location, Rot Rotation)> s_mounts = [];
    private static ActorRules? s_rules;
    private static TraversalWorld? s_mountWorld;

    /// <summary>Actor values with their class defaults, as the compiler reads them.</summary>
    private static ActorRules Rules => s_rules ??= new ActorRules(ClassDefaults.Create(EditorServices.ContentRoot, EditorServices.Classes));

    /// <summary>The static mesh path an actor draws with, or null.</summary>
    public static string? MeshPath(Actor a)
    {
        if (a.Shape is not null)
        {
            return null;
        }

        return (int)Rules.Number(a, "DrawType") == DrawTypeStaticMesh ? PropValues.AssetPath(Rules.Value(a, "StaticMesh")) : null;
    }

    /// <summary>The skeletal mesh an actor draws with (DT_Mesh and a Mesh, its own or its class default), or null.</summary>
    public static string? SkeletalPath(Actor a) =>
        a.Shape is null && (int)Rules.Number(a, "DrawType") == DrawTypeMesh ? PropValues.AssetPath(Rules.Value(a, "Mesh")) : null;

    /// <summary>
    /// Whether the views draw <paramref name="a"/> with a mesh: it has a static or skeletal mesh path, and the lookup
    /// (<see cref="StaticMeshes"/> through the open map's resolver: the project, the converted maps, a baked map's source
    /// map and the gameplay pack) has not found that nothing ships it. While the mesh is still loading this is true;
    /// <see cref="Attach"/>'s <c>missing</c> callback says when it turned out false.
    /// </summary>
    public static bool DrawsMesh(Actor a) =>
        MeshPath(a) is { } path ? !StaticMeshes.IsMissing(path)
        : SkeletalPath(a) is { } skeletal && !StaticMeshes.IsMissingSkeletal(skeletal);

    /// <summary>
    /// The wall mount a security camera shows in the game, or null: SCamera.PreBeginPlay with bAutoWallSupport spawns
    /// its WallSupportStaticmesh where <see cref="SecurityDevices.CameraMount"/> says (the game's 200 uu trace back for
    /// the wall, through the traversal world the security tools use; the last one built while a new one builds, and no
    /// wall until the first is ready). <see cref="Key"/> carries the spot, so a camera is redrawn when it changes.
    /// Editor-only: nothing is stored.
    /// </summary>
    public static (string Path, Vec3 Location, Rot Rotation)? Mount(Actor a)
    {
        if (!HasMount(a) || PropValues.AssetPath(Rules.Value(a, "WallSupportStaticmesh")) is not { } path)
        {
            return null;
        }

        TraversalWorld? world = GameplayState.Instance.LatestWorld;
        if (!ReferenceEquals(world, s_mountWorld) || s_mounts.Count > 4096)
        {
            s_mounts.Clear();
            s_mountWorld = world;
        }

        if (!s_mounts.TryGetValue((a.Location, a.Rotation), out (Vec3 Location, Rot Rotation) spot))
        {
            spot = SecurityDevices.CameraMount(a, world);
            s_mounts[(a.Location, a.Rotation)] = spot;
        }

        return (path, spot.Location, spot.Rotation);
    }

    /// <summary>Whether the actor is a security camera that spawns a wall mount in the game (bAutoWallSupport).</summary>
    public static bool HasMount(Actor a) =>
        a.Shape is null && (string.Equals(a.Class, CameraClass, StringComparison.OrdinalIgnoreCase) || Rules.IsA(a, CameraClass)) &&
        Rules.Flag(a, "bAutoWallSupport");

    /// <summary>Adds the mesh of <paramref name="a"/> under <paramref name="root"/> (now, or when it has loaded) and
    /// redraws <paramref name="wire"/> as the mesh's bounds. The wire marks this build: when the actor was rebuilt
    /// before the mesh loaded, its old wire has left the root and the late mesh is dropped. <paramref name="bakedLight"/>
    /// gives the Lit Preview's light for the actor once the mesh is there (<see cref="ShowBakedLight"/>).
    /// <paramref name="missing"/> runs (main thread, while this build is current) when nothing ships the actor's static or
    /// skeletal mesh, so the caller can show the actor as one drawn without a mesh.</summary>
    public static void Attach(Node3D root, Actor a, MeshInstance3D wire, Color color, Func<LitPreviewMesh?> bakedLight, Action? missing = null)
    {
        bool Built() => GodotObject.IsInstanceValid(root) && !root.IsQueuedForDeletion() && GodotObject.IsInstanceValid(wire) &&
            !wire.IsQueuedForDeletion() && wire.GetParent() == root;
        bool Current(string name) => Built() && root.GetNodeOrNull(name) is null;
        Action? gone = missing is null ? null : () =>
        {
            if (Built())
            {
                missing();
            }
        };

        if (MeshPath(a) is { } path)
        {
            StaticMeshes.Request(path, mesh =>
            {
                if (!Current(HolderName))
                {
                    return;
                }

                root.AddChild(Holder(mesh, HolderName, Local(a)));
                ShowBakedLight(root, a, bakedLight());
                var lines = new LineBuilder();
                lines.Box(ObjectGeometry.Corners(mesh.Min, mesh.Max).Select(c => Space.ToGodot(MeshToWorld(a, c) - a.Location)).ToList(), color);
                wire.Mesh = lines.Build();
            }, gone);
        }
        else if (SkeletalPath(a) is { } skeletal)
        {
            StaticMeshes.RequestSkeletal(skeletal, mesh =>
            {
                if (Current(SkeletalName) && SkeletalLocal(a, skeletal) is { } local)
                {
                    root.AddChild(Holder(mesh, SkeletalName, local));
                }
            }, gone);
        }

        if (Mount(a) is { } mount)
        {
            StaticMeshes.Request(mount.Path, mesh =>
            {
                if (Current(MountName))
                {
                    root.AddChild(Holder(mesh, MountName, new Transform3D(Space.RotationToGodot(mount.Rotation), Space.ToGodot(mount.Location - a.Location))));
                }
            });
        }
    }

    /// <summary>A skeletal mesh's transform under the actor's node, as OpenSCCT's SkeletalModel places it (UE2): at
    /// Location + PrePivot, turned by the actor's Rotation, then scaled by DrawScale x DrawScale3D and the mesh's own
    /// scale, then turned by its RotOrigin. Null when no converted copy of the mesh exists.</summary>
    private static Transform3D? SkeletalLocal(Actor a, string asset)
    {
        if (AssetService.Instance.Resolver.FindSkeletalMesh(asset) is not { } file)
        {
            return null;
        }

        Vec3 s = Rules.Scale(a);
        Vec3 m = file.MeshScale;
        Basis basis = Space.RotationToGodot(a.Rotation) * Basis.FromScale(new Vector3((float)s.X, (float)s.Z, (float)s.Y)) *
            Basis.FromScale(new Vector3((float)m.X, (float)m.Z, (float)m.Y)) * Space.RotationToGodot(file.RotOrigin);
        return new Transform3D(basis, Space.ToGodot(Rules.PrePivot(a)));
    }

    /// <summary>A node drawing <paramref name="mesh"/> in the unlit views and, as its <c>Lit</c> child, in the lit view.</summary>
    private static Node3D Holder(LoadedMesh mesh, string name, Transform3D transform)
    {
        var holder = new Node3D { Name = name, Transform = transform };
        holder.AddChild(TexturedSurfaces.Instance(mesh.Mesh, mesh.Materials, lit: false, VisualLayers.Unlit));
        MeshInstance3D lit = TexturedSurfaces.Instance(mesh.Mesh, mesh.Materials, lit: true, VisualLayers.Lit);
        lit.Name = LitName;
        holder.AddChild(lit);
        return holder;
    }

    /// <summary>Draws the actor's mesh in the lit view with the Lit Preview's baked light (its vertex colours, as the
    /// game draws it), or with the view's lights when <paramref name="light"/> is null, cut to the storey shown
    /// (<see cref="StoreyClip"/>) as <see cref="TexturedSurfaces.Instance"/> draws it. Nothing until the mesh has
    /// loaded; a light that does not fit the mesh (it changed since the bake) leaves the view's lights on.</summary>
    public static void ShowBakedLight(Node3D root, Actor a, LitPreviewMesh? light)
    {
        if (root.GetNodeOrNull<MeshInstance3D>($"{HolderName}/{LitName}") is not { } mi || Loaded(a) is not { } mesh)
        {
            return;
        }

        ArrayMesh? baked = light is null ? null : Coloured(mesh, light);
        mi.Mesh = baked ?? mesh.Mesh;
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            ViewMaterial m = mesh.Materials[i];
            mi.SetSurfaceOverrideMaterial(i, baked is null ? StoreyClip.Of(m.Lit, m.TextureFile) : VertexLit(m));
        }
    }

    /// <summary>The cut vertex-lit material of <paramref name="m"/> with the run-time world lights' additive pass over
    /// it, as the game lights a static mesh: baked vertex light plus the dynamic lights. Shared by every mesh using
    /// <paramref name="m"/>, as the pass reads only the lights and the mesh's own colours.</summary>
    private static ShaderMaterial VertexLit(ViewMaterial m)
    {
        ShaderMaterial mat = StoreyClip.Of(ViewMaterials.VertexLit(m), m.TextureFile);
        if (mat.NextPass is null)
        {
            ShaderMaterial dynamic = DynamicWorldLights.VertexPass(m.Unlit);
            mat.NextPass = dynamic;
            if (m.TextureFile is { } file)
            {
                TextureCache.Request(file, tex => DynamicWorldLights.SetAlbedo(dynamic, tex));
            }
        }

        return mat;
    }

    /// <summary>A copy of the mesh with the baked colours on its vertices, or null when a surface's primitive has no
    /// colours or a different vertex count.</summary>
    private static ArrayMesh? Coloured(LoadedMesh mesh, LitPreviewMesh light)
    {
        var copy = new ArrayMesh();
        for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
        {
            Godot.Collections.Array arrays = mesh.Mesh.SurfaceGetArrays(i);
            int count = ((Vector3[])arrays[(int)Mesh.ArrayType.Vertex]).Length;
            if (!light.Colours.TryGetValue(mesh.Primitives[i], out byte[]? rgba) || rgba.Length != count * 4)
            {
                return null;
            }

            var colours = new Color[count];
            for (int v = 0; v < count; v++)
            {
                colours[v] = Color.Color8(rgba[4 * v], rgba[(4 * v) + 1], rgba[(4 * v) + 2]);
            }

            arrays[(int)Mesh.ArrayType.Color] = colours;
            copy.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            copy.SurfaceSetName(i, mesh.Mesh.SurfaceGetName(i));
        }

        return copy;
    }

    /// <summary>Adds a faint copy of the mesh of <paramref name="a"/> under <paramref name="root"/> (now, or when it
    /// has loaded): see-through in the solid views and its bounds in every view. Nothing for actors without a mesh.</summary>
    public static void AttachGhost(Node3D root, Actor a, Color color)
    {
        var bounds = new LineBuilder();
        (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
        bounds.Box(ObjectGeometry.Corners(min, max).Select(c => Space.ToGodot(c - a.Location)).ToList(), color);
        MeshInstance3D? wire = WorldScene.AddMesh(root, bounds.Build(), SceneStyle.Lines, VisualLayers.Common);

        void Ready(LoadedMesh mesh, Transform3D transform)
        {
            if (!GodotObject.IsInstanceValid(root) || root.IsQueuedForDeletion())
            {
                return;
            }

            var holder = new Node3D { Name = "Ghost", Transform = transform };
            // Unshaded, tinted geometry remains legible even for glass or dark source textures.
            var material = new StandardMaterial3D
            {
                AlbedoColor = new Color(color.R, color.G, color.B, 0.65f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            holder.AddChild(new MeshInstance3D { Mesh = mesh.Mesh, MaterialOverride = material,
                Layers = VisualLayers.Unlit | VisualLayers.Lit });
            root.AddChild(holder);
            var lines = new LineBuilder();
            lines.Box(ObjectGeometry.Corners(mesh.Min, mesh.Max).Select(c => transform * Space.ToGodot(c)).ToList(), color);
            if (wire is not null)
            {
                wire.Mesh = lines.Build();
            }
        }

        if (MeshPath(a) is { } path)
        {
            StaticMeshes.Request(path, mesh => Ready(mesh, Local(a)));
        }
        else if (SkeletalPath(a) is { } skeletal)
        {
            StaticMeshes.RequestSkeletal(skeletal, mesh =>
            {
                if (SkeletalLocal(a, skeletal) is { } transform)
                {
                    Ready(mesh, transform);
                }
            });
        }
    }

    /// <summary>The mesh a static mesh actor draws with once it has loaded, else null.</summary>
    public static LoadedMesh? Loaded(Actor a) => MeshPath(a) is { } path ? StaticMeshes.Loaded(path) : null;

    /// <summary>World bounds of a static mesh actor whose mesh has loaded, or null (the caller uses its box).</summary>
    public static (Vec3 Min, Vec3 Max)? WorldBounds(Actor a)
    {
        if (Loaded(a) is { } mesh)
        {
            return ObjectGeometry.BoundsOf(ObjectGeometry.Corners(mesh.Min, mesh.Max).Select(c => MeshToWorld(a, c)));
        }
        if (SkeletalPath(a) is { } skeletal && StaticMeshes.LoadedSkeletal(skeletal) is { } posed
            && SkeletalLocal(a, skeletal) is { } transform)
        {
            return ObjectGeometry.BoundsOf(ObjectGeometry.Corners(posed.Min, posed.Max)
                .Select(c => a.Location + Space.ToUe(transform * Space.ToGodot(c))));
        }
        return null;
    }

    /// <summary>A point of the actor's mesh (UE units, mesh space) in the world, with the scale and PrePivot it is
    /// drawn with (the actor's own, else its class defaults).</summary>
    public static Vec3 MeshToWorld(Actor a, Vec3 p) => Rules.LocalToWorld(a, p);

    /// <summary>The DrawScale the actor is drawn at (its own, else the class default).</summary>
    public static double DrawScale(Actor a) => Rules.DrawScale(a);

    /// <summary>The DrawScale3D the actor is drawn at (its own, else the class default).</summary>
    public static Vec3 DrawScale3D(Actor a) => Rules.DrawScale3D(a);

    /// <summary>A world point in the actor's mesh space (the inverse of <see cref="MeshToWorld"/>).</summary>
    public static Vec3 WorldToMesh(Actor a, Vec3 p) => Rules.WorldToLocal(a, p);

    /// <summary>A key of what the actor draws, for the scene's rebuild check.</summary>
    public static string Key(Actor a) => $"{MeshPath(a)}|{SkeletalPath(a)}|{Mount(a)}";

    /// <summary>Forgets cached class data (after the class catalogue changed).</summary>
    public static void Reset() => s_rules = null;

    /// <summary>The mesh's transform under the actor's node (which sits at its location): the UE actor transform
    /// R * ((p - PrePivot) * scale), carried into Godot space, with the class's DrawScale, DrawScale3D and PrePivot
    /// where the actor has none of its own (a mine's PrePivot, a sticky cam's DrawScale 2).</summary>
    private static Transform3D Local(Actor a)
    {
        Vec3 s = Rules.Scale(a);
        Basis basis = Space.RotationToGodot(a.Rotation) * Basis.FromScale(new Vector3((float)s.X, (float)s.Z, (float)s.Y));
        return new Transform3D(basis, -(basis * Space.ToGodot(Rules.PrePivot(a))));
    }
}
