using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary><c>ui/mapinfo.json</c> (the briefing text, its legends from the map's identity) and <c>audio/audio.json</c> (svm.mapaudio/1: sound actors,
/// zones and the portal distances between zones). A baked project keeps its source map's other ui and audio files, and
/// its audio.json with the sound actors following the project.</summary>
public static class SideFiles
{
    public static void Write(CompileContext ctx)
    {
        if (ctx.Baked is { } baked)
        {
            CopyFolder(ctx, baked.Folder, "ui");
            CopyFolder(ctx, baked.Folder, "audio");
        }

        WriteMapInfo(ctx);
        if (ctx.Baked is { } source && ctx.Files.TryGetValue("audio/audio.json", out byte[]? kept) && JsonNode.Parse(kept) is JsonObject audio)
        {
            FollowAudio(ctx, source, audio);
        }
        else
        {
            WriteAudio(ctx);
        }
    }

    /// <summary>The source's audio.json without the records of deleted actors and with moved ones at their new place,
    /// plus records for the sound actors added in the editor. Other record values stay as the converter wrote them.</summary>
    private static void FollowAudio(CompileContext ctx, BakedSource baked, JsonObject audio)
    {
        var project = ctx.Render.Concat(ctx.Gameplay).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var actors = new JsonArray();
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? r in audio["actors"] as JsonArray ?? [])
        {
            if (r?["id"]?.GetValue<string>() is not { } id || !project.TryGetValue(id, out Actor? a))
            {
                continue;
            }

            JsonObject record = r.DeepClone().AsObject();
            static float At(JsonNode? o, string axis) => o is JsonObject j && j[axis] is JsonValue v && v.TryGetValue(out double d) ? (float)d : float.NaN;
            JsonNode? at = record["location"];
            if (At(at, "X") != (float)a.Location.X || At(at, "Y") != (float)a.Location.Y || At(at, "Z") != (float)a.Location.Z)
            {
                record["location"] = new JsonObject { ["X"] = (float)a.Location.X, ["Y"] = (float)a.Location.Y, ["Z"] = (float)a.Location.Z };
                record["zoneNumber"] = ctx.ZoneAt(a.Location);
            }

            actors.Add(record);
            kept.Add(id);
        }

        foreach (JsonObject r in SoundActors(ctx))
        {
            string id = r["id"]!.GetValue<string>();
            if (!kept.Contains(id) && !baked.Actors.Contains(id))
            {
                actors.Add(r);
            }
        }

        audio["actors"] = actors;
        ctx.Files["audio/audio.json"] = RenderBuild.Json(audio);
        ctx.Count("audio.actors", actors.Count);
    }

    /// <summary>A record per actor with an AmbientSound (zones have theirs in the zone list).</summary>
    private static IEnumerable<JsonObject> SoundActors(CompileContext ctx)
    {
        foreach (Actor a in ctx.Render.Concat(ctx.Gameplay))
        {
            if (PropValues.AssetPath(a.Props["AmbientSound"]) is not { } sound || ctx.Rules.IsZoneActor(a))
            {
                continue;
            }

            yield return new JsonObject
            {
                ["ambientSound"] = sound,
                ["category"] = ctx.GameplayIds.Contains(a.Id) ? "gameplay" : "render",
                ["class"] = a.Class,
                ["id"] = a.Id,
                ["location"] = new JsonObject { ["X"] = (float)a.Location.X, ["Y"] = (float)a.Location.Y, ["Z"] = (float)a.Location.Z },
                ["soundPitch"] = (int)ctx.Rules.Number(a, "SoundPitch", 64),
                ["soundRadius"] = (float)ctx.Rules.Number(a, "SoundRadius", 64),
                ["soundVolume"] = (int)ctx.Rules.Number(a, "SoundVolume", 128),
                ["zoneNumber"] = ctx.ZoneAt(a.Location),
            };
        }
    }

    private static void CopyFolder(CompileContext ctx, string root, string group)
    {
        string dir = Path.Combine(root, group);
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ctx.Files[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }
    }

    private static void WriteMapInfo(CompileContext ctx)
    {
        if (ctx.Baked is not null && ctx.Project.MapInfo.Count == 0 && ctx.Files.ContainsKey("ui/mapinfo.json"))
        {
            return;
        }

        // The identity is the source of the legends when it has them; else the strings stay as stored.
        PropSet stored = ctx.Project.MapInfo.Clone();
        ProjectIdentity identity = ctx.Project.Identity;
        if (!identity.Dossier.IsEmpty)
        {
            stored.Set(LegendMap, SvJsonNode.String(LegendOf(identity.Dossier)));
        }

        if (identity.Places.Count > 0)
        {
            stored.Set(LegendMapInBrief, SvJsonNode.String(LegendOf(identity.Places)));
        }

        SvJsonObject info = stored.ToJson();
        var obj = new SvJsonObject();
        foreach ((string k, SvJsonNode v) in info)
        {
            obj.Add(k, v);
        }

        if (!obj.ContainsKey("MapName"))
        {
            obj.Add("MapName", SvJsonNode.String(ctx.Project.Title.ToUpperInvariant()));
        }

        if (!obj.ContainsKey("modes"))
        {
            obj.Add("modes", new SvJsonObject());
        }

        ctx.Files["ui/mapinfo.json"] = CanonicalJson.ToFileBytes(obj);
    }

    public const string LegendMap = "LegendMap", LegendMapInBrief = "LegendMapInBrief";

    /// <summary><c>LegendMap</c> as the stock maps write it: "when|where||summary" (<c>|</c> breaks a line, so the
    /// summary's own line breaks are written as <c>|</c>).</summary>
    public static string LegendOf(Dossier d) => $"{d.When}|{d.Where}||{d.Summary.Replace('\n', '|')}";

    /// <summary><c>LegendMapInBrief</c> as the stock maps write it: "1. Server Room||2. Loading Bay".</summary>
    public static string LegendOf(IEnumerable<Place> places) =>
        string.Join("||", places.OrderBy(p => p.Number).Select(p => $"{p.Number}. {p.Name}"));

    /// <summary>The dossier in a stored <c>LegendMap</c>, or null when it does not have the stock shape. Lines after the
    /// summary's first stay in the summary as line breaks, so <see cref="LegendOf(Dossier)"/> writes the legend back
    /// as it was.</summary>
    public static Dossier? DossierOf(string? legend)
    {
        string[] parts = (legend ?? "").Split('|');
        return parts.Length >= 4 && parts[2].Length == 0
            ? new Dossier { When = parts[0], Where = parts[1], Summary = string.Join('\n', parts.Skip(3)) }
            : null;
    }

    /// <summary>The places named in a stored <c>LegendMapInBrief</c> ("1. Name||2. Name"), without positions. A legend
    /// with any other text gives none, so the build keeps it as stored rather than rewriting it from the places.</summary>
    public static List<Place> PlacesOf(string? legend)
    {
        var places = new List<Place>();
        foreach (string item in (legend ?? "").Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            int dot = item.IndexOf(". ", StringComparison.Ordinal);
            if (dot <= 0 || !int.TryParse(item.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                || n is < 1 or > 99 || places.Any(p => p.Number == n))
            {
                return [];
            }

            places.Add(new Place { Number = n, Name = item[(dot + 2)..].Trim() });
        }

        return places;
    }

    private static void WriteAudio(CompileContext ctx)
    {
        var actors = new JsonArray(SoundActors(ctx).ToArray<JsonNode?>());

        var zones = new JsonArray { new JsonObject { ["class"] = ctx.LevelInfoClass, ["id"] = ctx.LevelInfoId } };
        for (int z = 1; z < ctx.ZoneActors.Count; z++)
        {
            if (ctx.ZoneActors[z] is not { } id || ctx.Gameplay.Find(a => a.Id == id) is not { } zone)
            {
                continue;
            }

            var entry = new JsonObject { ["class"] = zone.Class, ["id"] = id };
            if (PropValues.AssetPath(zone.Props["AmbientSound"]) is { } ambient)
            {
                entry["ambientSound"] = ambient;
                entry["soundPitch"] = (int)ctx.Rules.Number(zone, "SoundPitch", 64);
                entry["soundRadius"] = (float)ctx.Rules.Number(zone, "SoundRadius", 64);
                entry["soundVolume"] = (int)ctx.Rules.Number(zone, "SoundVolume", 128);
            }

            zones.Add(entry);
        }

        var dist = new JsonArray();
        byte[,] d = ctx.World.Zones.Distance;
        int n = ctx.Baked is { KeepCollision: true } ? 0 : d.GetLength(0);
        for (int i = 0; i < n; i++)
        {
            var row = new JsonArray();
            for (int j = 0; j < n; j++)
            {
                row.Add((int)d[i, j]);
            }

            dist.Add(row);
        }

        var root = new JsonObject
        {
            ["actors"] = actors,
            ["bank"] = null,
            ["format"] = "svm.mapaudio/1",
            ["map"] = ctx.MapId,
            ["missing"] = new JsonObject { ["sounds"] = new JsonObject(), ["streams"] = new JsonArray() },
            ["sounds"] = new JsonObject(),
            ["streams"] = new JsonArray(),
            ["zoneDist"] = dist,
            ["zones"] = zones,
        };
        ctx.Files["audio/audio.json"] = RenderBuild.Json(root);
        ctx.Count("audio.actors", actors.Count);
    }
}
