using System.Globalization;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Shell.Shots;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Scripting;

/// <summary>
/// Live editing. The editor hosts the scripting API on 127.0.0.1 (<see cref="LiveServer"/>, found through a
/// discovery file with a random token), so <c>ose serve --live</c> edits the open map: every call runs on the main
/// thread against the open session, lands on its undo stack and updates every view. Applied change documents are
/// logged with their label and what they created gets selected. Proposals (a script's <c>propose</c>, or a change
/// document pasted into the command palette) are drawn as a ghost with Accept and Reject. The status bar shows
/// connected clients.
/// </summary>
[ShellPart(Order = 95)]
public sealed partial class LiveEditing : Node, IShellPart, ILiveEditor
{
    /// <summary>How long a server thread waits for the main thread before giving up on a call.</summary>
    private static readonly TimeSpan s_mainThreadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The status bar field: a dot (green while the server listens) and "Live" or the connected clients.</summary>
    private readonly PanelContainer _statusField = new() { ThemeTypeVariation = "StatusSegment", MouseFilter = Control.MouseFilterEnum.Pass };
    private readonly TextureRect _statusDot = new() { StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _status = new() { ThemeTypeVariation = "StatusText", MouseFilter = Control.MouseFilterEnum.Ignore };
    private EditorContext _ctx = null!;
    private LiveEndpoint _endpoint = null!;
    private LiveServer? _server;
    private GhostLayer? _ghost;
    private ProposalBar _bar = null!;
    private int _mainThread;
    private int _shownProposal;
    private bool _refreshQueued;

    public static LiveEditing? Instance { get; private set; }

    public ProposalBook Proposals { get; } = new();

    public EditorSession Session => _ctx.Session;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        _mainThread = System.Environment.CurrentManagedThreadId;
        Name = "LiveEditing";
        window.AddChild(this);
        _endpoint = new LiveEndpoint(this, Dispatch);

        _bar = new ProposalBar(Accept, Reject, FrameProposal);
        window.ViewportArea.AddChild(_bar);
        var field = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        field.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        field.AddChild(_statusDot);
        field.AddChild(_status);
        _statusField.AddChild(field);
        window.StatusRow.AddChild(_statusField);
        if (ViewportLayout.Instance is { } layout)
        {
            _ghost = new GhostLayer { Name = "Ghost" };
            layout.Scene.AddChild(_ghost);
        }

        Proposals.Changed += OnProposalChanged;
        Proposals.Refreshed += OnProposalRefreshed;
        context.SessionReplaced += OnSessionReplaced;
        context.ProjectChanged += _ => QueueProposalRefresh();
        context.Saved += OnSaved;
        RegisterActions();
        // Clients are cut off as soon as the editor starts to quit, so no call reaches a closing editor.
        EditorLifetime.Quitting += StopServer;
        if (AppSettings.GetBool("scripting", "live", true) && BackgroundMode.AllowsLiveServer)
        {
            StartServer();
        }

        UpdateStatus();
    }

    public override void _Notification(int what)
    {
        // Not on the close request: the user may still cancel the quit (unsaved changes).
        if (what == NotificationPredelete)
        {
            StopServer();
        }
    }

    public override void _ExitTree() => StopServer();

    // ---- ILiveEditor (main thread) --------------------------------------------------------------------------------

    public void ReplaceSession(EditorSession session) => _ctx.ReplaceSession(session);

