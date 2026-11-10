using OpenScct.Content;
using OpenScct.Content.Collision;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

public sealed partial class CollisionBuild
{
    /// <summary>True for an actor that places a static mesh of its own (a StaticMeshActor and the like); devices whose
    /// mesh comes from their class are not level geometry to the traversal tools.</summary>
    public static bool PlacesMesh(Actor a) => a.Shape is null && a.Props.Contains("StaticMesh");

    /// <summary>
    /// A hash of everything the collision of the placed static meshes depends on (which actors, their meshes, where
    /// they are and whether they collide); 0 when the project places none.
    /// </summary>
    public static int PlacedMeshKey(MapProject project)
    {
        var h = new HashCode();
        int count = 0;
        foreach (Actor a in project.Actors)
        {
            if (!PlacesMesh(a))
            {
                continue;
            }

            count++;
            h.Add(a.Id, StringComparer.OrdinalIgnoreCase);
            h.Add(a.Class, StringComparer.OrdinalIgnoreCase);
            h.Add(a.Location);
            h.Add(a.Rotation);
            h.Add(a.DrawScale);
            h.Add(a.DrawScale3D);
            h.Add(a.PrePivot);
            h.Add(PropValues.AssetPath(a.Props["StaticMesh"]), StringComparer.OrdinalIgnoreCase);
            h.Add(a.Props.GetInt("DrawType", -1));
            foreach (string flag in s_meshKeyProps)
            {
                h.Add(a.Props[flag]?.Kind);
            }
        }

        return count == 0 ? 0 : h.ToHashCode();
    }

    private static readonly string[] s_meshKeyProps =
        ["bCollideActors", "bBlockActors", "bBlockPlayers", "bUseCylinderCollision", "bWorldGeometry"];

    /// <summary>
    /// The collision of the project's placed static meshes alone, built as a compile builds it (the converted map's
    /// collision sets of each mesh, else the library mesh's triangles), for the editor's traversal tools. Actors in
    /// <paramref name="skip"/> are left out (a baked map's collision already holds them). Null when nothing was added.
    /// </summary>
    public static CollisionData? PlacedMeshes(MapProject project, string? projectFolder, IReadOnlySet<string>? skip = null)
    {
        List<Actor> actors = project.Actors.Where(a => PlacesMesh(a) && skip?.Contains(a.Id) != true).ToList();
        if (actors.Count == 0)
        {
            return null;
        }

        try
        {
            // Only the meshes: the brushes, GEs and devices of the project play no part in their collision.
            var slim = new MapProject { Id = project.Id, Actors = actors, Baked = project.Baked, Extra = project.Extra };
            var ctx = new CompileContext(slim, new CompileOptions { ProjectFolder = projectFolder }, EditorServices.Classes);
            ctx.Library = LocalAssets.Resolver(project, ctx.ContentRoot, projectFolder);
            var b = new CollisionBuild(ctx);
            b.Material(null, 0, CollisionMaterialFlags.None);
            b.AddCollider(ColliderKind.WorldBsp, WorldFlags, null, 0, 0, 0, 0, Vec3.Zero, Rot.Zero, Vec3.One, Vec3.Zero, Aabb.Empty,
                $"{ctx.MapId}.World", null, SetWorld);
            bool added = false;
            foreach (Actor a in actors)
            {
                if (b.IsMeshPrimitive(a))
                {
                    added |= b.AddMeshActor(a, a.Id);
                }
            }

            if (!added)
            {
                return null;
            }

            b._zones.Add(new CollisionZone(b.String(ctx.LevelInfoId), CollisionZoneFlags.None, default, default));
            return b.Data();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or SvMapFormatException or ArgumentException or InvalidOperationException
            or FormatException or System.Text.Json.JsonException)
        {
            // A mesh file that does not read must not take the traversal tools down; they go on without the meshes.
            return null;
        }
    }
}
