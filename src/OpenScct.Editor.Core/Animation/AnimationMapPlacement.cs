using OpenScct.Animation;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Places a custom animated actor without inventing a stock package or changing a gameplay slot.</summary>
public static class AnimationMapPlacement
{
    public static AddObjectsCommand Prepare(MapProject map, AnimationProject project, string clipId, Vec3 location)
    {
        AnimationClip clip = project.Clips.Single(c => c.Id == clipId);
        string id = "AnimatedProp"; int suffix = 1;
        var ids = map.Actors.Select(a => a.Id).Concat(map.Brushes.Select(b => b.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (ids.Contains(id)) id = "AnimatedProp" + suffix++;
        var actor = new Actor(id, "SBase.SAnimatedMesh") { Location = location };
        actor.Props.Set("Mesh", PropValues.AssetRef("osanim." + clip.Skeleton));
        actor.Props.Set("DrawType", SvJsonNode.String("DT_Mesh"));
        var node = new GraphNode { Clip = clip.Id };
        var graph = new AnimationGraph { Name = id, Entry = "Playing", Skeleton = clip.Skeleton, Nodes = [node], States = [new() { Name = "Playing", Node = node.Id }] };
        project.Graphs.Add(graph);
        project.Bindings.Add(new() { Target = "actor:" + id, Slot = "Animation", Graph = graph.Id });
        AnimationJson.Validate(project);
        return new("Place animated prop", actors: [actor]);
    }
}