    public SvJsonObject State()
    {
        EditorSession s = _ctx.Session;
        Workspace w = s.Project.Workspace;
        ViewportLayout? layout = ViewportLayout.Instance;
        Vec3? placement = null;
        try
        {
            placement = _ctx.PlacementPoint?.Invoke();
        }
        catch (InvalidOperationException)
        {
        }

        return ApiJson.Obj(
            ("map", ApiJson.Str(s.Project.Id)),
            ("title", ApiJson.Str(s.Project.Title)),
            ("folder", s.Folder is null ? null : ApiJson.Str(s.Folder)),
            ("dirty", ApiJson.Bool(s.IsDirty)),
            ("undo", s.Commands.UndoName is { } u ? ApiJson.Str(u) : null),
            ("selection", ApiJson.Strings(s.Selection.Items.Take(200).Select(r => r.Id))),
            ("selectionCount", SvJsonNode.Integer(s.Selection.Count)),
            ("camera", ApiJson.Obj(("loc", ApiJson.Vec(w.CameraLocation)), ("rot", ApiJson.Rotator(w.CameraRotation)))),
            ("viewCenter", placement is { } p ? ApiJson.Vec(p) : null),
            ("cursor", ViewportLayout.Instance?.CursorWorld is { } c ? ApiJson.Vec(c) : null),
            ("clients", ApiJson.Strings(_server?.Clients ?? [])),
            ("preview", layout is null ? null : ApiJson.Obj(
                ("sceneSyncPending", ApiJson.Bool(layout.SceneSyncPending)),
                ("geometry", ApiJson.Str(layout.Scene.GeometryPreviewState)),
                ("lighting", ApiJson.Str(layout.Scene.LightingPreviewState)),
                ("sky", ApiJson.Str(layout.Scene.SkyPreviewState)),
                ("litReady", ApiJson.Bool(!layout.SceneSyncPending && layout.Scene.GeometryPreviewState == "ready" &&
                    layout.Scene.LightingPreviewState == "ready" && layout.Scene.SkyPreviewState == "ready")),
                ("lightingVersion", SvJsonNode.Integer(layout.Scene.LightingVersion)),
                ("completedLightingVersion", layout.Scene.CompletedLightingVersion is { } version ? SvJsonNode.Integer(version) : null),
                ("error", layout.Scene.PreviewError is { } previewError ? ApiJson.Str(previewError) : null))),
            ("matchGrey", MatchGreying.On
                ? ApiJson.Obj(("players", SvJsonNode.Integer(MatchGreying.Players)),
                    ("mode", MatchGreying.Mode is { } mode ? ApiJson.Str(mode) : null),
                    ("greyed", SvJsonNode.Integer(MatchGreying.Count)))
                : null),
            ("proposal", Proposals.Pending is { } pending ? pending.ToJson() : null));
    }

    public void Called(string method, SvJsonObject args, ApiReply reply)
    {
        if (reply.IsError)
        {
            string message = reply.Body is SvJsonObject e ? ApiJson.GetString(e, "message") ?? ApiJson.Write(e) : ApiJson.Write(reply.Body);
            _ctx.Log($"Live {method} failed: {message}", Severity.Warning);
            return;
        }

        if (reply.Body is not SvJsonObject body)
        {
            return;
        }

        switch (method)
        {
            case "apply" when !ApiJson.GetBool(body, "dryRun", false):
                LogApplied(body, args);
                break;
            case "open" or "new":
                _ctx.Log($"Live client opened {_ctx.Project.Id} ({_ctx.Session.Folder ?? "unsaved"}).");
                break;
            case "save":
                _ctx.Log($"Live client saved {ApiJson.GetString(body, "saved")}.");
                break;
            case "ui.input":
                _ctx.Log($"Background editor input: {ApiJson.Number(body, "done", 0)} steps.");
                break;
            case "undo" or "redo":
                _ctx.Log($"Live {method}: {string.Join(", ", ApiJson.StringList(body[method == "undo" ? "undone" : "redone"], method) ?? [])}");
                break;
        }
    }

    /// <summary>The live editor allows read-only viewport captures. UI inspection and synthetic input still require
    /// an isolated background editor, where embedded popups, a synthetic pointer and steady redraws are available.</summary>
    public Task<ApiReply> Ui(string method, SvJsonObject args) =>
        UiDriver.Instance is { } driver && DisplayServer.GetName() != "headless" &&
            (BackgroundMode.Active || method == "ui.shot" && ApiJson.GetBool(args, "views", false))
            ? driver.Call(method, args)
            : Task.FromResult(ApiReply.Error(new ApiException(
                "In the live editor, use ui.shot with views:true to capture only the map viewports. " +
                "UI inspection and input require an isolated background editor (tools/Start-BackgroundEditor.ps1).")));

    // ---- proposals ------------------------------------------------------------------------------------------------

