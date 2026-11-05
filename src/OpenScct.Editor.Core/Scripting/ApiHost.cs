using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>The answer to one call: a result, or an error body (<c>{"message","op","field"}</c> and friends).</summary>
public readonly record struct ApiReply(bool IsError, SvJsonNode Body)
{
    public static ApiReply Ok(SvJsonNode body) => new(false, body);

    public static ApiReply Error(ApiException ex) => new(true, ex.ToJson());
}

/// <summary>
/// The scripting API's method table over one open map: apply, query, summary, open, new, save, undo, redo, build, check,
/// describe. The JSON-lines server and the CLI call it, and the editor app can host it on its
/// open session (it sets <see cref="Dispatch"/> to run calls on its main thread and listens to
/// <see cref="SessionChanged"/>).
/// </summary>
public sealed class ApiHost : IApiEndpoint
{
    public ApiHost(EditorSession? session = null) => Session = session;

    public IReadOnlyList<ApiMethod> Methods => ApiMethods.All;

    public EditorSession? Session { get; private set; }

    /// <summary>Raised when open or new replaces the session.</summary>
    public event Action<EditorSession>? SessionChanged;

    /// <summary>Runs a call. The default runs it at once; a UI host marshals it to its own thread.</summary>
    public Func<Func<ApiReply>, ApiReply> Dispatch { get; init; } = call => call();

    /// <summary>The author stamped on objects when a change document names none.</summary>
    public string DefaultAuthor { get; init; } = "script";

