using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class ReconnectPolicyTests
{
    // A fixed seed keeps the jitter deterministic without pinning an exact value.
    private static Random Fixed() => new(1234);

    [Fact]
    public void Backs_off_exponentially()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2), maxAttempts: 6);

        var first = policy.DelayBefore(1, Fixed())!.Value;
        var second = policy.DelayBefore(2, Fixed())!.Value;
        var third = policy.DelayBefore(3, Fixed())!.Value;

        Assert.True(second > first, $"{second} should exceed {first}");
        Assert.True(third > second, $"{third} should exceed {second}");
    }

    [Fact]
    public void Never_exceeds_the_ceiling()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), maxAttempts: 10);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.True(policy.DelayBefore(attempt, Fixed())!.Value <= TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Gives_up_after_the_attempt_limit()
    {
        var policy = new ReconnectPolicy(maxAttempts: 3);

        Assert.NotNull(policy.DelayBefore(3));
        Assert.Null(policy.DelayBefore(4));
    }

    [Fact]
    public void Applies_jitter_so_simultaneous_reconnects_spread_out()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        var delays = Enumerable.Range(0, 20).Select(_ => policy.DelayBefore(3)!.Value).ToList();

        // Several processes recovering from one outage must not all return at the same instant.
        Assert.True(delays.Distinct().Count() > 1, "every delay was identical, so there is no jitter");
    }

    [Fact]
    public void Jitter_only_ever_shortens_the_delay()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        foreach (var delay in Enumerable.Range(0, 50).Select(_ => policy.DelayBefore(1)!.Value))
        {
            Assert.InRange(delay, TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Rejects_a_nonsensical_attempt_number()
    {
        var policy = new ReconnectPolicy();

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayBefore(0));
    }
}