    /// <summary>Previews (as a ghost) or applies a change document typed or pasted by the user.</summary>
    public void RunChangeDocument(string text, bool apply)
    {
        SvJsonNode doc;
        try
        {
            doc = ApiJson.Parse(text, "change document");
        }
        catch (ApiException ex)
        {
            _ctx.Log($"Not a change document: {ex.Message}", Severity.Error);
            return;
        }

        if (!apply)
        {
            Proposal p = Proposals.Open(_ctx.Session, doc);
            if (p.Status == ProposalStatus.Failed)
            {
                _ctx.Log($"The change document fails: {ProposalBook.ErrorText(p.Outcome)}", Severity.Error);
            }

            return;
        }

        ChangeResult r = ChangeApplier.Apply(_ctx.Session, doc);
        if (!r.Ok)
        {
            _ctx.Log($"The change document fails: {ProposalBook.ErrorText(r.Json["error"] as SvJsonObject)}", Severity.Error);
            return;
        }

        LogApplied(r.Json, doc as SvJsonObject ?? new SvJsonObject());
    }

    private void Accept()
    {
        if (Proposals.Pending is not { } p || p.Problem is not null)
        {
            return;
        }

        ChangeResult? r = Proposals.Accept(_ctx.Session);
        if (r is { Ok: true })
        {
            LogApplied(r.Json, p.Preview.Document as SvJsonObject ?? new SvJsonObject());
        }
        else if (r is not null)
        {
            _ctx.Log($"Proposal {p.Id} no longer applies: {ProposalBook.ErrorText(r.Json["error"] as SvJsonObject)}", Severity.Error);
        }
    }

    private void Reject()
    {
        if (Proposals.Pending is { } p)
        {
            Proposals.Reject();
            _ctx.Log($"Rejected proposal {p.Id}: {p.Preview.Label}.");
        }
    }

    private void FrameProposal()
    {
        if (_ghost?.Bounds is { } b)
        {
            ViewportLayout.Instance?.FrameAll(b);
        }
    }

    private void OnProposalChanged()
    {
        Proposal? p = Proposals.Pending;
        Draw(p);
        if (p is not null && p.Id != _shownProposal)
        {
            _shownProposal = p.Id;
            _ctx.Log($"Proposal {p.Id}: {p.Preview.Label} ({p.Preview.Counts}). Accept or Reject it over the views.");
        }
    }

