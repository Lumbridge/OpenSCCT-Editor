using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Emitters;

internal static class StockEmitters
{
    private const string Resource = "OpenScct.Editor.Core.Emitters.StockEmitters.json";
    private static readonly Lazy<SvJsonArray> s_specs = new(ReadSpecs);

    public static IEnumerable<EmitterPreset> Load(string? contentRoot, UeSchema? schema)
    {
        var maps = new Dictionary<string, SvJsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (SvJsonObject spec in s_specs.Value.OfType<SvJsonObject>())
        {
            var source = (SvJsonObject)spec["source"]!;
            string map = EmitterLibrary.Required(source, "map");
            if (!maps.TryGetValue(map, out SvJsonObject? records))
            {
                records = ReadMap(contentRoot, map);
                maps.Add(map, records);
            }

            var preset = new EmitterPreset(EmitterLibrary.Required(spec, "id"), EmitterLibrary.Required(spec, "name"),
                EmitterLibrary.Required(spec, "category"), EmitterLibrary.Required(spec, "description"),
                EmitterLibrary.Required(spec, "mode"), [], SourceMap: map);
            string[] ids = ((SvJsonArray)source["actors"]!).OfType<SvJsonString>().Select(x => x.Value).ToArray();
            if (records is null || ids.Any(id => records[id] is not SvJsonObject record ||
                record["class"] is not SvJsonString || record["emitters"] is not SvJsonArray { Count: > 0 } parts ||
                parts.Any(part => part is not SvJsonObject system || system["class"] is not SvJsonString || system["props"] is not SvJsonObject)))
            {
                yield return preset with { UnavailableReason = $"This effect requires the full {map} source map pack." };
                continue;
            }

            var actors = ids.Select(id => ReadActor(id, (SvJsonObject)records[id]!)).ToArray();
            var pivot = new Vec3(
                (actors.Min(a => a.Location.X) + actors.Max(a => a.Location.X)) / 2,
                (actors.Min(a => a.Location.Y) + actors.Max(a => a.Location.Y)) / 2,
                (actors.Min(a => a.Location.Z) + actors.Max(a => a.Location.Z)) / 2);
            var tweaks = ((SvJsonArray)spec["tweaks"]!).OfType<SvJsonObject>().ToArray();
            var types = new T3dTypes(schema, map);
            for (int i = 0; i < actors.Length; i++)
            {
                Actor actor = actors[i];
                actor.Location -= pivot;
                var parts = (SvJsonArray)((SvJsonObject)records[actor.Id]!)["emitters"]!;
                IEnumerable<SvJsonObject> actorTweaks = tweaks.Where(t =>
                    t["actor"] is SvJsonString id ? id.Value == actor.Id : i == 0 || Op(t) is not ("set" or "remove" or "tag"));
                var systems = new SvJsonArray(parts.Select((part, index) => NormalizePart((SvJsonObject)part, index, map, types, actorTweaks)));
                foreach (SvJsonObject tweak in actorTweaks)
                {
                    ApplyActorTweak(actor, tweak);
                }

                EmitterBuild.WithSystems(actor, systems);
            }

            yield return preset with { Actors = actors };
        }
    }

    private static Actor ReadActor(string id, SvJsonObject record)
    {
        var actor = new Actor(id, EmitterLibrary.Required(record, "class"));
        PropSet props = PropSet.FromJson(record["props"]);
        InteropValues.TakeTransform(props, actor);
        foreach (string key in new[] { "Emitters", "Level", "Region", "bMustInitNetChannels", "Tag", "Group", "Platform",
            "AttachTag", "Base", "ForcedVisibilityZoneTag" })
        {
            props.Remove(key);
        }

        actor.Props = props;
        actor.Editor.Folder = SvMapImport.EmittersFolder;
        return actor;
    }

    private static void ApplyActorTweak(Actor actor, SvJsonObject tweak)
    {
        switch (Op(tweak))
        {
            case "offset":
                actor.Location += ModelJson.ReadVec(tweak["by"]);
                break;
            case "tag":
                actor.Props.Set("Tag", tweak["value"]!);
                break;
            case "actorremove":
                actor.Props.Remove(EmitterLibrary.Required(tweak, "property"));
                break;
        }
    }

    private static SvJsonObject NormalizePart(SvJsonObject part, int index, string map, T3dTypes types, IEnumerable<SvJsonObject> tweaks)
    {
        var props = PropSet.FromJson(part["props"]);
        string cls = EmitterLibrary.Required(part, "class");
        foreach (SvJsonObject tweak in tweaks)
        {
            string op = Op(tweak);
            if (op == "texture" && PropValues.AssetPath(props["Texture"]) is { } original &&
                string.Equals(original, EmitterLibrary.Required(tweak, "from"), StringComparison.OrdinalIgnoreCase))
            {
                // Keep the source sprite already converted with the map. Original exports use the RE+ package reference.
                props.Set("Texture", PropValues.AssetRef(EmitterLibrary.Required(tweak, "to")));
            }
            else if ((op is "set" or "remove") && tweak["emitter"] is SvJsonNumber n && PropValues.Num(n) == index)
            {
                string property = EmitterLibrary.Required(tweak, "property");
                if (op == "remove")
                {
                    props.Remove(property);
                }
                else if (T3dValueParser.ParseField(property + "=" + EmitterLibrary.Required(tweak, "value")) is { } field)
                {
                    foreach ((string key, SvJsonNode value) in types.ToProps(cls, [field]))
                    {
                        props.Set(key, value);
                    }
                }
            }
        }

        var result = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in part)
        {
            result.Add(key, key == "props" ? props.ToJson() : value);
        }

        result.Add(EmitterBuild.TextureOriginKey, SvJsonNode.String(map));
        return result;
    }

    private static string Op(SvJsonObject tweak) => EmitterLibrary.Required(tweak, "op");

    private static SvJsonObject? ReadMap(string? root, string map)
    {
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, "svmap", map, "render", "fx.json");
        try
        {
            return File.Exists(path) && SvJson.Parse(File.ReadAllBytes(path), path) is SvJsonObject fx
                ? fx["emitters"] as SvJsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SvMapFormatException)
        {
            return null;
        }
    }

    private static SvJsonArray ReadSpecs()
    {
        using Stream source = typeof(StockEmitters).Assembly.GetManifestResourceStream(Resource)!;
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return (SvJsonArray)SvJson.Parse(bytes.ToArray(), Resource);
    }
}
