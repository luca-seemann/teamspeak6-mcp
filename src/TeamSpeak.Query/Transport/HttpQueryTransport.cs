using System.Net;
using System.Net.Http.Headers;

using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Transport;

/// <summary>
/// Talks to the ServerQuery interface over the HTTP or HTTPS WebQuery.
/// </summary>
/// <remarks>
/// <para>
/// Authentication is the <c>x-api-key</c> header and nothing else. HTTP Basic Auth is refused even
/// with correct <c>serveradmin</c> credentials, despite what the official documentation says, and
/// keys can only be minted over SSH with <c>apikeyadd</c>. Since 6.0.0-beta13 a request without the
/// header is not refused but answered as the ServerQuery guest, which is what a guest profile uses;
/// <c>login</c> is not a way out of that, as guests are refused it with <c>5120 out of scope</c>.
/// </para>
/// <para>
/// The server answers every request with <c>Connection: close</c>, so each command costs a fresh
/// TCP connection no matter what the client does. That makes this the slower interface for chatty
/// work, and it cannot deliver events at all.
/// </para>
/// </remarks>
public sealed class HttpQueryTransport : IQueryTransport
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly FloodGuard _guard;
    private readonly SemaphoreSlim _sequenceGate = new(1, 1);
    private readonly int _defaultVirtualServerId;
    private readonly TimeSpan _commandTimeout;

    /// <summary>Creates a transport for a profile.</summary>
    /// <param name="profile">The server to talk to. Must have a WebQuery URL and an API key.</param>
    /// <param name="httpClient">
    /// An HTTP client to borrow. When omitted, one is created and disposed with this transport.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the profile lacks a WebQuery URL, or an API key it is not a guest profile.
    /// </exception>
    public HttpQueryTransport(QueryProfile profile, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.CanUseWebQuery)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' needs a WebQuery URL and either an API key or the guest login.");
        }

        _ownsClient = httpClient is null;

        // The server closes every connection, so pooling one and reusing it fails with a truncated
        // response. A zero pooled lifetime keeps HttpClient from trying.
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            // Each request carries its own timeout instead, so a command known to run long can wait
            // longer than the rest. A borrowed client keeps whatever limit its owner gave it.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _commandTimeout = profile.CommandTimeout;

        _http.BaseAddress ??= EnsureTrailingSlash(profile.WebQueryUrl!);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // A guest sends no key at all: the server answers a request without the header as the
        // ServerQuery guest, while a header carrying nothing useful is an invalid key (5122).
        if (!profile.IsGuest && !_http.DefaultRequestHeaders.Contains(ApiKeyHeader))
        {
            _http.DefaultRequestHeaders.Add(ApiKeyHeader, profile.ApiKey);
        }

        _guard = new FloodGuard(profile.CommandInterval);
        _defaultVirtualServerId = profile.DefaultVirtualServerId;
    }

    /// <summary>The header the WebQuery authenticates with.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <inheritdoc />
    /// <remarks>
    /// Always <see langword="false"/>. Events need a persistent session, which the WebQuery does
    /// not provide.
    /// </remarks>
    public bool SupportsEvents => false;

    /// <inheritdoc />
    /// <remarks>
    /// Always <see langword="true"/>. Requests stand on their own at the HTTP level, but the server
    /// answers them through one internal query client per key that keeps its client id and its
    /// channel between requests: moving it with one request and asking <c>whoami</c> in the next
    /// shows it in the new channel.
    /// </remarks>
    public bool HoldsSession => true;

    /// <inheritdoc />
    /// <remarks>
    /// The virtual server travels in each request's URL, so the one piece of shared state is the
    /// internal client's channel, which only sequences change. Sequences are therefore serialised
    /// among themselves, while ordinary requests keep running alongside them.
    /// </remarks>
    public async Task<T> RunExclusiveAsync<T>(Func<QuerySender, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _sequenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(SendAsync).ConfigureAwait(false);
        }
        finally
        {
            _sequenceGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The WebQuery has no <c>use</c> command and holds no state; the virtual server goes into each
    /// request's URL, so concurrent callers cannot interfere with one another.
    /// </remarks>
    public async Task<QueryResponse> SendAsync(
        QueryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var response = await SendOnceAsync(command, cancellationToken).ConfigureAwait(false);

        if (response.Error.IsFlooding)
        {
            _guard.PenaliseFor(response.Error.RetryAfter);
            response = await SendOnceAsync(command, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown; the WebQuery has no event stream.</exception>
    public IAsyncEnumerable<QueryEvent> GetEventsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The WebQuery interface cannot deliver events. Use the SSH transport for servernotifyregister.");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _guard.Dispose();
        _sequenceGate.Dispose();

        if (_ownsClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<QueryResponse> SendOnceAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        // Measured on 6.0.0-beta12.1: /help answers 404 "not found", and /help/<command> 1538.
        if (string.Equals(command.Name, "help", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The WebQuery does not serve 'help'; it answers 404. The SSH query does.");
        }

        using var lease = await _guard.AcquireAsync(cancellationToken).ConfigureAwait(false);

        var path = QueryCommandSerializer.ToWebQueryPath(
            command,
            QueryCommandScope.IsInstanceWide(command.Name)
                ? null
                : command.VirtualServerId ?? _defaultVirtualServerId);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Say out loud what the server is going to do anyway. Without this HttpClient may hand the
        // request to a pooled connection the server has already closed, which surfaces as a
        // truncated response rather than as a connection error.
        request.Headers.ConnectionClose = true;

        var timeout = command.Timeout ?? _commandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        string body;
        HttpStatusCode status;
        try
        {
            using var message = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            status = message.StatusCode;
            body = await message.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so the deadline did.
            throw new TimeoutException($"No response to '{command.Name}' within {timeout.TotalSeconds:0} seconds.", ex);
        }

        // Errors arrive as a normal JSON envelope with a non-2xx status, so the body is what
        // matters. Only an empty body means something went wrong below the application layer.
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new QueryProtocolException(
                $"The WebQuery returned {(int)status} with an empty body. " +
                "An empty reply usually means the server is refusing traffic after a flood rejection.");
        }

        return WebQueryResponseParser.Parse(body);
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}