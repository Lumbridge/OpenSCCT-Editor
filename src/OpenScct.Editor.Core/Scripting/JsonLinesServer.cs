using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting.Live;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// The scripting API over JSON lines: one request per line, <c>{"id":1,"method":"apply","params":{...}}</c>, one response
/// per line, <c>{"id":1,"result":{...}}</c> or <c>{"id":1,"error":{"message":..,"op":..,"field":..}}</c>. Works over
/// any reader and writer (stdio for <c>ose serve</c>, a socket or pipe for the app). <c>exit</c> or end of input stops it.
/// </summary>
public sealed class JsonLinesServer
{
    private readonly IApiEndpoint _host;

    public JsonLinesServer(IApiEndpoint host) => _host = host;

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

            (SvJsonObject response, bool stop) = Handle(line);
            await output.WriteLineAsync(ApiJson.Write(response).AsMemory(), cancel).ConfigureAwait(false);
            await output.FlushAsync(cancel).ConfigureAwait(false);
            if (stop)
            {
                return;
            }
        }
    }

    /// <summary>Answers one request line.</summary>
    public (SvJsonObject Response, bool Stop) Handle(string line)
    {
        SvJsonNode? id = null;
        try
        {
            if (ApiJson.Parse(line, "request") is not SvJsonObject req)
            {
                throw new ApiException("a request is {\"id\":..,\"method\":..,\"params\":{..}}");
            }

            id = req["id"];
            string method = ApiJson.GetString(req, "method") ?? throw new ApiException("missing method", "method");
            if (method == "exit")
            {
                return (Response(id, "result", ApiJson.Obj(("bye", SvJsonNode.True))), true);
            }

            SvJsonObject? args = req["params"] switch
            {
                null => null,
                SvJsonObject o => o,
                _ => throw new ApiException("params is an object", "params"),
            };
            ApiReply reply = _host.Call(method, args);
            return (Response(id, reply.IsError ? "error" : "result", reply.Body), false);
        }
        catch (ApiException ex)
        {
            return (Response(id, "error", ex.ToJson()), false);
        }
    }

    private static SvJsonObject Response(SvJsonNode? id, string kind, SvJsonNode body) =>
        ApiJson.Obj(("id", id ?? SvJsonNode.Null), (kind, body));
}
