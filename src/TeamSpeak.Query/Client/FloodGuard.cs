using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Client;

/// <summary>
/// Paces commands so the server never sees a burst.
/// </summary>
/// <remarks>
/// <para>
/// TeamSpeak rejects commands sent too quickly with <see cref="QueryErrorCode.Flooding"/>, and
/// pushing on through that rejection escalates to an IP-level block that takes down both query
/// interfaces for minutes. Since a single useful question often costs four or five commands, pacing
/// is a correctness concern rather than an optimisation.
/// </para>
/// <para>
/// The guard serialises callers and holds a minimum gap between commands. When the server does
/// report flooding, <see cref="PenaliseFor"/> extends the next gap by the delay the server itself
/// asked for.
/// </para>
/// </remarks>
public sealed class FloodGuard : IDisposable
{
    /// <summary>
    /// The default gap between commands.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: capturing the command reference issued about 160 commands in one
    /// session at this spacing without ever being throttled.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private long _nextAllowedTicks;

    /// <summary>Initialises a guard.</summary>
    /// <param name="interval">The minimum gap between commands. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeProvider">The clock to pace against. Defaults to the system clock.</param>
    public FloodGuard(TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _interval = interval ?? DefaultInterval;
        _time = timeProvider ?? TimeProvider.System;
        _nextAllowedTicks = _time.GetTimestamp();
    }

    /// <summary>
    /// Waits until it is safe to send, then reserves the slot.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A lease that must be disposed once the command has been sent.</returns>
    /// <remarks>
    /// Callers are serialised: only one command is in flight at a time, which is what keeps
    /// concurrent tool calls from turning into a burst.
    /// </remarks>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var delay = TimeToWait();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }

            return new Lease(this);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    /// <summary>
    /// Records that the server asked the client to slow down.
    /// </summary>
    /// <param name="retryAfter">
    /// The delay the server requested, from <see cref="QueryError.RetryAfter"/>. When it supplied
    /// none, a conservative default is applied instead.
    /// </param>
    /// <remarks>
    /// A margin is added on top so the retry does not land on the very edge of the server's window
    /// and get rejected again, which is what escalates a rejection into a block.
    /// </remarks>
    public void PenaliseFor(TimeSpan? retryAfter)
    {
        var penalty = (retryAfter ?? TimeSpan.FromSeconds(1)) + TimeSpan.FromMilliseconds(250);
        var until = _time.GetTimestamp() + (long)(penalty.TotalSeconds * _time.TimestampFrequency);

        if (until > Interlocked.Read(ref _nextAllowedTicks))
        {
            Interlocked.Exchange(ref _nextAllowedTicks, until);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _turnstile.Dispose();

    private TimeSpan TimeToWait()
    {
        var now = _time.GetTimestamp();
        var next = Interlocked.Read(ref _nextAllowedTicks);

        return next <= now
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((next - now) / (double)_time.TimestampFrequency);
    }

    private void ReleaseTurn()
    {
        var next = _time.GetTimestamp()
                   + (long)(_interval.TotalSeconds * _time.TimestampFrequency);

        if (next > Interlocked.Read(ref _nextAllowedTicks))
        {
            Interlocked.Exchange(ref _nextAllowedTicks, next);
        }

        _turnstile.Release();
    }

    private sealed class Lease(FloodGuard guard) : IDisposable
    {
        private FloodGuard? _guard = guard;

        public void Dispose() => Interlocked.Exchange(ref _guard, null)?.ReleaseTurn();
    }
}