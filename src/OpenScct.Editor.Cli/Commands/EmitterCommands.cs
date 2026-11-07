using OpenScct.Content.Json;
using OpenScct.Editor.Core.Emitters;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

internal static class EmitterCommands
{
    private const string Usage = "emitters list | show <effect> | place <project> <effect> [--at x,y,z] " +
        "[--loop-min seconds] [--loop-max seconds] [--dry-run] | save <project> <name> <ids...> [--category name] | " +
        "import-pack <file.json> [--content folder]";

    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("emitters", Usage, "Lists, inspects, places and saves Emitter Library effects and imports RE+ effect packs. " +
            "Effect names and ids are accepted. Prints JSON.", Run),
    ];

    private static Task<int> Run(CliArgs a)
    {
        GeometryCliSupport.EnsureServices();
        string? root = a.Get("content") ?? EditorServices.ContentRoot;
        SvJsonNode result;
        switch (a.Positional(0))
        {
            case "list":
                var library = EmitterLibrary.Load(root);
                result = ApiJson.Obj(("effects", ApiJson.Arr(library.Presets.Select(p => Describe(p, details: false)))),
                    ("warnings", GeometryCliSupport.Array(library.Warnings)));
                break;
            case "show":
                result = Describe(Find(root, Required(a, 1, "Give an effect name or id.")), details: true);
                break;
            case "place":
                result = Place(a, root);
                break;
            case "save":
                result = Save(a, root);
                break;
            case "import-pack":
                string pack = EmitterLibrary.ImportPack(Required(a, 1, "Give the effect pack JSON file."), root);
                result = ApiJson.Obj(("pack", ApiJson.Str(pack)));
                break;
            default:
                throw new ArgumentException("Run 'ose emitters --help' to see the library commands.");
        }

        GeometryCliSupport.PrintJson(result, a);
        return Task.FromResult(0);
    }

    private static SvJsonNode Place(CliArgs a, string? root)
    {
        var session = EditorSession.Open(Required(a, 1, "Give the project folder."));
        EmitterPreset preset = Find(root, Required(a, 2, "Give an effect name or id."));
        Vec3 at = Vec3.Zero;
        if (a.Get("at") is { } text && !ApiJson.TryParseTriple(text, out at))
        {
            throw new ArgumentException("Use --at x,y,z for the placement point.");
        }

        double? Wait(string option) => a.Get(option) is { } value ? Parsing.Number(value, $"--{option}") : null;
        var placed = EmitterPlacement.Place(session.Project, preset, at, Wait("loop-min"), Wait("loop-max"));
        bool dryRun = a.Has("dry-run");
        if (!dryRun)
        {
            session.Commands.Execute(placed.Command);
            session.Save();
        }

        return ApiJson.Obj(("effect", ApiJson.Str(preset.Id)), ("dryRun", SvJsonNode.Bool(dryRun)),
            ("actors", ApiJson.Arr(placed.Actors.Select(actor => ModelJson.ActorToJson(actor)))));
    }

    private static SvJsonNode Save(CliArgs a, string? root)
    {
        var session = EditorSession.Open(Required(a, 1, "Give the project folder."));
        string name = Required(a, 2, "Give the new effect a name.");
        var actors = a.PositionalArgs.Skip(3).Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            Actor actor = session.Project.FindActor(id) ?? throw new ArgumentException($"No actor named '{id}'.");
            return EmitterPlacement.CanCapture(actor, session.Project, root) ? actor : throw new ArgumentException($"'{id}' has no particle systems.");
        });
        var preset = EmitterPlacement.Capture(name, a.Get("category") ?? "Other", actors, session.Project, root);
        return Describe(EmitterLibrary.Save(preset), details: false);
    }

    private static EmitterPreset Find(string? root, string id)
    {
        var presets = EmitterLibrary.Load(root).Presets;
        EmitterPreset? match = presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        EmitterPreset[] named = presets.Where(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return named.Length switch
        {
            1 => named[0],
            0 => throw new ArgumentException($"No effect named '{id}'. Run 'ose emitters list' for available effects."),
            _ => throw new ArgumentException($"More than one effect is named '{id}'. Use its id from 'ose emitters list'."),
        };
    }

    private static SvJsonNode Describe(EmitterPreset preset, bool details) => ApiJson.Obj(
        ("id", ApiJson.Str(preset.Id)), ("name", ApiJson.Str(preset.Name)), ("category", ApiJson.Str(preset.Category)),
        ("group", ApiJson.Str(preset.Group)), ("mode", ApiJson.Str(preset.Mode)),
        ("description", details ? ApiJson.Str(preset.Description) : null),
        ("sourceMap", preset.SourceMap is { } map ? ApiJson.Str(map) : null),
        ("unavailable", preset.UnavailableReason is { } reason ? ApiJson.Str(reason) : null),
        ("actors", details ? ApiJson.Arr(preset.Actors.Select(actor => ModelJson.ActorToJson(actor))) : null));

    private static string Required(CliArgs a, int index, string message) => a.Positional(index) ?? throw new ArgumentException(message);
}
