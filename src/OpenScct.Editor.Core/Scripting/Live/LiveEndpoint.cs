using OpenScct.Content.Json;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>What the live endpoint needs from the editor that hosts it. Called on the editor's main thread only.</summary>
public interface ILiveEditor
{
    /// <summary>The open map.</summary>
    EditorSession Session { get; }

    /// <summary>The editor's proposals (ghost previews).</summary>
    ProposalBook Proposals { get; }

    /// <summary>Makes another session the editor's open map (a script called open or new).</summary>
    void ReplaceSession(EditorSession session);

    /// <summary>The editor's state for <c>editor</c> calls: selection, camera, placement point...</summary>
    SvJsonObject State();

    /// <summary>Told after every call, so the editor can log changes and select what a script made.</summary>
    void Called(string method, SvJsonObject args, ApiReply reply);

    /// <summary>Starts a <c>ui.*</c> call (screenshot, input, control list). It finishes on the main thread after the
    /// editor has drawn the frames it needs, so the caller waits for the task off the main thread.</summary>
    Task<ApiReply> Ui(string method, SvJsonObject args);
}

/// <summary>
/// The scripting API of a running editor: every call runs on the editor's main thread (through <c>dispatch</c>) against
/// its open session, so live edits land on the editor's undo stack and every view updates. Adds the live methods
/// (propose, proposal, editor) and refuses to replace a map that has unsaved changes.
/// </summary>
public sealed class LiveEndpoint : IApiEndpoint
{
    /// <summary>The longest a proposal call waits for the user's answer.</summary>
    private const double MaxWaitSeconds = 600;

    /// <summary>The longest a ui.* call may take (input with waits, a screenshot with every view).</summary>
    private static readonly TimeSpan s_uiTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<Func<ApiReply>, ApiReply> _dispatch;
    private readonly ILiveEditor _editor;
    private ApiHost? _host;

    public LiveEndpoint(ILiveEditor editor, Func<Func<ApiReply>, ApiReply> dispatch)
    {
        _editor = editor;
        _dispatch = dispatch;
    }

    public IReadOnlyList<ApiMethod> Methods => LiveMethods.WithApi;

    public ApiReply Call(string method, SvJsonObject? args)
    {
        args ??= new SvJsonObject();
        if (method.StartsWith("ui.", StringComparison.Ordinal))
        {
            return CallUi(method, args);
        }

        // Long jobs run on a copy of the map off the main thread, so the editor stays responsive.
        if (method is "playtest" or "export" || (method is "query" or "check" && MapQuery.NeedsCompiledMap(method == "check" ? With(args, "q", "check") : args)))
        {
            return OnSnapshot(method, args);
        }

        ApiReply reply = _dispatch(() => Guarded(method, args));
        double wait = Math.Min(ApiJson.Number(args, "wait", 0), MaxWaitSeconds);
        if (reply.IsError || wait <= 0 || method is not ("propose" or "proposal") || reply.Body is not SvJsonObject body)
        {
            return reply;
        }

        // Wait for the user's answer off the main thread, then read the final status there.
        int id = (int)ApiJson.Number(body, "proposal", 0);
        FindDone(id)?.Wait(TimeSpan.FromSeconds(wait));

        return _dispatch(() => Status(id, body));
    }

    /// <summary>Starts a ui.* call on the main thread and waits here, on the server's thread, for the editor to finish
    /// it. Like every other call, it never throws and is reported to the editor (<see cref="ILiveEditor.Called"/>).</summary>
    private ApiReply CallUi(string method, SvJsonObject args)
    {
        Task<ApiReply>? pending = null;
        ApiReply reply = _dispatch(() =>
        {
            try
            {
                pending = _editor.Ui(method, args);
                return ApiReply.Ok(SvJsonNode.Null);
            }
            catch (Exception ex)
            {
                return ApiReply.Error(ex as ApiException ?? ApiException.Internal(ex));
            }
        });
        if (!reply.IsError && pending is not null)
        {
            reply = Finished(method, pending);
        }

        _dispatch(() =>
        {
            _editor.Called(method, args, reply);
            return reply;
        });
        return reply;
    }

    private static ApiReply Finished(string method, Task<ApiReply> pending)
    {
        try
        {
            return pending.Wait(s_uiTimeout) ? pending.Result : ApiReply.Error(new ApiException($"the editor did not finish {method} in time"));
        }
        catch (AggregateException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            return ApiReply.Error(inner as ApiException ?? ApiException.Internal(inner));
        }
    }

    private Task<ProposalStatus>? FindDone(int id)
    {
        Task<ProposalStatus>? task = null;
        _dispatch(() =>
        {
            task = _editor.Proposals.Find(id)?.Done;
            return ApiReply.Ok(SvJsonNode.Null);
        });
        return task;
    }

