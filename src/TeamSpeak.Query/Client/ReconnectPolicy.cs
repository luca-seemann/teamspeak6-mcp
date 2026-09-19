namespace TeamSpeak.Query.Client;

/// <summary>
/// Decides how long to wait before trying a dropped connection again.
/// </summary>
/// <remarks>
/// Reconnecting is not free against a TeamSpeak server: a flurry of connection attempts is itself
/// what the flood protection punishes, and the block it hands out takes down both query interfaces
/// for minutes. So the delay grows quickly, is capped, and carries jitter, because several profiles or
/// several processes recovering from the same outage must not march in step.
/// </remarks>
public sealed class ReconnectPolicy
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _maximum;
    private readonly int _maxAttempts;

    /// <summary>Initialises a policy.</summary>
    /// <param name="initial">The delay before the first retry.</param>
    /// <param name="maximum">The ceiling the delay grows to.</param>
    /// <param name="maxAttempts">How many attempts to make before giving up.</param>
    public ReconnectPolicy(
        TimeSpan? initial = null,
        TimeSpan? maximum = null,
        int maxAttempts = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        _initial = initial ?? TimeSpan.FromSeconds(2);
        _maximum = maximum ?? TimeSpan.FromMinutes(2);
        _maxAttempts = maxAttempts;
    }

    /// <summary>Gets how many attempts this policy allows.</summary>
    public int MaxAttempts => _maxAttempts;

    /// <summary>
    /// Gets the delay before a given attempt.
    /// </summary>
    /// <param name="attempt">The attempt number, starting at one.</param>
    /// <param name="random">A source of jitter. Defaults to <see cref="Random.Shared"/>.</param>
    /// <returns>How long to wait, or <see langword="null"/> when the policy has given up.</returns>
    /// <remarks>
    /// The delay doubles per attempt up to the ceiling, then has up to 25% subtracted at random so
    /// that simultaneous reconnects spread out instead of arriving together.
    /// </remarks>
    public TimeSpan? DelayBefore(int attempt, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        if (attempt > _maxAttempts)
        {
            return null;
        }

        // Cap the exponent before shifting so a large attempt count cannot overflow.
        var exponent = Math.Min(attempt - 1, 20);
        var scaled = _initial * Math.Pow(2, exponent);
        var capped = scaled > _maximum ? _maximum : scaled;

        var jitter = (random ?? Random.Shared).NextDouble() * 0.25;
        return capped * (1 - jitter);
    }
}