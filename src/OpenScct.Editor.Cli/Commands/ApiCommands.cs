using System.Text;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>The scripting API on the command line: apply, query, summary (one shot, compact JSON out) and serve
/// (a long-running server on stdio).</summary>
internal static class ApiCommands
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("apply", "apply <project.osmap> <changes.json|-> [--dry-run] [--save] [--author name]",
            "Applies an osmap.changes/1 document (a file, '-' for stdin, or inline JSON) as one undo step and prints the diff.", Guard(ApplyAsync)),
        new("query", "query <project.osmap> <query-json|file|->",
            "Runs a query ({\"q\":\"find\",...}, or {\"queries\":[...]}) and prints compact JSON.", Guard(QueryAsync)),
        new("summary", "summary <project.osmap>", "Prints a short overview of the map (counts, starts, objectives, problems).", Guard(SummaryAsync)),
        new("mcp", "mcp [project.osmap] | mcp --live [--pid N]",
            "Serves the OpenSCCT Editor tools over Model Context Protocol (stdio).", McpAsync),
        new("serve", "serve [project.osmap] | serve --live [--pid N]",
            "Serves the scripting API as JSON lines on stdin/stdout: {\"id\",\"method\",\"params\"} -> {\"id\",\"result\"|\"error\"}. " +
            "--live edits the map open in the running editor.", Guard(ServeAsync)),
    ];

    /// <summary>API errors (bad JSON, unknown ids...) print as {"ok":false,"error":{...}} with exit code 1.</summary>
    private static Func<CliArgs, Task<int>> Guard(Func<CliArgs, Task<int>> run) => async a =>
    {
        try
        {
            return await run(a).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            Print(ApiJson.Obj(("ok", SvJsonNode.False), ("error", ex.ToJson())));
            return 1;
        }
    };

    private static Task<int> ApplyAsync(CliArgs a)
    {
        EditorSession session = OpenProject(a);
        SvJsonNode doc = ApiJson.Parse(ReadInput(a.Positional(1) ?? throw new ArgumentException("give the change document (a file, - or JSON)")), "changes");
        bool dry = a.Has("dry-run");
        ChangeResult result = ChangeApplier.Apply(session, doc, dryRun: dry ? true : null, author: a.Get("author"), writeFiles: a.Has("save"));
        if (result.Ok && !dry && !ApiJson.GetBool(result.Json, "dryRun", false))
        {
            if (a.Has("save"))
            {
                session.Save();
                result.Json.Add("saved", ApiJson.Str(session.Folder!));
            }
            else
            {
                Console.Error.WriteLine("ose apply: not saved (add --save to keep the changes)");
            }
        }

        Print(result.Json);
        return Task.FromResult(result.Ok ? 0 : 1);
    }

    private static Task<int> QueryAsync(CliArgs a)
    {
        EditorSession session = OpenProject(a);
        SvJsonNode query = ApiJson.Parse(ReadInput(a.Positional(1) ?? throw new ArgumentException("give the query (JSON, a file or -)")), "query");
        SvJsonNode result = MapQuery.Run(session, query);
        Print(result);
        return Task.FromResult(result is SvJsonObject o && o["error"] is not null ? 1 : 0);
    }

    private static Task<int> SummaryAsync(CliArgs a)
    {
        Print(MapSummary.Build(OpenProject(a)));
        return Task.FromResult(0);
    }

    private static async Task<int> ServeAsync(CliArgs a)
    {
        IApiEndpoint host = Endpoint(a, "ose serve");
        (TextReader input, TextWriter output) = Stdio();
        await new JsonLinesServer(host).RunAsync(input, output).ConfigureAwait(false);
        (host as IDisposable)?.Dispose();
        return 0;
    }

    private static async Task<int> McpAsync(CliArgs a)
    {
        try
        {
            IApiEndpoint host = Endpoint(a, "ose mcp");
            using var connection = host as IDisposable;
            (TextReader input, TextWriter output) = Stdio();
            string version = typeof(ApiCommands).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            await new McpServer(host, version).RunAsync(input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"ose mcp: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The running editor (--live), or a host over the project folder given.</summary>
    private static IApiEndpoint Endpoint(CliArgs a, string client) => a.Has("live") ? LiveCommands.Connect(a, client) : Host(a);

    private static ApiHost Host(CliArgs a)
    {
        EditorServices.Discover(typeof(ApiCommands).Assembly);
        string? path = a.Positional(0);
        return new ApiHost(path is null ? null : EditorSession.Open(path)) { DefaultAuthor = a.Get("author") ?? "script" };
    }

    private static EditorSession OpenProject(CliArgs a)
    {
        EditorServices.Discover(typeof(ApiCommands).Assembly);
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        return EditorSession.Open(folder);
    }

    /// <summary>A file's text, stdin for "-", or the argument itself when it is inline JSON.</summary>
    private static string ReadInput(string arg)
    {
        if (arg == "-")
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), s_utf8);
            return stdin.ReadToEnd();
        }

        string trimmed = arg.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return arg;
        }

        return File.ReadAllText(arg.StartsWith('@') ? arg[1..] : arg, s_utf8);
    }

    private static void Print(SvJsonNode node)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), s_utf8);
        stdout.Write(ApiJson.Write(node));
        stdout.Write('\n');
    }

    private static (TextReader, TextWriter) Stdio() =>
        (new StreamReader(Console.OpenStandardInput(), s_utf8),
         new StreamWriter(Console.OpenStandardOutput(), s_utf8) { AutoFlush = true, NewLine = "\n" });
}
