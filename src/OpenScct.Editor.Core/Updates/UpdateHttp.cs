using System.Net;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// The HTTP client updates use. Requests name the editor (<c>OpenSCCT-Editor/0.2.0</c>) as their user agent. A
/// download whose server stops sending fails after <see cref="StallTimeout"/> without data (the downloader then resumes
/// it) instead of hanging. For trying updates out against a local release feed, <c>OSE_UPDATE_API</c> (for example
/// <c>http://127.0.0.1:8770</c>) replaces <c>https://api.github.com</c> in every request; the feed's download links
/// point wherever it likes. Only a feed on this machine (loopback) is accepted, so the variable can never point an
/// editor at someone else's server.
/// </summary>
public static class UpdateHttp
{
    public const string ApiVariable = "OSE_UPDATE_API";
    private const string GitHubApiHost = "api.github.com";

    /// <summary>How long a download may go without receiving anything.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The local feed in use (<see cref="ApiVariable"/> on a loopback address); null for GitHub.</summary>
    public static Uri? ApiOverride { get; } = ParseOverride(Environment.GetEnvironmentVariable(ApiVariable));

    /// <summary>For release checks: a short timeout, so a check never hangs.</summary>
    public static HttpClient ForChecks() => Create(TimeSpan.FromSeconds(10), stallGuard: false);

    /// <summary>For downloads: a long overall timeout (the downloader retries and resumes) and the stall guard.</summary>
    public static HttpClient ForDownloads() => Create(TimeSpan.FromMinutes(30), stallGuard: true);

    private static HttpClient Create(TimeSpan timeout, bool stallGuard)
    {
        var sockets = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        return new HttpClient(new EditorRequests(sockets, stallGuard)) { Timeout = timeout };
    }

    private static Uri? ParseOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return uri.IsLoopback ? uri : null;
    }

    private sealed class EditorRequests(HttpMessageHandler inner, bool stallGuard) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd($"OpenSCCT-Editor/{EditorRelease.Version}");
            if (ApiOverride is { } api && request.RequestUri is { } uri && string.Equals(uri.Host, GitHubApiHost, StringComparison.OrdinalIgnoreCase))
            {
                request.RequestUri = new Uri(api, uri.PathAndQuery.TrimStart('/'));
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (stallGuard)
            {
                response.Content = new StallGuardContent(response.Content);
            }

            return response;
        }
    }

    /// <summary>A response body whose reads fail with an <see cref="IOException"/> after <see cref="StallTimeout"/>
    /// without data. Keeps the original content headers (length, range).</summary>
    private sealed class StallGuardContent : HttpContent
    {
        private readonly HttpContent _inner;

        public StallGuardContent(HttpContent inner)
        {
            _inner = inner;
            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new StallGuardStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using Stream body = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await body.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StallGuardStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            try
            {
                return await inner.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"The server sent nothing for {StallTimeout.TotalSeconds:0} seconds.", ex);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
