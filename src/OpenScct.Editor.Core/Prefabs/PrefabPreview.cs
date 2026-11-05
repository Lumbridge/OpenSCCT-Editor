using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>
/// A prefab expanded with its default parameters at the origin of an empty scratch map, exactly as
/// <c>prefab.place</c> would put it in a map. The Prefabs panel draws its thumbnails from it.
/// </summary>
public static class PrefabPreview
{
    /// <summary>The scratch map holding the prefab's parts, or null when the prefab does not expand (the reason is in
    /// <paramref name="problem"/>). <paramref name="mapFolder"/> finds the map's own prefabs.</summary>
    public static MapProject? Expand(string name, string? mapFolder, out string? problem)
    {
        var session = new EditorSession(MapProject.CreateDefault("Preview", "Prefab preview"), mapFolder);
        SvJsonObject doc = ApiJson.Obj(
            ("format", ApiJson.Str(ChangeApplier.Format)),
            ("label", ApiJson.Str("Prefab preview")),
            ("ops", ApiJson.Arr([ApiJson.Obj(
                ("op", ApiJson.Str("prefab.place")),
                ("name", ApiJson.Str(name)),
                ("at", ApiJson.Vec(Vec3.Zero)))])));
        ChangeResult result = ChangeApplier.Apply(session, doc, writeFiles: false);
        problem = result.Ok ? null : (result.Json["error"] as SvJsonObject)?["message"] is SvJsonString m ? m.Value : "it does not expand";
        return result.Ok ? session.Project : null;
    }
}
