using Microsoft.Extensions.Time.Testing;

using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class ConnectionThrottleTests
{
    [Fact]
    public async Task First_connection_to_a_host_is_not_delayed()
    {
        var time = new FakeTimeProvider();
        var throttle = new ConnectionThrottle(TimeSpan.FromSeconds(2), time);

        var first = throttle.WaitForTurnAsync("ts.example.com", TestContext.Current.CancellationToken);

        await first;
        Assert.True(first.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_second_connection_to_the_same_host_waits()
    {
        var time = new FakeTimeProvider();
        var throttle = new ConnectionThrottle(TimeSpan.FromSeconds(2), time);

        await throttle.WaitForTurnAsync("ts.example.com", TestContext.Current.CancellationToken);
        var second = throttle.WaitForTurnAsync("ts.example.com", TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));
        await second;
    }

    [Fact]
    public async Task Different_hosts_do_not_wait_for_each_other()
    {
        var time = new FakeTimeProvider();
        var throttle = new ConnectionThrottle(TimeSpan.FromSeconds(2), time);

        await throttle.WaitForTurnAsync("a.example.com", TestContext.Current.CancellationToken);
        var other = throttle.WaitForTurnAsync("b.example.com", TestContext.Current.CancellationToken);

        await other;
        Assert.True(other.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_matching_ignores_case()
    {
        var time = new FakeTimeProvider();
        var throttle = new ConnectionThrottle(TimeSpan.FromSeconds(2), time);

        await throttle.WaitForTurnAsync("TS.example.com", TestContext.Current.CancellationToken);
        var second = throttle.WaitForTurnAsync("ts.EXAMPLE.com", TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));
        await second;
    }

    [Fact]
    public async Task A_later_connection_is_not_delayed_once_the_interval_has_passed()
    {
        var time = new FakeTimeProvider();
        var throttle = new ConnectionThrottle(TimeSpan.FromSeconds(2), time);

        await throttle.WaitForTurnAsync("ts.example.com", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));

        var later = throttle.WaitForTurnAsync("ts.example.com", TestContext.Current.CancellationToken);

        await later;
        Assert.True(later.IsCompletedSuccessfully);
    }
}