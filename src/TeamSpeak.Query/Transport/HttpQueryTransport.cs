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
        _http = httpClient ?? new HttpClient();
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

    /// <summary>
    /// Gets or sets the virtual server that scoped commands are addressed to.
    /// </summary>
    /// <remarks>
    /// The WebQuery has no <c>use</c> command; the virtual server is part of the URL, so it is
    /// tracked here instead of on the server.
    /// </remarks>
    public int VirtualServerId { get; set; }

    /// <inheritdoc />
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

        var sid = VirtualServerId == 0 ? _defaultVirtualServerId : VirtualServerId;
        var path = QueryCommandSerializer.ToWebQueryPath(
            command,
            InstanceWideCommands.Contains(command.Name) ? null : sid);

        using var message = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Commands addressed to the instance rather than to a virtual server.
    /// </summary>
    /// <remarks>
    /// These are requested as <c>/version</c>; everything else is <c>/{sid}/{command}</c>.
    /// </remarks>
    private static readonly HashSet<string> InstanceWideCommands = new(StringComparer.Ordinal)
    {
        "version",
        "hostinfo",
        "instanceinfo",
        "instanceedit",
        "bindinglist",
        "serverlist",
        "servercreate",
        "serveridgetbyport",
        "whoami",
        "logout",
        "apikeylist",
        "apikeyadd",
        "apikeydel",
    };

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}