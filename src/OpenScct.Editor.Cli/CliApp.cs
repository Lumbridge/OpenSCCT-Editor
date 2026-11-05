using System.Text;
using OpenScct.Content;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli;

/// <summary>A command of the <c>ose</c> tool.</summary>
public sealed record CliCommand(string Name, string Usage, string Summary, Func<CliArgs, Task<int>> Run);

/// <summary>
/// <c>ose</c>, the OpenSCCT Editor's command line. Every command works headless and prints plain text, or JSON with
/// <c>--json</c>, so scripts can drive the whole editor without the app.
/// </summary>
public static class CliApp
{
    private static readonly List<CliCommand> s_commands = new();

    public static IReadOnlyList<CliCommand> Commands => s_commands;

    /// <summary>Adds a command (feature files register theirs in <see cref="RegisterAll"/>).</summary>
    public static void Register(CliCommand command) => s_commands.Add(command);

    public static async Task<int> RunAsync(string[] args)
    {
        RegisterAll();
        if (Console.IsOutputRedirected)
        {
            // Piped output (--json read by scripts) is UTF-8 whatever the console's code page; setting
            // Console.OutputEncoding instead would change the code page of the console this runs in.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        CliCommand? cmd = s_commands.Find(c => string.Equals(c.Name, args[0], StringComparison.OrdinalIgnoreCase));
        if (cmd is null)
        {
            Console.Error.WriteLine($"ose: unknown command '{args[0]}'. Run 'ose help'.");
            return 2;
        }

        var parsed = CliArgs.Parse(args.Skip(1));
        if (parsed.Has("help"))
        {
            Console.WriteLine($"ose {cmd.Usage}\n\n{cmd.Summary}");
            return 0;
        }

        try
        {
            return await cmd.Run(parsed).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or FormatException or NotSupportedException or SvMapFormatException
            or ApiException)
        {
            Console.Error.WriteLine($"ose {cmd.Name}: {ex.Message}");
            return 1;
        }
    }

    private static void RegisterAll()
    {
        if (s_commands.Count > 0)
        {
            return;
        }

        Register(new CliCommand("new", "new <folder.osmap> [--id NewMD] [--title \"My map\"]",
            "Creates a new map project with one room, a spy and a merc start.", NewAsync));
        Register(new CliCommand("info", "info <project>", "Prints a short summary of a project.", InfoAsync));
        foreach (CliCommand c in CliCommands.All())
        {
            Register(c);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ose - OpenSCCT Editor command line\n\nCommands:");
        foreach (CliCommand c in s_commands)
        {
            Console.WriteLine($"  {c.Usage}");
            Console.WriteLine($"      {c.Summary.Replace("\n", "\n      ")}");
        }

        Console.WriteLine("\nRun 'ose <command> --help' for one command.");
    }

    private static Task<int> NewAsync(CliArgs a)
    {
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        string id = a.Get("id") ?? Path.GetFileNameWithoutExtension(folder);
        EditorSession s = EditorSession.New(id, a.Get("title") ?? id);
        Starter.AddStarterRoom(s.Project);
        s.Save(folder);
        Console.WriteLine($"Created {Path.GetFullPath(folder)}");
        return Task.FromResult(0);
    }

    private static Task<int> InfoAsync(CliArgs a)
    {
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        MapProject p = ProjectStore.Load(folder);
        Console.WriteLine($"{p.Id}  \"{p.Title}\"  source {p.Source.Kind}");
        foreach (string note in p.LoadNotes)
        {
            Console.WriteLine($"  note: {note}");
        }

        Console.WriteLine($"  brushes {p.Brushes.Count}, actors {p.Actors.Count}, GEs {p.Ges.Count}, layers {p.Layers.Count}");
        foreach (IGrouping<string, Actor> g in p.Actors.GroupBy(x => x.Class).OrderByDescending(g => g.Count()).Take(12))
        {
            Console.WriteLine($"  {g.Count(),5}  {g.Key}");
        }

        return Task.FromResult(0);
    }
}

/// <summary>Parsed arguments: positionals plus <c>--key value</c>, <c>--key=value</c> and bare <c>--flag</c>.</summary>
public sealed class CliArgs
{
    /// <summary>Switches that never take a value, so one given before the positionals does not eat the next one.</summary>
    private static readonly HashSet<string> s_switches = new(StringComparer.OrdinalIgnoreCase)
        { "dry-run", "save", "json", "pretty", "help", "verbose", "overwrite", "no-validate", "live", "link", "replace", "force", "delete",
          "no-restart", "prereleases" };

    private readonly List<string> _positional = new();
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> PositionalArgs => _positional;

    /// <summary>Every <c>--key</c> given, in no particular order.</summary>
    public IEnumerable<string> OptionKeys => _options.Keys;

    public static CliArgs Parse(IEnumerable<string> args)
    {
        var a = new CliArgs();
        List<string> list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string s = list[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                string key = s[2..];
                int eq = key.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    a._options[key[..eq]] = key[(eq + 1)..];
                }
                else if (!s_switches.Contains(key) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    a._options[key] = list[++i];
                }
                else
                {
                    a._options[key] = "true";
                }
            }
            else
            {
                a._positional.Add(s);
            }
        }

        return a;
    }

    public string? Positional(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Get(string key) => _options.GetValueOrDefault(key);

    public bool Has(string key) => _options.ContainsKey(key);

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
