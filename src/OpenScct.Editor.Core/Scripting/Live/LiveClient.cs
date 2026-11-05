using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>
/// Connects to a running editor's <see cref="LiveServer"/> and forwards API calls to it, so <c>ose serve --live</c>
/// edits the map open in the editor. One call at a time. When the editor goes away, the
/// failed call says so and the next call connects again (to a restarted editor, for example), so a long-lived
/// server outlives the editor it first talked to. The first call that finds a different editor is not sent, so the
/// client learns the target changed. A call is never sent twice.
/// </summary>
public sealed class LiveClient : IApiEndpoint, IDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _lock = new();
    private readonly int? _pid;
    private readonly string _client;
    private Connection? _connection;
    private long _nextId;

    private LiveClient(int? pid, string client, Connection connection)
    {
        _pid = pid;
        _client = client;
        _connection = connection;
        var offered = (ApiJson.StringList(connection.Hello["methods"], "methods") ?? []).ToHashSet(StringComparer.Ordinal);
        Methods = offered.Count == 0 ? LiveMethods.WithApi : LiveMethods.WithApi.Where(m => offered.Contains(m.Name)).ToList();
    }

    /// <summary>The editor connected to now (or last).</summary>
    public LiveInstance Instance { get; private set; } = null!;

    /// <summary>The methods the first editor offered (listed once, at start).</summary>
    public IReadOnlyList<ApiMethod> Methods { get; }

    /// <summary>Connects to the editor with this pid or, without one, to the newest running editor that answers.</summary>
    public static LiveClient Connect(int? pid = null, string client = "ose")
    {
        Connection c = Open(pid, client);
        return new LiveClient(pid, client, c) { Instance = c.Instance };
    }

    public ApiReply Call(string method, SvJsonObject? args)
    {
        lock (_lock)
        {
            if (_connection is { } old && old.IsClosed)
            {
                // The editor went away since the last call: nothing was sent, so connect again first.
                old.Dispose();
                _connection = null;
            }

            if (_connection is null)
            {
                int before = Instance.Pid;
                try
                {
                    _connection = Open(_pid, _client);
                    Instance = _connection.Instance;
                }
                catch (ApiException ex)
                {
                    return ApiReply.Error(new ApiException($"lost the editor and cannot reconnect: {ex.Message}"));
                }

                if (Instance.Pid != before)
                {
                    // Another editor may have another map open: say so before any call edits it.
                    return ApiReply.Error(new ApiException($"the editor (pid {before}) went away; now connected to the editor with pid " +
                        $"{Instance.Pid}{(Instance.Map is null ? "" : $" (map {Instance.Map})")}. This call was not sent: check the open " +
                        "map with the editor method, then call again"));
                }
            }

            long id = ++_nextId;
            SvJsonObject request = ApiJson.Obj(("id", SvJsonNode.Integer(id)), ("method", ApiJson.Str(method)), ("params", args));
            try
            {
                _connection.Writer.WriteLine(ApiJson.Write(request));
                string? line = _connection.Reader.ReadLine();
                if (line is null)
                {
                    return Lost("the editor closed the connection (was it closed?)");
                }

                if (ApiJson.Parse(line, "response") is not SvJsonObject response)
                {
                    return Lost("the editor sent something that is not a response");
                }

                return response["error"] is { } error ? new ApiReply(true, error) : ApiReply.Ok(response["result"] ?? SvJsonNode.Null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ApiException)
            {
                return Lost($"lost the connection to the editor: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>Drops the connection (the next call connects again) and reports the failed call.</summary>
    private ApiReply Lost(string message)
    {
        _connection?.Dispose();
        _connection = null;
        return ApiReply.Error(new ApiException(message + "; whether this call ran is unknown (check with the editor method), " +
            "the next call connects again"));
    }

    private static Connection Open(int? pid, string client)
    {
        if (pid is not null)
        {
            return Open(LiveDiscovery.Find(pid), client);
        }

        // A discovery file can outlive its editor when the pid was reused: try the others before giving up.
        List<LiveInstance> running = LiveDiscovery.Running();
        if (running.Count == 0)
        {
            LiveDiscovery.Find(); // throws the "no editor is running" error
        }

        ApiException? first = null;
        foreach (LiveInstance instance in running)
        {
            try
            {
                return Open(instance, client);
            }
            catch (ApiException ex)
            {
                first ??= ex;
            }
        }

        throw first!;
    }

    private static Connection Open(LiveInstance instance, string client)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            tcp.Connect(IPAddress.Loopback, instance.Port);
            NetworkStream stream = tcp.GetStream();
            var reader = new StreamReader(stream, s_utf8);
            var writer = new StreamWriter(stream, s_utf8) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(ApiJson.Write(ApiJson.Obj(("auth", ApiJson.Str(instance.Token)), ("client", ApiJson.Str(client)))));
            string line = reader.ReadLine() ?? throw new ApiException($"the editor (pid {instance.Pid}) closed the connection");
            if (ApiJson.Parse(line, "hello") is not SvJsonObject hello || hello["error"] is SvJsonObject)
            {
                throw new ApiException($"the editor (pid {instance.Pid}) refused the connection: {line}");
            }

            return new Connection(instance, tcp, reader, writer, hello);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            tcp.Dispose();
            throw new ApiException($"cannot reach the editor (pid {instance.Pid}, port {instance.Port}): {ex.Message}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>One open connection to an editor, after the hello.</summary>
    private sealed record Connection(LiveInstance Instance, TcpClient Tcp, StreamReader Reader, StreamWriter Writer, SvJsonObject Hello)
        : IDisposable
    {
        /// <summary>True when the editor closed its end (readable with nothing to read).</summary>
        public bool IsClosed
        {
            get
            {
                try
                {
                    return Tcp.Client.Poll(0, SelectMode.SelectRead) && Tcp.Client.Available == 0;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
            Tcp.Dispose();
        }
    }
}
