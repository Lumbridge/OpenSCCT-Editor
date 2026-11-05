using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>
/// Serves the scripting API of a running editor to local clients (<c>ose serve --live</c>).
/// It listens on 127.0.0.1 only, on a free port, and writes a discovery file with the port and a random token. A
/// client's first line must be <c>{"auth":TOKEN,"client":NAME}</c>; after the server's hello the connection speaks
/// the JSON-lines protocol of <see cref="JsonLinesServer"/>. Calls go to the endpoint, which marshals them onto the
/// editor's main thread, so live edits land on the editor's own undo stack.
/// </summary>
public sealed class LiveServer : IDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long a new connection has to send its auth line.</summary>
    private static readonly TimeSpan s_authTimeout = TimeSpan.FromSeconds(10);

    private readonly IApiEndpoint _endpoint;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, string> _clients = new();
    private readonly object _lock = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private TcpListener? _listener;
    private int _nextClient;

    public LiveServer(IApiEndpoint endpoint, string version)
    {
        _endpoint = endpoint;
        _version = version;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    }

    public string Token { get; }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Raised (on a server thread) when a client connects or leaves.</summary>
    public event Action? ClientsChanged;

    /// <summary>Raised (on a server thread) for problems worth logging: a refused client, a dropped connection.</summary>
    public event Action<string>? Problem;

    /// <summary>The names of the connected clients.</summary>
    public IReadOnlyList<string> Clients
    {
        get
        {
            lock (_lock)
            {
                return _clients.Values.ToList();
            }
        }
    }

    /// <summary>Starts listening and writes the discovery file.</summary>
    public void Start(string? map, string? folder)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        UpdateMap(map, folder);
        _ = AcceptLoopAsync(_listener, _stop.Token);
    }

    /// <summary>Rewrites the discovery file with the open map (after another map is opened).</summary>
    public void UpdateMap(string? map, string? folder)
    {
        if (_listener is not null)
        {
            LiveDiscovery.Write(Port, Token, map, folder, _version, _started);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _listener = null;
        LiveDiscovery.Remove(Environment.ProcessId);
        // The token source is not disposed: connections still running observe it as they wind down.
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, cancel);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        int id = Interlocked.Increment(ref _nextClient);
        bool joined = false;
        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, s_utf8);
                using var writer = new StreamWriter(stream, s_utf8) { AutoFlush = true, NewLine = "\n" };
                string? name = await AuthenticateAsync(reader, writer, cancel).ConfigureAwait(false);
                if (name is null)
                {
                    return;
                }

                lock (_lock)
                {
                    _clients[id] = name;
                }

                joined = true;
                ClientsChanged?.Invoke();
                await new JsonLinesServer(_endpoint).RunAsync(reader, writer, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Problem?.Invoke($"Live connection closed: {ex.Message}");
            }
        }
        finally
        {
            if (joined)
            {
                lock (_lock)
                {
                    _clients.Remove(id);
                }

                ClientsChanged?.Invoke();
            }
        }
    }

    /// <summary>Reads the auth line and answers with the hello; returns the client's name, or null when refused.</summary>
    private async Task<string?> AuthenticateAsync(StreamReader reader, StreamWriter writer, CancellationToken cancel)
    {
        string? line;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(s_authTimeout);
            try
            {
                line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                Problem?.Invoke("Dropped a live connection that sent no token.");
                return null;
            }
        }

        SvJsonObject? auth = null;
        try
        {
            auth = line is null ? null : ApiJson.Parse(line, "auth") as SvJsonObject;
        }
        catch (ApiException)
        {
        }

        string? token = ApiJson.GetString(auth, "auth");
        if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Token)))
        {
            await writer.WriteLineAsync(ApiJson.Write(ApiJson.Obj(("error", ApiJson.Obj(("message", ApiJson.Str("bad or missing token"))))))
                .AsMemory(), cancel).ConfigureAwait(false);
            Problem?.Invoke("Refused a live connection with a bad token.");
            return null;
        }

        string name = ApiJson.GetString(auth, "client") is { Length: > 0 } n ? n : "client";
        SvJsonObject hello = ApiJson.Obj(
            ("ok", SvJsonNode.True),
            ("server", ApiJson.Str("openscct-editor")),
            ("version", ApiJson.Str(_version)),
            ("pid", SvJsonNode.Integer(Environment.ProcessId)),
            ("methods", ApiJson.Strings(_endpoint.Methods.Select(m => m.Name))));
        await writer.WriteLineAsync(ApiJson.Write(hello).AsMemory(), cancel).ConfigureAwait(false);
        return name;
    }
}
