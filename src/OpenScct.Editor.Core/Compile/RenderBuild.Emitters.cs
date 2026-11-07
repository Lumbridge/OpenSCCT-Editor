using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

public sealed partial class RenderBuild
{
    // In a baked map, leave the converter's other actor properties intact, especially on its native SDC overlay.
    private static readonly string[] s_emitterOwnerProps = ["Tag", "AutoDestroy", "AutoReset", "HeatOpacity", "bUnlit", "AmbientGlow"];

    private void WriteEmitters(BakedSource? baked)
    {
        const string file = "render/fx.json";
        var actors = _ctx.Render.Concat(_ctx.Gameplay).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        JsonObject fx = _ctx.Files.TryGetValue(file, out byte[]? bytes) && JsonNode.Parse(bytes) is JsonObject source
            ? source
            : new JsonObject
            {
                ["patches"] = new JsonArray(),
                ["projectors"] = new JsonArray(),
                ["rain"] = new JsonArray(),
                ["spawnParticles"] = new JsonObject(),
            };
        if (fx["emitters"] is not JsonObject emitters)
        {
            emitters = new JsonObject();
            fx["emitters"] = emitters;
        }

        foreach (string removed in emitters.Select(kv => kv.Key).Where(id => !actors.ContainsKey(id)).ToList())
        {
            emitters.Remove(removed);
        }

        T3dTypes? types = null;
        foreach (Actor actor in actors.Values)
        {
            SvJsonArray? systems = EmitterBuild.SystemsOf(actor, _ctx.Project);
            if (systems is null && actor.Source?.Contains(T3dImport.ObjectsKey, StringComparison.Ordinal) == true)
            {
                types ??= new T3dTypes(UeSchema.Load(_ctx.ContentRoot), _ctx.MapId);
                systems = T3dImport.ParticleSystemsOf(actor, types);
            }

            if (systems is null || EmitterBuild.Record(actor, systems) is not { } authored)
            {
                continue;
            }

            var original = emitters[actor.Id] as JsonObject;
            authored["gameplay"] = _ctx.GameplayIds.Contains(actor.Id);
            var actorProps = authored["props"]!.AsObject();
            actorProps["Tag"] = GameplayBuild.EffectiveTag(_ctx, actor);
            JsonArray parts = authored["emitters"]!.AsArray();
            JsonArray? originalParts = original?["emitters"] as JsonArray;
            int index = 0;
            foreach (SvJsonObject system in EmitterBuild.ValidSystems(systems))
            {
                JsonNode part = parts[index]!;
                if (originalParts is not null && index < originalParts.Count &&
                    originalParts[index] is JsonObject originalPart && SamePart(originalPart, part, system))
                {
                    parts[index++] = originalPart.DeepClone();
                    continue;
                }

                index++;
                var props = (SvJsonObject)system["props"]!;
                if (EmitterBuild.TextureOf(_ctx.Library, _ctx.ContentRoot, system) is { } texture)
                {
                    part["texture"] = CopyTexture(texture.RenderDir, texture.Origin, texture.Relative);
                }
                else if (PropValues.AssetPath(props["Texture"]) is { } asset)
                {
                    _ctx.Warn("render.emitter-texture", $"actor {actor.Id}: {_ctx.NothingShips($"particle texture {asset}")}", ObjectRef.Actor(actor.Id));
                }

                if (PropValues.AssetPath(props["StaticMesh"]) is { } mesh && !AddParticleMesh(mesh))
                {
                    _ctx.Warn("render.emitter-mesh", $"actor {actor.Id}: {_ctx.NothingShips($"particle mesh {mesh}")}", ObjectRef.Actor(actor.Id));
                }
            }

            if (original is null)
            {
                emitters[actor.Id] = authored;
            }
            else
            {
                FollowBakedEmitter(original, authored);
            }
        }

        _ctx.Count("render.emitters", emitters.Count);
        if (baked is null || bytes is not null || emitters.Count > 0)
        {
            _ctx.Files[file] = Json(fx);
        }
    }

    private static bool SamePart(JsonObject original, JsonNode current, SvJsonObject system) =>
        JsonNode.DeepEquals(original["class"], current["class"]) &&
        JsonNode.DeepEquals(original["object"], current["object"]) &&
        JsonNode.DeepEquals(original["props"], current["props"]) &&
        original["texture"]?.GetValue<string>() == EmitterBuild.Text(system["texture"]);

    private static void FollowBakedEmitter(JsonObject original, JsonObject authored)
    {
        foreach (string key in new[] { "class", "gameplay", "emitters" })
        {
            if (!JsonNode.DeepEquals(original[key], authored[key]))
            {
                original[key] = authored[key]?.DeepClone();
            }
        }

        JsonObject newProps = authored["props"]!.AsObject();
        if (original["props"] is not JsonObject oldProps)
        {
            original["props"] = newProps.DeepClone();
            return;
        }

        foreach (string key in InteropValues.TransformProps.Concat(s_emitterOwnerProps))
        {
            JsonNode? current = newProps[key];
            JsonNode? before = oldProps[key];
            if (SameActorProperty(key, before, current))
            {
                continue;
            }

            if (current is null)
            {
                oldProps.Remove(key);
            }
            else
            {
                oldProps[key] = current.DeepClone();
            }
        }
    }

    private static bool SameActorProperty(string key, JsonNode? before, JsonNode? current)
    {
        if (JsonNode.DeepEquals(before, current))
        {
            return true;
        }

        // Missing transform components use their native defaults, as FxDoc does when reading a placed emitter.
        if (current is JsonObject vector && InteropValues.TransformProps.Contains(key))
        {
            float defaultValue = key == "DrawScale3D" ? 1f : 0f;
            return vector.All(component => Number((before as JsonObject)?[component.Key], defaultValue) == Number(component.Value, defaultValue));
        }

        return key switch
        {
            "DrawScale" => Number(before, 1f) == Number(current, 1f),
            "Tag" => (before?.GetValue<string>() ?? "Emitter") == (current?.GetValue<string>() ?? "Emitter"),
            _ => false,
        };

        static float Number(JsonNode? value, float fallback) =>
            value is JsonValue v && v.TryGetValue(out double number) ? (float)number : fallback;
    }

    private bool AddParticleMesh(string asset)
    {
        JsonArray nodes = _glb.Array("nodes");
        JsonObject? FindNode(string name) => nodes.OfType<JsonObject>().FirstOrDefault(node =>
            node["name"]?.GetValue<string>() is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            node["extras"]?["svmap"]?["role"]?.GetValue<string>() == "libraryMesh");
        JsonObject? existing = FindNode(asset);
        if (existing is null)
        {
            if (ImportMesh(asset) is not { } imported || FindNode(imported) is not { } source)
            {
                return false;
            }

            existing = source;
            if (!asset.Equals(imported, StringComparison.OrdinalIgnoreCase))
            {
                existing = source.DeepClone().AsObject();
                existing["name"] = asset;
                _library.Add(_glb.AddNode(existing));
            }
        }

        existing["extras"]!["svmap"]!["particle"] = true;
        return true;
    }
}
