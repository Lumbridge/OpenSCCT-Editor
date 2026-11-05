using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose identity</c> (the map's name, dossier, players, tags, places and picture recipes) and <c>ose
/// mapshot</c> (renders the camera pictures with OpenSCCT). Edits go through the script's <c>identity</c> op, so they
/// are checked the same way, and are saved.</summary>
public static class IdentityCommands
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    /// <summary>The <c>set</c> keys and the identity op fields they fill.</summary>
    private static readonly IReadOnlyList<string> s_setKeys =
        ["name", "family", "variant", "description", "when", "where", "summary", "authors", "version", "language", "players", "tags", "floors"];

    [CliProvider]
    public static IEnumerable<CliCommand> Commands() =>
    [
        new("identity",
            "identity <project.osmap> [set key=value ...] | [place <n> [name] [--at x,y,z] [--floor n] [--delete]] | " +
            "[text <lang> [name|description|when|where|summary|place.<n>=value ...] [--delete]] | " +
            "[picture <menu|loading-spy|loading-merc|briefing|banner> file <image> [--focus x,y] [--link] | camera <bookmark> " +
            "[--look plain|nightvision|thermal] [--fov 80] | plan | none] [--replace]",
            "Prints or edits the map's identity and saves it. set keys: " + string.Join(", ", s_setKeys) + " (authors and tags are comma " +
            "lists, floors is auto or comma heights, an empty value clears). text edits the map's translation into a language " +
            "(OpenSCCT shows " + string.Join(", ", ProjectIdentity.GameLanguages) + "): its name, description, dossier lines and " +
            "place names, each as long as the map's own may be; an empty value falls back to the map's own text, --delete removes " +
            "the translation. A picture file (PNG, JPEG, BMP or WebP) is copied into the project's pictures/ " +
            "unless --link, and the build crops it to the slot's shape around --focus; loading pictures are added to the side's " +
            "list unless --replace.",
            IdentityAsync),
        new("mapshot", "mapshot <project.osmap> [--only menu,loading-spy-1] [--force] [--exe <OpenSCCT.exe>] [--out <folder>] [--fast] [--no-classes] [--dry-run]",
            "Builds the project, renders its camera pictures with OpenSCCT in a background window (a linked copy of the game " +
            "started minimised beside a background override.cfg, so it never takes the focus) into the project's " +
            ".cache/pictures, then builds again so the map carries them. Pictures already rendered from this version of the map " +
            "are kept unless --force; --dry-run lists each picture's state (missing, stale or current).",
            MapShotAsync),
    ];

    private static Task<int> IdentityAsync(CliArgs a)
    {
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        EditorServices.Discover(typeof(IdentityCommands).Assembly);
        EditorSession session = EditorSession.Open(folder);
        string? verb = a.Positional(1);
        if (verb is null)
        {
            Console.WriteLine(ApiJson.Write(MapQuery.IdentityOf(session.Project)));
            return Task.FromResult(0);
        }

        SvJsonObject op = verb switch
        {
            "set" => SetOp(a, session.Project.Identity.Dossier),
            "place" => PlaceOp(a),
            "picture" => PictureOp(a, session),
            "text" => TextOp(a, session.Project.Identity),
            _ => throw new ArgumentException($"unknown identity command '{verb}' (set, place, text or picture)"),
        };
        var doc = new SvJsonObject();
        doc.Add("format", SvJsonNode.String(ChangeApplier.Format));
        doc.Add("label", SvJsonNode.String("Map identity"));
        doc.Add("ops", new SvJsonArray([op]));
        ChangeResult result = ChangeApplier.Apply(session, doc, author: a.Get("author") ?? "ose");
        if (!result.Ok)
        {
            Console.WriteLine(ApiJson.Write(result.Json));
            return Task.FromResult(1);
        }

        session.Save();
        var reply = new SvJsonObject();
        reply.Add("ok", SvJsonNode.True);
        reply.Add("saved", SvJsonNode.String(session.Folder!));
        reply.Add("identity", MapQuery.IdentityOf(session.Project));
        Console.WriteLine(ApiJson.Write(reply));
        return Task.FromResult(0);
    }

    private static SvJsonObject SetOp(CliArgs a, Dossier current)
    {
        var op = Op();
        var dossier = new SvJsonObject();
        foreach (string pair in a.PositionalArgs.Skip(2))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = eq > 0 ? pair[..eq] : throw new ArgumentException($"'{pair}' is not key=value");
            string value = pair[(eq + 1)..];
            SvJsonNode text = value.Length == 0 ? SvJsonNode.Null : SvJsonNode.String(value);
            switch (key)
            {
                case "when" or "where" or "summary":
                    dossier.Add(key, text);
                    break;
                case "authors" or "tags":
                    op.Add(key, new SvJsonArray(List(value).Select(SvJsonNode.String)));
                    break;
                case "players":
                    op.Add(key, value.Length == 0 ? SvJsonNode.Null : SvJsonNode.Integer(int.Parse(value, CultureInfo.InvariantCulture)));
                    break;
                case "floors":
                    op.Add(key, value is "" or "auto" ? SvJsonNode.String("auto")
                        : new SvJsonArray(List(value).Select(z => SvJsonNode.Float(float.Parse(z, CultureInfo.InvariantCulture)))));
                    break;
                case var k when s_setKeys.Contains(k):
                    op.Add(k, text);
                    break;
                default:
                    throw new ArgumentException($"unknown identity key '{key}' (one of {string.Join(", ", s_setKeys)})");
            }
        }

        if (dossier.Count > 0)
        {
            // The op sets the dossier whole: keep the lines not given.
            foreach ((string key, string line) in new[] { ("when", current.When), ("where", current.Where), ("summary", current.Summary) })
            {
                dossier.TryAdd(key, SvJsonNode.String(line));
            }

            op.Add("dossier", dossier);
        }

        return op;
    }

    /// <summary>The identity op that sets one translation: the current one with the given texts changed.</summary>
    private static SvJsonObject TextOp(CliArgs a, ProjectIdentity identity)
    {
        string lang = a.Positional(2) ?? throw new ArgumentException("give the language (fr, de, es...)");
        IdentityText text = a.Has("delete") ? new IdentityText()
            : identity.Texts.TryGetValue(lang, out IdentityText? current) ? current.Clone() : new IdentityText();
        foreach (string pair in a.PositionalArgs.Skip(3))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = eq > 0 ? pair[..eq] : throw new ArgumentException($"'{pair}' is not key=value");
            string value = pair[(eq + 1)..].Trim();
            switch (key)
            {
                case "name":
                    text.Name = value;
                    break;
                case "description":
                    text.Description = value;
                    break;
                case "when":
                    text.Dossier.When = value;
                    break;
                case "where":
                    text.Dossier.Where = value;
                    break;
                case "summary":
                    text.Dossier.Summary = value;
                    break;
                case var k when k.StartsWith("place.", StringComparison.Ordinal)
                    && int.TryParse(k["place.".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number):
                    text.Places[number] = value;
                    break;
                default:
                    throw new ArgumentException($"unknown text key '{key}' (name, description, when, where, summary or place.<n>)");
            }
        }

        var texts = new SvJsonObject();
        texts.Add(lang, text.IsEmpty ? SvJsonNode.Null : IdentityJson.TextToJson(text));
        SvJsonObject op = Op();
        op.Add("texts", texts);
        return op;
    }

    private static SvJsonObject PlaceOp(CliArgs a)
    {
        string number = a.Positional(2) ?? throw new ArgumentException("give the place number");
        var place = new SvJsonObject();
        place.Add("number", SvJsonNode.Integer(int.Parse(number, CultureInfo.InvariantCulture)));
        if (a.Positional(3) is { } name)
        {
            place.Add("name", SvJsonNode.String(name));
        }

        if (a.Get("at") is { } at)
        {
            place.Add("at", SvJsonNode.String(at));
        }

        if (a.Get("floor") is { } floor)
        {
            place.Add("floor", floor is "" or "auto" ? SvJsonNode.Null : SvJsonNode.Integer(int.Parse(floor, CultureInfo.InvariantCulture)));
        }

        if (a.Has("delete"))
        {
            place.Add("delete", SvJsonNode.True);
        }

        SvJsonObject op = Op();
        op.Add("place", place);
        return op;
    }

    private static SvJsonObject PictureOp(CliArgs a, EditorSession session)
    {
        string slot = a.Positional(2) ?? throw new ArgumentException("give the picture slot (menu, loading-spy, loading-merc, briefing or banner)");
        string from = a.Positional(3) ?? throw new ArgumentException("give the source: file <image>, camera <bookmark>, plan or none");
        PictureRecipe? recipe = from switch
        {
            "file" => PictureRecipe.OfFile(FileOf(a, session), Focus(a).X, Focus(a).Y),
            "camera" => PictureRecipe.OfCamera(a.Positional(4) ?? throw new ArgumentException("give the bookmark name"),
                a.Get("look") ?? (slot == "loading-spy" ? PictureRecipe.NightVisionLook : PictureRecipe.PlainLook),
                double.Parse(a.Get("fov") ?? "80", CultureInfo.InvariantCulture)),
            "plan" => PictureRecipe.OfPlan(),
            "none" => null,
            _ => throw new ArgumentException($"unknown picture source '{from}' (file, camera, plan or none)"),
        };

        var pictures = new SvJsonObject();
        IdentityPictures current = session.Project.Identity.Pictures;
        switch (slot)
        {
            case "menu" or "briefing" or "banner":
                pictures.Add(slot, recipe is null ? SvJsonNode.Null : IdentityJson.RecipeToJson(recipe));
                break;
            case "loading-spy" or "loading-merc":
                bool spy = slot == "loading-spy";
                List<PictureRecipe> list = a.Has("replace") || recipe is null ? [] : (spy ? current.LoadingSpy : current.LoadingMerc).ToList();
                if (recipe is not null)
                {
                    list.Add(recipe);
                }

                var loading = new SvJsonObject();
                loading.Add(spy ? "spy" : "merc", new SvJsonArray(list.Select(IdentityJson.RecipeToJson)));
                pictures.Add("loading", loading);
                break;
            default:
                throw new ArgumentException($"unknown picture slot '{slot}' (menu, loading-spy, loading-merc, briefing or banner)");
        }

        SvJsonObject op = Op();
        op.Add("pictures", pictures);
        return op;
    }

    /// <summary>The recipe's file: copied into the project's pictures/ folder, or the full path with --link. A file the
    /// build could not read (not PNG, JPEG, BMP or WebP, or broken) is refused here.</summary>
    private static string FileOf(CliArgs a, EditorSession session)
    {
        string file = Path.GetFullPath(a.Positional(4) ?? throw new ArgumentException("give the image file"));
        if (!File.Exists(file))
        {
            throw new ArgumentException($"{file} does not exist");
        }

        try
        {
            RgbaImage.Decode(File.ReadAllBytes(file));
        }
        catch (InvalidDataException e)
        {
            throw new ArgumentException($"{Path.GetFileName(file)} cannot be used: {e.Message}");
        }

        return a.Has("link") ? file : ProjectPictures.Import(session.Folder!, file);
    }

    private static (double X, double Y) Focus(CliArgs a)
    {
        if (a.Get("focus") is not { } focus)
        {
            return (0.5, 0.5);
        }

        string[] xy = focus.Split(',');
        return xy.Length == 2 ? (double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture))
            : throw new ArgumentException("--focus is x,y (0..1 across and down)");
    }

    private static async Task<int> MapShotAsync(CliArgs a)
    {
        (MapProject project, CompileResult build) = BuildCommands.Compile(a);
        if (!build.Ok)
        {
            Console.WriteLine(BuildCommands.ResultJson(build, project.Id).ToJsonString(s_compact));
            return 1;
        }

        string projectFolder = Path.GetFullPath(a.Positional(0)!);
        List<CameraShot> shots = MapShots.CameraSlots(project, out List<string> missing);
        if (a.Get("only") is { } only)
        {
            HashSet<string> wanted = List(only).ToHashSet(StringComparer.Ordinal);
            shots = shots.Where(s => wanted.Contains(s.Slot)).ToList();
        }

        var json = new JsonObject
        {
            ["map"] = project.Id,
            ["out"] = build.OutputFolder,
            ["missingBookmarks"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()),
        };
        string? exe = GameExe.Find(a.Get("exe"));
        if (exe is not null && shots.Count > 0 && !a.Has("dry-run") && GameExe.TooOld(exe) is { } tooOld)
        {
            Console.Error.WriteLine("ose mapshot: " + tooOld);
            return 1;
        }

        if (shots.Count == 0 || a.Has("dry-run") || exe is null)
        {
            json["ok"] = shots.Count == 0 || a.Has("dry-run");
            json["exe"] = exe;
            string mapKey = MapShots.MapKey(build.OutputFolder);
            json["mapKey"] = mapKey;
            json["shots"] = new JsonArray(shots.Select(s => (JsonNode)new JsonObject
            {
                ["slot"] = s.Slot,
                ["bookmark"] = s.Bookmark.Name,
                ["look"] = s.Recipe.Look,
                ["key"] = s.Key,
                ["state"] = MapShots.StateOf(projectFolder, s.Key, mapKey).ToString().ToLowerInvariant(),
            }).ToArray());
            if (exe is null && shots.Count > 0 && !a.Has("dry-run"))
            {
                json["error"] = "OpenSCCT.exe not found; give --exe, set OSE_GAME_EXE (or OPENSCCT_EXE), or install OpenSCCT with its launcher";
            }

            Console.WriteLine(json.ToJsonString(s_compact));
            return json["ok"]!.GetValue<bool>() ? 0 : 1;
        }

        List<MapShotResult> results = await MapShots.RenderAsync(project, projectFolder, build.OutputFolder, shots, exe, a.Has("force"),
            line => Console.Error.WriteLine("ose mapshot: " + line));
        bool ok = results.All(r => r.Ok);
        (_, CompileResult rebuilt) = BuildCommands.Compile(a);
        json["ok"] = ok && rebuilt.Ok;
        json["exe"] = exe;
        json["shots"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
        {
            ["slot"] = r.Slot,
            ["ok"] = r.Ok,
            ["cached"] = r.Cached,
            ["file"] = r.File,
            ["error"] = r.Error,
        }).ToArray());
        json["build"] = BuildCommands.ResultJson(rebuilt, project.Id);
        Console.WriteLine(json.ToJsonString(s_compact));
        return json["ok"]!.GetValue<bool>() ? 0 : 1;
    }

    private static SvJsonObject Op()
    {
        var op = new SvJsonObject();
        op.Add("op", SvJsonNode.String("identity"));
        return op;
    }

    private static IEnumerable<string> List(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