    public ApiReply Call(string method, SvJsonObject? args)
    {
        args ??= new SvJsonObject();
        return Dispatch(() =>
        {
            try
            {
                return Invoke(method, args);
            }
            catch (ApiException ex)
            {
                return ApiReply.Error(ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                or InvalidOperationException or OpenScct.Content.SvMapFormatException or NotSupportedException)
            {
                return ApiReply.Error(new ApiException(ex.Message));
            }
            catch (Exception ex)
            {
                // A long-running server answers every call; an unexpected failure is reported, not fatal.
                return ApiReply.Error(ApiException.Internal(ex));
            }
        });
    }

    private ApiReply Invoke(string method, SvJsonObject args) => method switch
    {
        "apply" => Apply(args),
        "query" => Query(args),
        "summary" => ApiReply.Ok(MapSummary.Build(Require(), args)),
        "open" => Open(args),
        "new" => New(args),
        "save" => Save(args),
        "undo" => Step(args, undo: true),
        "redo" => Step(args, undo: false),
        "build" => Build(args),
        "export" => Export(args),
        "playtest" => Playtest(args),
        "check" => Query(With(args, "q", ApiJson.Str("check"))),
        "describe" => ApiReply.Ok(ApiMethods.Describe()),
        _ => throw new ApiException($"unknown method '{method}' ({string.Join(", ", ApiMethods.All.Select(m => m.Name))})", "method"),
    };

    private EditorSession Require() => Session ?? throw new ApiException("no map is open: call open or new first");

    private ApiReply Apply(SvJsonObject args)
    {
        EditorSession s = Require();
        bool save = ApiJson.RequireBool(args, "save") ?? false;
        if (save && s.Folder is null)
        {
            throw new ApiException("the map has no folder yet: call save with a path first", "save");
        }

        // The document is the params, or params.changes (then a dryRun beside it still counts).
        SvJsonNode? changes = args["changes"];
        SvJsonNode doc = changes ?? Without(args, "save");
        bool? dryRun = changes is null ? null : ApiJson.RequireBool(args, "dryRun");
        string? author = doc is SvJsonObject d && d["author"] is not null ? null : DefaultAuthor;
        ChangeResult r = ChangeApplier.Apply(s, doc, dryRun: dryRun, author: author);
        if (r.Ok && save && !ApiJson.GetBool(r.Json, "dryRun", false))
        {
            s.Save();
            r.Json.Add("saved", ApiJson.Str(s.Folder!));
        }

        return r.Ok ? ApiReply.Ok(r.Json) : new ApiReply(true, r.Json["error"]!);
    }

    private ApiReply Query(SvJsonObject args)
    {
        SvJsonNode result = MapQuery.Run(Require(), args);
        bool failed = result is SvJsonObject o && o["error"] is not null;
        return new ApiReply(failed, failed ? ((SvJsonObject)result)["error"]! : result);
    }

    private ApiReply Open(SvJsonObject args)
    {
        string path = ApiJson.GetString(args, "path") ?? throw new ApiException("path is an .osmap project folder (or a map an importer reads)", "path");
        RefuseUnsaved(args);
        EditorSession session;
        int findings = 0;
        if (ProjectStore.IsProject(path))
        {
            session = EditorSession.Open(path);
        }
        else
        {
            IMapImporter importer = EditorServices.ImporterFor(path)
                ?? throw new ApiException(Directory.Exists(path) || File.Exists(path)
                    ? $"{path} is not a project, and no importer reads it"
                    : $"{path} does not exist", "path");
            BrushRecoveryMode brushes;
            try
            {
                brushes = BrushRecoveryImport.ParseMode(ApiJson.GetString(args, "brushes"));
            }
            catch (ArgumentException ex)
            {
                throw new ApiException(ex.Message, "brushes");
            }

            ImportResult imported = EditorServices.Import(importer, path, new ImportOptions
            {
                ContentRoot = EditorServices.ContentRoot,
                BrushRecovery = brushes,
                Progress = Console.Error.WriteLine,
            });
            session = new EditorSession(imported.Project);
            findings = imported.Findings.Count;
        }

        Replace(session);
        SvJsonObject summary = MapSummary.Build(session);
        if (findings > 0)
        {
            summary.Add("importFindings", SvJsonNode.Integer(findings));
        }

        return ApiReply.Ok(summary);
    }

    private ApiReply New(SvJsonObject args)
    {
        RefuseUnsaved(args);
        string? path = ApiJson.GetString(args, "path");
        string id = ApiJson.GetString(args, "id") ?? (path is null ? "NewMD" : Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')));
        EditorSession session = EditorSession.New(id, ApiJson.GetString(args, "title") ?? id);
        if (ApiJson.GetBool(args, "starter", true))
        {
            Starter.AddStarterRoom(session.Project);
        }

        if (path is not null)
        {
            if (ProjectStore.IsProject(path) && !ApiJson.GetBool(args, "overwrite", false))
            {
                throw new ApiException($"{path} already holds a project (pass overwrite:true to replace it)", "path");
            }

            session.Save(path);
        }

        Replace(session);
        return ApiReply.Ok(MapSummary.Build(session));
    }

    private ApiReply Save(SvJsonObject args)
    {
        EditorSession s = Require();
        string? path = ApiJson.GetString(args, "path");
        if (path is null && s.Folder is null)
        {
            throw new ApiException("the map has no folder yet: give path", "path");
        }

        s.Save(path);
        return ApiReply.Ok(ApiJson.Obj(("saved", ApiJson.Str(s.Folder!))));
    }

    private ApiReply Step(SvJsonObject args, bool undo)
    {
        EditorSession s = Require();
        int steps = (int)ApiJson.Number(args, "steps", 1);
        var names = new List<string>();
        for (int i = 0; i < steps; i++)
        {
            string? name = undo ? s.Commands.UndoName : s.Commands.RedoName;
            if (name is null || !(undo ? s.Commands.Undo() : s.Commands.Redo()))
            {
                break;
            }

            names.Add(name);
        }

        return ApiReply.Ok(ApiJson.Obj((undo ? "undone" : "redone", ApiJson.Strings(names)), ("position", SvJsonNode.Integer(s.Commands.Position)),
            ("dirty", ApiJson.Bool(s.IsDirty))));
    }

    private ApiReply Build(SvJsonObject args)
    {
        CompileResult r = Compile(args, ApiJson.GetBool(args, "fast", false));
        var stats = new SvJsonObject();
        foreach (KeyValuePair<string, long> kv in r.Stats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            stats.Add(kv.Key, SvJsonNode.Integer(kv.Value));
        }

        var body = ApiJson.Obj(("ok", ApiJson.Bool(r.Ok)), ("out", ApiJson.Str(r.OutputFolder)),
            ("ms", SvJsonNode.Integer((long)r.Elapsed.TotalMilliseconds)), ("stats", stats),
            ("cols", ApiJson.Strings(["sev", "code", "message", "ids", "at"])),
            ("rows", ApiJson.Arr(r.Findings.OrderBy(f => f.Severity).Take(50).Select(MapQuery.FindingRow))));
        return new ApiReply(!r.Ok, body);
    }

    /// <summary>Exports the open map for the original game (<c>format</c> original: a folder for RE+; t3d: one file, or the
    /// <c>only</c> objects; sdc: a map package in the <c>layout</c> chosen) to <c>out</c> (default export/&lt;id&gt;.&lt;ext&gt;
    /// next to the project).</summary>
    private ApiReply Export(SvJsonObject args)
    {
        EditorSession s = Require();
        string format = ApiJson.GetString(args, "format") ?? "original";
        IMapExporter exporter = EditorServices.Exporter(format)
            ?? throw new ApiException($"unknown format '{format}' ({string.Join(", ", EditorServices.Exporters.Select(e => e.Name))})", "format");
        List<string>? only = ApiJson.StringList(args["only"], "only");
        if (only is not null && exporter is not Interop.T3d.T3dExporter)
        {
            throw new ApiException("only works with format t3d", "only");
        }

        string? output = ApiJson.GetString(args, "out");
        if (output is null)
        {
            string parent = s.Folder is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(s.Folder) ?? ".";
            output = Path.Combine(parent, "export", s.Project.Id + exporter.Extension);
        }

        var choices = new Dictionary<string, string>();
        foreach (ExportChoice c in exporter.Choices)
        {
            if (ApiJson.GetString(args, c.Name) is { } value)
            {
                if (!c.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ApiException($"{c.Name} is one of: {string.Join(", ", c.Values)}", c.Name);
                }

                choices[c.Name] = value.ToLowerInvariant();
            }
        }

        var options = new ExportOptions
        {
            ContentRoot = EditorServices.ContentRoot,
            GameRoot = ApiJson.GetString(args, "game"),
            Choices = choices,
            ProjectFolder = s.Folder,
            Progress = Console.Error.WriteLine,
        };
        ExportResult r;
        if (only is not null)
        {
            var refs = new List<ObjectRef>();
            foreach (string id in only)
            {
                refs.Add(s.Project.AllRefs().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { Id: not null } found
                    ? found
                    : throw new ApiException($"no object '{id}' in {s.Project.Id}", "only"));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            r = Interop.T3d.T3dExporter.ExportSelection(s.Project, refs, output, options);
        }
        else
        {
            if (!exporter.WritesFolder)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            }

            r = exporter.Export(s.Project, output, options);
        }

        var body = ApiJson.Obj(("ok", ApiJson.Bool(r.Ok)), ("format", ApiJson.Str(exporter.Name)), ("files", ApiJson.Strings(r.Files)),
            ("cols", ApiJson.Strings(["sev", "code", "message", "ids", "at"])),
            ("rows", ApiJson.Arr(r.Findings.OrderBy(f => f.Severity).Take(80).Select(MapQuery.FindingRow))));
        return new ApiReply(!r.Ok, body);
    }

    /// <summary>Compiles the open map to <c>out</c> (default build/&lt;id&gt; next to the project).</summary>
    private CompileResult Compile(SvJsonObject args, bool fast)
    {
        EditorSession s = Require();
        IMapCompiler compiler = EditorServices.Compiler ?? throw new ApiException("no map compiler is loaded in this build");
        string? outDir = ApiJson.GetString(args, "out");
        if (outDir is null)
        {
            string parent = s.Folder is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(s.Folder) ?? ".";
            outDir = Path.Combine(parent, "build", s.Project.Id);
        }

        return compiler.Compile(s.Project, outDir, new CompileOptions
        {
            FastLighting = fast,
            ContentRoot = EditorServices.ContentRoot,
            ProjectFolder = s.Folder,
            Progress = Console.Error.WriteLine,
        });
    }

    /// <summary>
    /// Compiles the open map and plays a headless test round of it. The game is the one ose finds (OSE_GAME_EXE, the
    /// launcher's install...): a script cannot name a program to run.
    /// </summary>
    private ApiReply Playtest(SvJsonObject args)
    {
        CompileResult r = Compile(args, ApiJson.GetBool(args, "fast", true));
        if (!r.Ok)
        {
            Finding first = r.Findings.First(f => f.Severity == Severity.Error);
            throw new ApiException($"the map does not compile: {first.Code}: {first.Message}");
        }

        string exe = GameExe.Find() ?? throw new ApiException("OpenSCCT.exe not found: set OSE_GAME_EXE, or install OpenSCCT with its launcher");
        if (GameExe.TooOld(exe) is { } tooOld)
        {
            throw new ApiException(tooOld);
        }

        Workspace workspace = Require().Project.Workspace;
        PlayOptions play = workspace.Play;
        PlayStart? startAt = null;
        if (ApiJson.GetString(args, "at") is { } at)
        {
            try
            {
                startAt = Geometry.Edit.ActorPlacement.OnFloor(new WorldGeometry(Require().Project, Require().Folder), PlayStart.Parse(at, workspace));
            }
            catch (ArgumentException e)
            {
                throw new ApiException(e.Message, "at");
            }
        }

        List<string>? route = Route(args);
        bool client = ApiJson.GetBool(args, "client", false);
        string? from = ApiJson.GetString(args, "from");
        if (client && (route is not null || from is not null || ApiJson.Number(args, "bots", 0) > 0))
        {
            throw new ApiException("client:true plays an online round with no bots and no route: leave out route, from and bots", "client");
        }

        // The online lobby sets its own rules for the mode and seats the two players; nothing would take these.
        if (client && new[] { "rules", "difficulty", "players" }.FirstOrDefault(k => args[k] is not null) is { } unused)
        {
            throw new ApiException($"client:true plays the online lobby's rules for mode with two players: leave out {unused}", unused);
        }

        // The test start is an offline round's, and a route bot starts at its side's start (the game would move the
        // player standing by on the other side instead).
        if (startAt is not null && (client || route is not null))
        {
            throw new ApiException($"at starts an offline round where you play: leave it out with {(client ? "client" : "route")}", "at");
        }

        if (from is not null && Require().Project.FindActor(from) is null)
        {
            throw new ApiException($"the map has no {from}", "from");
        }

        // A route run, or an online one, has no other bots unless asked for.
        bool plain = route is null && !client;
        int bots = (int)ApiJson.Number(args, "bots", plain ? HeadlessPlay.DefaultBots(play) : 0);
        if (bots is < 0 or > HeadlessPlay.MaxBots)
        {
            throw new ApiException($"bots is 0 to {HeadlessPlay.MaxBots}", "bots");
        }

        HeadlessPlayReport report = HeadlessPlay.Run(new HeadlessPlayOptions
        {
            Exe = exe,
            MapFolder = r.OutputFolder,
            MapId = Require().Project.Id,
            Side = ApiJson.GetString(args, "side") ?? play.Side,
            Mode = ApiJson.GetString(args, "mode") ?? play.Mode,
            Rules = ApiJson.GetString(args, "rules") ?? play.Rules,
            Bots = bots,
            Difficulty = ApiJson.GetString(args, "difficulty") ?? play.Difficulty,
            Players = (int)ApiJson.Number(args, "players", HeadlessPlay.DefaultPlayers(play, botsGiven: args["bots"] is not null || !plain)),
            Content = GameExe.LauncherContent(exe),
            Seconds = Math.Clamp(ApiJson.Number(args, "seconds", route is not null ? HeadlessPlay.RouteSeconds(route.Count) : 40), 5, 600),
            StartAt = startAt,
            Route = route,
            RouteFrom = from,
            Client = client,
        });
        return new ApiReply(false, HeadlessPlay.ToJson(report, ApiJson.GetBool(args, "output", false)));
    }

    /// <summary>The playtest's route: <c>route</c>, the stops a route bot walks in order: floor points ([x,y,z]) or object ids.</summary>
    private List<string>? Route(SvJsonObject args)
    {
        if (args["route"] is not SvJsonArray list)
        {
            return args["route"] is null ? null : throw new ApiException("route is a list of stops ([x,y,z] or an object id)", "route");
        }

        var ctx = new ChangeContext(Require().Project, Require().Folder, "query", "query");
        return [.. list.Select((p, i) => p is SvJsonString { Value: var id } && Require().Project.FindActor(id) is not null
            ? id
            : HeadlessPlay.RouteStop(ctx.Point(p, $"route[{i}]")))];
    }

    /// <summary>Open and new replace the map: unsaved changes are kept unless the call says discard:true.</summary>
    private void RefuseUnsaved(SvJsonObject args)
    {
        if (Session is { IsDirty: true } s && ApiJson.RequireBool(args, "discard") != true)
        {
            throw new ApiException($"the open map ({s.Project.Id}) has unsaved changes: save it first, or pass discard:true to drop them", "discard");
        }
    }

    private void Replace(EditorSession session)
    {
        Session = session;
        SessionChanged?.Invoke(session);
    }

    private static SvJsonObject With(SvJsonObject o, string key, SvJsonNode value)
    {
        SvJsonObject copy = Without(o, key);
        copy.Add(key, value);
        return copy;
    }

    private static SvJsonObject Without(SvJsonObject o, string key)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key != key)
            {
                copy.Add(kv.Key, kv.Value);
            }
        }

        return copy;
    }
}
