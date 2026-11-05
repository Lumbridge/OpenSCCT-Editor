using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Model;

/// <summary>
/// A placed, linked copy of a prefab. Its objects carry the instance id in <see cref="EditorMeta.PrefabInstance"/> and
/// their prefab part id in <see cref="EditorMeta.PrefabPart"/>. Updating the instance rebuilds those objects from the
/// prefab with the instance's parameters and transform; breaking it forgets the link and keeps the objects.
/// </summary>
public sealed class PrefabInstance
{
    public PrefabInstance(string id, string prefab)
    {
        Id = id;
        Prefab = prefab;
    }

    /// <summary>Unique among the map's instances, ignoring case (e.g. <c>LaserGrid0</c>).</summary>
    public string Id { get; set; }

    /// <summary>The prefab's name (looked up in the map's prefabs folder, the user library, then the built-in one).</summary>
    public string Prefab { get; set; }

    /// <summary>Where the prefab's pivot was put.</summary>
    public Vec3 Location { get; set; }

    public Rot Rotation { get; set; }

    /// <summary>The parameter values given at placement (the rest take the prefab's defaults).</summary>
    public SortedDictionary<string, SvJsonNode> Params { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A hash of the prefab as placed, to tell when the prefab changed since.</summary>
    public string? Hash { get; set; }

    /// <summary>One part's place inside the prefab when it was placed: if the user moves or turns the instance's
    /// objects afterwards, updating recovers the instance transform from that part.</summary>
    public string? AnchorPart { get; set; }

    public Vec3 AnchorLocal { get; set; }

    public Rot AnchorRotation { get; set; }

    public PrefabInstance Clone() => new(Id, Prefab)
    {
        Location = Location,
        Rotation = Rotation,
        Params = new SortedDictionary<string, SvJsonNode>(Params, StringComparer.Ordinal),
        Hash = Hash,
        AnchorPart = AnchorPart,
        AnchorLocal = AnchorLocal,
        AnchorRotation = AnchorRotation,
    };
}
