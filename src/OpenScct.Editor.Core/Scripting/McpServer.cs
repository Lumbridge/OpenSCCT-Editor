using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting.Live;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>Exposes the public editor scripting API as MCP tools over UTF-8 stdio.</summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    private readonly IApiEndpoint _host;
    private readonly string _version;

    public McpServer(IApiEndpoint host, string version = "0.1.0")
    {
        _host = host;
        _version = version;
    }

    /// <summary>Short guidance sent with initialize; clients show it to the model.</summary>
    public const string Instructions =
        "Start with map_summary or editor_state. Read narrowly with map_query (fields, rows:true, limit) and batch related queries. " +
        "Use map_help for one operation or query example at a time. Group related changes in one map_apply with dryRun:true first, " +
        "or show a visual preview with map_propose in a live editor.";

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancel = default)
    {
        while (!cancel.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancel).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (Handle(line) is { } response)
            {
                await output.WriteLineAsync(ApiJson.Write(response).AsMemory(), cancel).ConfigureAwait(false);
                await output.FlushAsync(cancel).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Answers one message, or returns null for notifications and client responses.</summary>
    public SvJsonObject? Handle(string line)
    {
        SvJsonNode message;
        try
        {
            message = ApiJson.Parse(line, "message");
        }
        catch (ApiException ex)
        {
            return Error(SvJsonNode.Null, ParseError, ex.Message);
        }

        if (message is not SvJsonObject req)
        {
            return Error(SvJsonNode.Null, InvalidRequest, "expected a JSON-RPC object");
        }

        SvJsonNode? id = req["id"];
        string? method = ApiJson.GetString(req, "method");
        if (ApiJson.GetString(req, "jsonrpc") != "2.0" || id is not (null or SvJsonString or SvJsonNumber) && id != SvJsonNode.Null)
        {
            return Error(SvJsonNode.Null, InvalidRequest, "expected a JSON-RPC 2.0 message with a valid id");
        }
        if (method is null)
        {
            return id is not null && req["method"] is null && (req["result"] is not null || req["error"] is not null)
                ? null : Error(id ?? SvJsonNode.Null, InvalidRequest, "missing method");
        }
        if (id is null)
        {
            return null;
        }

        if (req["params"] is { } parameters && parameters is not SvJsonObject)
        {
            return Error(id, InvalidParams, "params must be an object");
        }

        var args = req["params"] as SvJsonObject ?? new SvJsonObject();
        return method switch
        {
            "initialize" => Result(id, Initialize()),
            "ping" => Result(id, new SvJsonObject()),
            "tools/list" => Result(id, ToolsList()),
            "tools/call" => CallTool(id, args),
            _ => Error(id, MethodNotFound, $"method not found: {method}"),
        };
    }

    private SvJsonObject Initialize() => ApiJson.Obj(
        ("protocolVersion", ApiJson.Str(ProtocolVersion)),
        ("capabilities", ApiJson.Obj(("tools", ApiJson.Obj(("listChanged", SvJsonNode.False))))),
        ("serverInfo", ApiJson.Obj(("name", ApiJson.Str("openscct-editor")), ("title", ApiJson.Str("OpenSCCT Editor")),
            ("version", ApiJson.Str(_version)))),
        ("instructions", ApiJson.Str(Instructions)));

    private IEnumerable<McpTool> Tools => _host.Methods
        .Where(m => McpTool.Supports(m.Name))
        .Select(m => new McpTool(m))
        .Append(new McpTool(McpHelp.Method));

    private SvJsonObject ToolsList() => ApiJson.Obj(("tools", ApiJson.Arr(Tools.Select(tool => (SvJsonNode)tool.Describe()))));

    private SvJsonObject CallTool(SvJsonNode id, SvJsonObject args)
    {
        string? name = ApiJson.GetString(args, "name");
        McpTool? tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return Error(id, InvalidParams, $"unknown tool: {name}");
        }

        if (args["arguments"] is { } a && a is not SvJsonObject)
        {
            return Error(id, InvalidParams, "arguments must be an object");
        }

        var arguments = args["arguments"] as SvJsonObject;
        ApiReply reply = tool.Name == "map_help" ? McpHelp.Read(Tools, arguments) : _host.Call(tool.Method.Name, arguments);
        var content = new SvJsonArray([ApiJson.Obj(("type", ApiJson.Str("text")), ("text", ApiJson.Str(ApiJson.Write(reply.Body))))]);
        // Object results also go as structured content (the text block stays for clients that only read text).
        return Result(id, ApiJson.Obj(("content", content), ("structuredContent", reply.Body as SvJsonObject),
            ("isError", ApiJson.Bool(reply.IsError))));
    }

    private static SvJsonObject Result(SvJsonNode id, SvJsonNode result) =>
        ApiJson.Obj(("jsonrpc", ApiJson.Str("2.0")), ("id", id), ("result", result));

    private static SvJsonObject Error(SvJsonNode id, int code, string message) =>
        ApiJson.Obj(("jsonrpc", ApiJson.Str("2.0")), ("id", id),
            ("error", ApiJson.Obj(("code", SvJsonNode.Integer(code)), ("message", ApiJson.Str(message)))));
}
