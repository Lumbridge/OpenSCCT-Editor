using System.Globalization;
using System.Text.Json;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;

namespace OpenScct.Editor.Cli.Commands;

internal static class AnimationCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Provide() => [new("animation", "animation new <folder.osanim> | info <project> | check <project> | import <project> <asset> | export <project> <output.glb|output.gltf|output.osanimpack> [--skeleton id] | install <pack.osanimpack> <folder> | recover <file.json> <folder.osanim>",
        "Creates, validates, imports and exports Animation Studio projects. Bone tracks, layers, graphs and sequences use the same Core pipeline as the app.", Run)];
    private static Task<int> Run(CliArgs args)
    {
        string Required(int index) => args.Positional(index) ?? throw new ArgumentException("Run 'ose animation --help' for usage.");
        switch (Required(0))
        {
            case "new": new AnimationSession().Save(Required(1)); break;
            case "info":
            case "check":
            {
                AnimationProject p = AnimationSession.Open(Required(1)).Project;
                AnimationJson.Validate(p);
                Console.WriteLine(JsonSerializer.Serialize(new { p.Id, p.Name, Skeletons = p.Skeletons.Count, Clips = p.Clips.Select(c => new { c.Id, c.Name, c.Duration, Tracks = c.Tracks.Count }), Graphs = p.Graphs.Count, Sequences = p.Sequences.Count, Bindings = p.Bindings.Count }, AnimationJson.Options)); break;
            }
            case "import":
            {
                AnimationSession session = AnimationSession.Open(Required(1));
                if (Path.GetExtension(Required(2)).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    string godot = args.Get("godot") ?? Environment.GetEnvironmentVariable("OSE_GODOT") ?? throw new ArgumentException("FBX CLI import needs --godot <Godot executable> and --editor-project <app folder>.");
                    string? app = args.Get("editor-project");
                    AnimationImport.ConvertFbx = path => Core.Animation.AnimationFbxProcess.Convert(godot, app, path);
                }
                session.Import(Required(2)); session.Save(); break;
            }
            case "export":
            {
                AnimationSession session = AnimationSession.Open(Required(1)); string output = Required(2);
                float rate = args.Get("rate") is { } value ? float.Parse(value, CultureInfo.InvariantCulture) : 60;
                if (output.EndsWith(".osanimpack", StringComparison.OrdinalIgnoreCase)) AnimationPackBuild.Export(session.Project, session.Folder, output, rate);
                else AnimationExport.Write(session.Project, args.Get("skeleton") ?? session.Project.Skeletons.FirstOrDefault()?.Id ?? throw new InvalidOperationException("The project has no skeleton."), output, session.Folder,
                    rate);
                break;
            }
            case "install": Console.WriteLine(AnimationPackBuild.Install(Required(1), Required(2))); break;
            case "recover": AnimationSession.Recover(Required(1)).Save(Required(2)); break;
            default: throw new ArgumentException("Run 'ose animation --help' for usage.");
        }
        return Task.FromResult(0);
    }
}
