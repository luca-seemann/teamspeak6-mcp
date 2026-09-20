using System.Collections.Concurrent;

namespace TeamSpeak.Query.Client;

/// <summary>
/// Keeps connection attempts to one server spaced out.
/// </summary>
/// <remarks>
/// <para>
/// Connections are far more dangerous than commands, and the server says so itself:
/// <c>serverinstance_serverquery_max_connections_per_ip</c> is 5 on a stock server, and
/// <c>_ban_time</c> 600 seconds. Five or six connections in quick succession duly earned an
/// IP-level block that took both query interfaces down for minutes.
/// <see cref="FloodGuard"/> paces commands within a session; this paces the sessions themselves.
/// </para>
/// <para>
/// The state is shared per host so that several profiles pointing at the same server, or a retry
/// racing a fresh connect, still queue behind one another.
/// </para>
/// </remarks>
public sealed class ConnectionThrottle
{
    /// <summary>The default gap between connection attempts to one host.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>The throttle used unless a caller supplies its own.</summary>
    public static ConnectionThrottle Shared { get; } = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;

    /// <summary>Initialises a throttle.</summary>
    /// <param name="interval">The minimum gap between attempts. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeProvider">The clock to pace against. Defaults to the system clock.</param>
    public ConnectionThrottle(TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _interval = interval ?? DefaultInterval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Waits until it is safe to connect to a host, then records the attempt.
    /// </summary>
    /// <param name="host">The server being connected to.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A task that completes when the caller may connect.</returns>
    public async Task WaitForTurnAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var gate = _perHost.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_lastAttempt.TryGetValue(host, out var last))
            {
                var wait = _interval - (_time.GetUtcNow() - last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastAttempt[host] = _time.GetUtcNow();
        }
        finally
        {
            gate.Release();
        }
    }
}