    private ApiReply Status(int id, SvJsonObject first)
    {
        if (_editor.Proposals.Find(id) is not { } p)
        {
            return ApiReply.Ok(first);
        }

        SvJsonObject o = p.ToJson();
        if (first["created"] is { } created && p.Status == ProposalStatus.Pending)
        {
            o.Add("created", created);
        }

        return ApiReply.Ok(o);
    }

    /// <summary>
    /// A call that compiles the map (an analysis query, a deep check, a headless test round), run here on a snapshot
    /// of the open map so the editor stays responsive for the minutes a first nav graph build or a test round takes.
    /// The snapshot starts from the editor's compiled analysis map when it is current, and a map compiled here is
    /// handed back to the editor's cache (dropped there if the map was edited meanwhile).
    /// </summary>
    private ApiReply OnSnapshot(string method, SvJsonObject args)
    {
        EditorSession? snapshot = null;
        AnalysisCache? cache = null;
        int version = 0;
        ApiReply taken = _dispatch(() =>
        {
            EditorSession live = _editor.Session;
            cache = AnalysisCache.Of(live);
            version = cache.Version;
            snapshot = new EditorSession(ProjectSnapshot.Take(live.Project), live.Folder);
            if (cache.Current is { } current)
            {
                AnalysisCache.Of(snapshot).Store(current, AnalysisCache.Of(snapshot).Version);
            }

            return ApiReply.Ok(SvJsonNode.Null);
        });
        if (snapshot is null || cache is null)
        {
            return taken;
        }

        ApiReply reply = new ApiHost(snapshot).Call(method, args);
        if (AnalysisCache.Of(snapshot).Current is { } compiled)
        {
            cache.Store(compiled, version);
        }

        return _dispatch(() =>
        {
            _editor.Called(method, args, reply);
            return reply;
        });
    }

    private ApiReply Guarded(string method, SvJsonObject args)
    {
        ApiReply reply;
        try
        {
            reply = Invoke(method, args);
        }
        catch (ApiException ex)
        {
            reply = ApiReply.Error(ex);
        }
        catch (Exception ex)
        {
            // The editor keeps running whatever a script sends.
            reply = ApiReply.Error(ApiException.Internal(ex));
        }

        _editor.Called(method, args, reply);
        return reply;
    }

    private ApiReply Invoke(string method, SvJsonObject args)
    {
        switch (method)
        {
            case "propose":
                return Propose(args);
            case "proposal":
                return _editor.Proposals.Find(args["id"] is null ? null : (int)ApiJson.Number(args, "id", 0)) is { } p
                    ? ApiReply.Ok(p.ToJson())
                    : throw new ApiException("no such proposal", "id");
            case "editor":
                return ApiReply.Ok(_editor.State());
            case "open" or "new" when _editor.Session.IsDirty:
                throw new ApiException($"the map open in the editor ({_editor.Session.Project.Id}) has unsaved changes: save it " +
                    "(method save) before opening another map, or have the user save or discard it", "method");
        }

        return Host().Call(method, args);
    }

    private ApiReply Propose(SvJsonObject args)
    {
        SvJsonNode doc = args["changes"] ?? Without(args, "wait", "changes");
        string? author = doc is SvJsonObject d && d["author"] is not null ? null : "script";
        Proposal p = _editor.Proposals.Open(_editor.Session, doc, author);
        if (p.Status == ProposalStatus.Failed)
        {
            return new ApiReply(true, p.Outcome ?? ApiJson.Obj(("message", ApiJson.Str("the change document failed"))));
        }

        SvJsonObject o = p.Preview.Result.Json;
        o.Add("proposal", SvJsonNode.Integer(p.Id));
        o.Add("status", ApiJson.Str("pending"));
        return ApiReply.Ok(o);
    }

    /// <summary>The API host over the editor's open session (a new one when the editor opened another map).</summary>
    private ApiHost Host()
    {
        if (_host?.Session != _editor.Session)
        {
            _host = new ApiHost(_editor.Session);
            _host.SessionChanged += _editor.ReplaceSession;
        }

        return _host;
    }

    private static SvJsonObject With(SvJsonObject o, string key, string value)
    {
        SvJsonObject copy = Without(o, key);
        copy.Add(key, SvJsonNode.String(value));
        return copy;
    }

    private static SvJsonObject Without(SvJsonObject o, params string[] keys)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (Array.IndexOf(keys, kv.Key) < 0)
            {
                copy.Add(kv.Key, kv.Value);
            }
        }

        return copy;
    }
}
