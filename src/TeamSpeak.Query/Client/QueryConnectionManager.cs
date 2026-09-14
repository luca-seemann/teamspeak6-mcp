using System.Collections.Concurrent;

using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Client;

/// <summary>
/// Opens a transport for a profile, using the interface the manager chose for it.
/// </summary>
/// <param name="profile">The profile to connect.</param>
/// <param name="transport">The resolved interface; never <see cref="PreferredTransport.Auto"/>.</param>
/// <param name="cancellationToken">Abandons the connection attempt.</param>
/// <returns>A ready transport.</returns>
public delegate Task<IQueryTransport> QueryTransportFactory(
    QueryProfile profile,
    PreferredTransport transport,
    CancellationToken cancellationToken);

/// <summary>
/// Owns one long-lived connection per profile and hands it to every caller.
/// </summary>
/// <remarks>
/// <para>
/// Connections cost far more than commands on a TeamSpeak 6 server: one session carried 160
/// commands without complaint, while five or six connections in quick succession earned an
/// IP-level block. A tool call must therefore never open its own connection. Every caller for a
/// profile shares one transport, opened on first use and kept until the host shuts down.
/// </para>
/// <para>
/// The manager holds no MCP session state, so it is equally correct behind the stateless
/// Streamable HTTP transport, where consecutive tool calls need not share a session.
/// </para>
/// </remarks>
public sealed class QueryConnectionManager : IAsyncDisposable
{
    private readonly QueryTransportFactory _factory;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    /// <summary>Initialises a manager over a set of profiles.</summary>
    /// <param name="profiles">The configured profiles.</param>
    /// <param name="factory">
    /// Opens transports. Defaults to the real SSH and WebQuery transports; tests substitute a fake.
    /// </param>
    public QueryConnectionManager(ProfileRegistry profiles, QueryTransportFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        Profiles = profiles;
        _factory = factory ?? OpenAsync;
    }

    /// <summary>Gets the profiles this manager connects to.</summary>
    public ProfileRegistry Profiles { get; }

    /// <summary>
    /// Decides which interface a profile will actually use.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <returns><see cref="PreferredTransport.Ssh"/> or <see cref="PreferredTransport.WebQuery"/>.</returns>
    /// <remarks>
    /// <see cref="PreferredTransport.Auto"/> prefers SSH whenever it is configured: one session
    /// carries every command, and it is the only interface that delivers events. The WebQuery pays for
    /// a new TCP connection on every request.
    /// </remarks>
    public static PreferredTransport ResolveTransport(QueryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Transport switch
        {
            PreferredTransport.Auto => profile.CanUseSsh ? PreferredTransport.Ssh : PreferredTransport.WebQuery,
            var explicitChoice => explicitChoice,
        };
    }

    /// <summary>
    /// Gets the shared transport for a profile, opening it on first use.
    /// </summary>
    /// <param name="profileName">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <param name="cancellationToken">Abandons the wait for a connection.</param>
    /// <returns>The transport, shared with every other caller for the same profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile cannot be resolved.</exception>
    /// <exception cref="ObjectDisposedException">Thrown after the manager has been disposed.</exception>
    /// <remarks>
    /// A failed attempt is not remembered, so the next call tries again. That is safe because the
    /// SSH transport paces its own connection attempts per host.
    /// </remarks>
    public async Task<IQueryTransport> GetTransportAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var profile = Profiles.Resolve(profileName);
        var connection = _connections.GetOrAdd(profile.Name, static _ => new Connection());

        if (connection.Transport is { } open)
        {
            return open;
        }

        await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.Transport is { } openedMeanwhile)
            {
                return openedMeanwhile;
            }

            var transport = await _factory(profile, ResolveTransport(profile), cancellationToken)
                .ConfigureAwait(false);

            if (_disposed != 0)
            {
                // Shutdown began while this connection was being opened; nobody would close it.
                await transport.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(QueryConnectionManager));
            }

            connection.Transport = transport;
            return transport;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Closing each session properly matters: an abandoned SSH session occupies a query slot until
    /// the server's idle timeout reaps it, which was about 30 seconds on the test server.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var connection in _connections.Values)
        {
            if (connection.Transport is { } transport)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IQueryTransport> OpenAsync(
        QueryProfile profile,
        PreferredTransport transport,
        CancellationToken cancellationToken) =>
        transport == PreferredTransport.Ssh
            ? await SshQueryTransport.ConnectAsync(profile, cancellationToken).ConfigureAwait(false)
            : new HttpQueryTransport(profile);

    private sealed class Connection
    {
        private volatile IQueryTransport? _transport;

        // Never disposed: a waiter may still hold it while the manager shuts down, and a
        // SemaphoreSlim without an AvailableWaitHandle owns nothing that needs releasing.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IQueryTransport? Transport
        {
            get => _transport;
            set => _transport = value;
        }
    }
}