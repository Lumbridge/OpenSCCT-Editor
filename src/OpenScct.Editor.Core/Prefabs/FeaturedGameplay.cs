using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>Beginner-facing descriptions and prerequisites for the existing starter library.</summary>
public static class FeaturedGameplay
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["spawn_room"] = "A lit room with starting positions for one team and an exit doorway.",
        ["objective_computer"] = "A computer spies can hack to complete an objective. Its objective and activating Story mission are already connected.",
        ["door_switch"] = "A moving door and a switch that opens it. Each copy controls its own door.",
        ["camera_alarm"] = "A security camera that raises its own alarm when it spots an intruder.",
        ["laser_grid_alarm"] = "Laser beams that raise an alarm when a player crosses them.",
        ["vent_crawl"] = "A narrow passage spies can crawl through, with a grab point at a raised entrance.",
        ["ladder"] = "A climbable path up a wall. Add a ladder model if you want visible rungs.",
        ["zipline"] = "A line spies can slide down between two heights.",
    };
    public static readonly IReadOnlyDictionary<string, string> Conditions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["spawn_room"] = "Place beside your map and connect its doorway to a corridor. Choose 0 for spies or 1 for mercenaries. Starts must fit inside the room.",
        ["objective_computer"] = "Place on a reachable floor. Use Story mode to test hacking and objective completion. Requires the shared gameplay models.",
        ["door_switch"] = "Place in a wall between two open spaces, facing +X. Requires the door mesh and switch model. The switch is already connected to this door.",
        ["camera_alarm"] = "Place at the foot of a wall; the camera looks along +X. Requires the shared camera model. Its alarm connection is included.",
        ["laser_grid_alarm"] = "Place across an opening, with beams running along Y. Requires the shared laser model. Its alarm connection is included.",
        ["vent_crawl"] = "Place at a wall face with room on the far end. The vent runs along -X; a raised entrance includes a spy ledge grab.",
        ["ladder"] = "Place against a wall with a clear landing at the top. This supplies the climb interaction; choose a mesh for visible ladder rungs. Test it as a spy.",
        ["zipline"] = "Place with clear space along its full length and safe landings at both ends. Test the travel direction as a spy.",
    };

    /// <summary>Expand the actual parameters without touching the map, then check the same asset resolver used by the views.</summary>
    public static string? Problem(string name, SvJsonObject parameters, string? folder, AssetResolver resolver, IClassCatalog? classes)
    {
        var session = new EditorSession(MapProject.CreateDefault("Probe", "Prefab prerequisites"), folder);
        var op = ApiJson.Obj(("op", ApiJson.Str("prefab.place")), ("name", ApiJson.Str(name)), ("params", parameters), ("at", ApiJson.Vec(Vec3.Zero)));
        ChangeResult result = ChangeApplier.Apply(session, ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("ops", ApiJson.Arr([op]))), writeFiles: false);
        if (!result.Ok) return "This piece cannot be placed: " + ((result.Json["error"] as SvJsonObject)?["message"] as SvJsonString)?.Value;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor actor in session.Project.Actors)
        {
            SvJsonNode? node = actor.Props["StaticMesh"] ?? classes?.Default(actor.Class, "StaticMesh");
            string? mesh = node is SvJsonString s ? s.Value : (node as SvJsonObject)?["$asset"] is SvJsonString asset ? asset.Value : null;
            if (mesh is { Length: > 0 } && mesh != "None" && resolver.FindViewMesh(mesh) is null) missing.Add(mesh);
        }
        if (missing.Count > 0) return "Install the content containing these models before placing: " + string.Join(", ", missing.Take(4)) + ". Use Get content packs.";
        if (name is "objective_computer" or "camera_alarm" or "laser_grid_alarm" &&
            (EditorServices.ContentRoot is not { } root || !File.Exists(Path.Combine(root, "shared", "classes.json"))))
            return "Install the shared content pack before placing this gameplay piece.";
        return null;
    }
}
