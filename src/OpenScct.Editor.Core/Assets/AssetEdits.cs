using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>One object that uses an asset, and how (e.g. "3 faces", "StaticMesh").</summary>
public sealed record AssetUse(ObjectRef Object, string How);

/// <summary>Asset edits as undoable commands, and lookups of where a map uses an asset. The browser, the CLI and
/// scripts share these.</summary>
public static class AssetEdits
{
    /// <summary>The class a placed mesh gets.</summary>
    public const string MeshActorClass = "Engine.StaticMeshActor";

    /// <summary>Sets every face of the given brushes to <paramref name="material"/> (one undo step). Null when no
    /// brush is among <paramref name="refs"/>.</summary>
    public static ReplaceObjectsCommand? ApplyMaterial(MapProject project, IEnumerable<ObjectRef> refs, string material)
    {
        List<ObjectRef> brushes = refs.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && project.FindBrush(r.Id) is not null).Distinct().ToList();
        if (brushes.Count == 0)
        {
            return null;
        }

        string name = AssetPaths.Split(material).Name;
        return ReplaceObjectsCommand.Edit(project, brushes, $"Apply {name}", brush: b =>
        {
            foreach (BrushFace f in b.Shape.Faces)
            {
                f.Material = material;
            }
        });
    }

    /// <summary>A StaticMeshActor showing <paramref name="mesh"/> at <paramref name="location"/>. With
    /// <paramref name="bounds"/> (the mesh's local box) the mesh's bottom sits on the point, so a prop dropped on a
    /// floor stands on it.</summary>
    public static Actor MeshActor(MapProject project, string mesh, Vec3 location, (Vec3 Min, Vec3 Max)? bounds = null, Rot rotation = default)
    {
        string stem = AssetPaths.Safe(AssetPaths.Split(mesh).Name);
        var actor = new Actor(project.NewId(char.IsAsciiDigit(stem[0]) ? "Mesh" + stem : stem), MeshActorClass)
        {
            Location = bounds is { } b ? location - new Vec3(0, 0, b.Min.Z) : location,
            Rotation = rotation,
        };
        actor.Props["StaticMesh"] = PropValues.AssetRef(mesh);
        return actor;
    }

    /// <summary>Places one mesh actor (one undo step).</summary>
    public static AddObjectsCommand PlaceMesh(Actor actor) => new($"Place {AssetPaths.Split(PropValues.AssetPath(actor.Props["StaticMesh"]) ?? actor.Id).Name}", actors: [actor]);

    /// <summary>Where the map uses an asset: brush faces (materials), StaticMesh and any other asset reference in an
    /// actor's properties (sounds, skins, textures). Paths match ignoring case, in full or as Package.Name.</summary>
    public static List<AssetUse> Usages(MapProject project, string path)
    {
        var uses = new List<AssetUse>();
        foreach (Brush b in project.Brushes)
        {
            int faces = b.Shape.Faces.Count(f => f.Material is { } m && Same(m, path));
            if (faces > 0)
            {
                uses.Add(new AssetUse(ObjectRef.ForBrush(b), faces == 1 ? "1 face" : $"{faces} faces"));
            }
        }

        foreach (Actor a in project.Actors)
        {
            List<string> props = a.Props.Where(kv => References(kv.Value, path)).Select(kv => kv.Key).ToList();
            if (props.Count > 0)
            {
                uses.Add(new AssetUse(ObjectRef.Actor(a.Id), string.Join(", ", props)));
            }
        }

        return uses;
    }

    /// <summary>How many objects of the map use each asset path (for "in use" filters).</summary>
    public static Dictionary<string, int> InUse(MapProject project)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in project.Brushes)
        {
            foreach (string m in b.Shape.Faces.Select(f => f.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                used[m] = used.GetValueOrDefault(m) + 1;
            }
        }

        foreach (Actor a in project.Actors)
        {
            foreach ((string _, SvJsonNode v) in a.Props)
            {
                foreach (string p in AssetRefs(v))
                {
                    used[p] = used.GetValueOrDefault(p) + 1;
                }
            }
        }

        return used;
    }

    private static bool Same(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || AssetPaths.Short(a).Equals(AssetPaths.Short(b), StringComparison.OrdinalIgnoreCase);

    private static bool References(SvJsonNode node, string path) => AssetRefs(node).Any(p => Same(p, path));

    /// <summary>Every <c>{"$asset": ...}</c> inside a property value (arrays and structs included).</summary>
    private static IEnumerable<string> AssetRefs(SvJsonNode node)
    {
        switch (node)
        {
            case SvJsonObject o when PropValues.AssetPath(o) is { } p:
                yield return p;
                break;
            case SvJsonObject o:
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    foreach (string p in AssetRefs(kv.Value))
                    {
                        yield return p;
                    }
                }

                break;
            case SvJsonArray a:
                foreach (SvJsonNode item in a)
                {
                    foreach (string p in AssetRefs(item))
                    {
                        yield return p;
                    }
                }

                break;
        }
    }
}