    /// <summary>The map changed while a proposal is pending: redo its dry run once this frame's edits are in, so the
    /// ghost shows what Accept would do now.</summary>
    private void QueueProposalRefresh()
    {
        if (Proposals.Pending is null || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        EditorLifetime.Defer(() =>
        {
            _refreshQueued = false;
            Proposals.Refresh(_ctx.Session);
        });
    }

    private void OnProposalRefreshed(Proposal p)
    {
        string? before = _bar.Problem;
        Draw(p);
        if (p.Problem is not null && p.Problem != before)
        {
            _ctx.Log($"Proposal {p.Id} no longer applies to the map: {p.Problem}", Severity.Warning);
        }
        else if (p.Problem is null && before is not null)
        {
            _ctx.Log($"Proposal {p.Id} applies again.");
        }
    }

    private void Draw(Proposal? p)
    {
        _bar.ShowFor(p);
        if (p is null)
        {
            _ghost?.Clear();
        }
        else
        {
            _ghost?.Show(p.Preview, _ctx.Project);
        }

        ViewportLayout.Instance?.RedrawAll();
    }

    // ---- logging and selection ------------------------------------------------------------------------------------

    /// <summary>Logs an applied change document with its label and selects what it created (unless the document set
    /// the selection itself).</summary>
    private void LogApplied(SvJsonObject result, SvJsonObject args)
    {
        var created = (result["created"] as SvJsonObject)?.Select(kv => kv.Key).ToList() ?? [];
        int updated = (result["updated"] as SvJsonObject)?.Count ?? 0;
        int deleted = (result["deleted"] as SvJsonArray)?.Count ?? 0;
        SvJsonObject? doc = args["changes"] as SvJsonObject ?? args;
        string author = ApiJson.GetString(doc, "author") ?? "Live edit";
        string label = ApiJson.GetString(result, "undo") ?? ApiJson.GetString(doc, "label") ?? "changes";
        _ctx.Log($"{author}: {label} ({ChangePreview.CountsText(created.Count, updated, deleted)})");
        bool selects = doc["ops"] is SvJsonArray ops && ops.OfType<SvJsonObject>().Any(o => ApiJson.GetString(o, "op") == "select");
        List<ObjectRef> refs = created.Select(id => MapObjects.Find(_ctx.Project, id)).OfType<ObjectRef>().ToList();
        if (!selects && refs.Count > 0)
        {
            _ctx.Selection.Set(refs);
        }
    }

    // ---- server ---------------------------------------------------------------------------------------------------

    private void StartServer()
    {
        if (_server is not null)
        {
            return;
        }

        string version = typeof(LiveEditing).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var server = new LiveServer(_endpoint, version);
        server.ClientsChanged += () => BackgroundJobs.Post(OnClientsChanged);
        server.Problem += message => BackgroundJobs.Post(() => _ctx.Log(message, Severity.Warning));
        try
        {
            server.Start(_ctx.Project.Id, _ctx.Session.Folder);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException)
        {
            _ctx.Log($"Live editing is off: {ex.Message}", Severity.Warning);
            server.Dispose();
            return;
        }

        _server = server;
        _ctx.Log(string.Create(CultureInfo.InvariantCulture,
            $"Live editing on 127.0.0.1:{server.Port} (MCP: ose mcp --live, scripting: ose serve --live). Discovery: {LiveDiscovery.FileFor(System.Environment.ProcessId)}"));
        UpdateStatus();
    }

    private void StopServer()
    {
        if (_server is null)
        {
            return;
        }

        _server.Dispose();
        _server = null;
        if (IsInsideTree())
        {
            UpdateStatus();
        }
    }

    private void OnClientsChanged()
    {
        if (!IsInstanceValid(this) || !IsInsideTree())
        {
            return; // posted by a connection that closed as the editor shut down
        }

        IReadOnlyList<string> clients = _server?.Clients ?? [];
        _ctx.Log(clients.Count == 0 ? "No live clients connected." : $"Live clients connected: {string.Join(", ", clients)}");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        IReadOnlyList<string> clients = _server?.Clients ?? [];
        if (_server is null)
        {
            _statusField.Visible = false;
            return;
        }

        // The dot says the state (listening, a client connected); the text stays quiet until a client is there.
        _statusField.Visible = true;
        _statusDot.Texture = Icons.Get("dot", EditorTheme.Ok);
        _status.Text = clients.Count == 0 ? "Live editing" : $"Live: {string.Join(", ", clients.Distinct())}";
        _status.AddThemeColorOverride("font_color", clients.Count == 0 ? EditorTheme.TextDim : EditorTheme.Ok);
        _statusField.TooltipText = string.Create(CultureInfo.InvariantCulture,
            $"AI agents can edit this map through MCP (ose mcp --live).\n" +
            $"Scripts can connect with ose serve --live.\n" +
            $"Local server: 127.0.0.1:{_server.Port}\nConnected clients: {clients.Count}");
    }

    private void OnSessionReplaced()
    {
        Proposals.Withdraw();
        OnSaved();
    }

    private void OnSaved() => _server?.UpdateMap(_ctx.Project.Id, _ctx.Session.Folder);

    /// <summary>Runs a script call on the main thread and waits for it (server threads), or runs it at once (main).
    /// A server thread stops waiting when the editor starts to quit.</summary>
    private ApiReply Dispatch(Func<ApiReply> call)
    {
        if (System.Environment.CurrentManagedThreadId == _mainThread)
        {
            return call();
        }

        if (EditorLifetime.IsQuitting)
        {
            return Closing();
        }

        // 0 queued, 1 running, 2 given up: a call the client was told had failed must never run later.
        int state = 0;
        var done = new TaskCompletionSource<ApiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundJobs.Post(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            try
            {
                done.TrySetResult(call());
            }
            catch (Exception ex)
            {
                done.TrySetResult(ApiReply.Error(ApiException.Internal(ex)));
            }
        });
        bool finished;
        try
        {
            finished = done.Task.Wait(s_mainThreadTimeout, EditorLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            finished = false;
        }

        if (finished || Interlocked.CompareExchange(ref state, 2, 0) != 0)
        {
            // Finished, or started just as the wait ran out: a started call runs to the end on the main thread and
            // answers (the wait is bounded all the same).
            return done.Task.Wait(s_mainThreadTimeout) ? done.Task.Result : Closing();
        }

        return EditorLifetime.IsQuitting ? Closing() : ApiReply.Error(new ApiException("the editor did not answer in time, " +
            "so the call was dropped (is a dialog open?)"));
    }

    private static ApiReply Closing() => ApiReply.Error(new ApiException("the editor is closing, so the call was dropped"));

    // ---- actions --------------------------------------------------------------------------------------------------

    private void RegisterActions()
    {
        const string menu = "Tools/Agents and Scripting";
        _ctx.AddAction(new EditorAction("live.copyMcp", "Copy MCP Configuration", menu, CopyMcpConfiguration)
        {
            Order = 110, Description = "Copies the connection configuration for AI agents using MCP",
        });
        _ctx.AddAction(new EditorAction("live.allow", "Allow Live Agent and Script Connections", menu, () =>
        {
            bool on = _server is null;
            AppSettings.Set("scripting", "live", on);
            if (on)
            {
                StartServer();
            }
            else
            {
                StopServer();
                _ctx.Log("Live editing is off.");
            }
        })
        {
            Order = 100, IsChecked = () => _server is not null,
            Description = "Allow local, token-protected editing by AI agents through MCP and scripts through the scripting API",
        });
        _ctx.AddAction(new EditorAction("live.accept", "Accept Proposal", menu, Accept)
        {
            Order = 200, IsEnabled = () => Proposals.Pending is { Problem: null }, Description = "Apply the ghost preview as one undo step",
        });
        _ctx.AddAction(new EditorAction("live.reject", "Reject Proposal", menu, Reject)
        {
            Order = 210, IsEnabled = () => Proposals.Pending is not null,
        });
        _ctx.AddAction(new EditorAction("live.frameProposal", "Frame Proposal", menu, FrameProposal)
        {
            Order = 220, IsEnabled = () => Proposals.Pending is not null,
        });
        _ctx.AddAction(new EditorAction("live.previewClipboard", "Preview Change Document from Clipboard", menu,
            () => RunChangeDocument(EditorClipboard.Get(), apply: false))
        {
            Order = 300, Description = "Shows an osmap.changes/1 document from the clipboard as a ghost to accept or reject",
        });
        _ctx.AddAction(new EditorAction("live.applyClipboard", "Apply Change Document from Clipboard", menu,
            () => RunChangeDocument(EditorClipboard.Get(), apply: true))
        {
            Order = 310,
        });
        _ctx.AddAction(new EditorAction("live.previewFile", "Preview Change Document from --changes", "", PreviewFromCommandLine)
        {
            IsEnabled = () => ChangesArgument() is not null,
            Description = "Headless runs: -- --changes=<file.json> --run=live.previewFile",
        });
    }

    private void CopyMcpConfiguration()
    {
        string command = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "ose", "ose.exe");
        var args = new List<string> { "mcp", "--live" };
        if (!OS.HasFeature("template"))
        {
            string cli = Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "src", "OpenScct.Editor.Cli", "bin");
            string? dll = new[] { "Debug", "Release" }
                .Select(configuration => Path.GetFullPath(Path.Combine(cli, configuration, "net10.0", "ose.dll")))
                .FirstOrDefault(File.Exists);
            if (dll is null)
            {
                _ctx.Log("Build OpenScct.Editor.Cli before copying MCP configuration.", Severity.Error);
                return;
            }

            command = "dotnet";
            args.Insert(0, dll);
        }
        else if (!File.Exists(command))
        {
            _ctx.Log("The bundled ose/ose.exe is missing from this editor installation.", Severity.Error);
            return;
        }

        SvJsonObject config = ApiJson.Obj(("mcpServers", ApiJson.Obj(("openscct-editor", ApiJson.Obj(
            ("command", ApiJson.Str(command)),
            ("args", ApiJson.Strings(args)))))));
        EditorClipboard.Set(ApiJson.Write(config));
        _ctx.Log("Copied OpenSCCT Editor MCP configuration.");
    }

    private static string? ChangesArgument() =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--changes=", StringComparison.Ordinal))?["--changes=".Length..];

    private void PreviewFromCommandLine()
    {
        if (ChangesArgument() is not { } path)
        {
            return;
        }

        try
        {
            RunChangeDocument(File.ReadAllText(path), apply: false);
        }
        catch (IOException ex)
        {
            _ctx.Log($"Cannot read {path}: {ex.Message}", Severity.Error);
        }
    }
}
