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
/// keys can only be minted over SSH with <c>apikeyadd</c>.
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
    private readonly int _defaultVirtualServerId;

    /// <summary>Creates a transport for a profile.</summary>
    /// <param name="profile">The server to talk to. Must have a WebQuery URL and an API key.</param>
    /// <param name="httpClient">
    /// An HTTP client to borrow. When omitted, one is created and disposed with this transport.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the profile lacks a WebQuery URL or an API key.
    /// </exception>
    public HttpQueryTransport(QueryProfile profile, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.CanUseWebQuery)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' needs both a WebQuery URL and an API key.");
        }

        _ownsClient = httpClient is null;

        // The server closes every connection, so pooling one and reusing it fails with a truncated
        // response. A zero pooled lifetime keeps HttpClient from trying.
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            // The 100 second default is far too long for an administrative tool call; a stalled
            // request should surface quickly rather than block the caller for minutes.
            Timeout = profile.CommandTimeout,
        };

        _http.BaseAddress ??= EnsureTrailingSlash(profile.WebQueryUrl!);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!_http.DefaultRequestHeaders.Contains(ApiKeyHeader))
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

        if (_ownsClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<QueryResponse> SendOnceAsync(QueryCommand command, CancellationToken cancellationToken)
    {
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

        using var message = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await message.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Errors arrive as a normal JSON envelope with a non-2xx status, so the body is what
        // matters. Only an empty body means something went wrong below the application layer.
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new QueryProtocolException(
                $"The WebQuery returned {(int)message.StatusCode} with an empty body. " +
                "An empty reply usually means the server is refusing traffic after a flood rejection.");
        }

        return WebQueryResponseParser.Parse(body);
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}