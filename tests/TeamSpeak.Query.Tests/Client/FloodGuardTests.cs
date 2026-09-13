using Microsoft.Extensions.Time.Testing;

using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class FloodGuardTests
{
    [Fact]
    public async Task First_command_goes_through_without_waiting()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        var acquire = guard.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.True(acquire.IsCompleted);
        (await acquire).Dispose();
    }

    [Fact]
    public async Task Second_command_waits_for_the_interval()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        (await guard.AcquireAsync(TestContext.Current.CancellationToken)).Dispose();
        var second = guard.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(150));
        (await second).Dispose();
    }

    [Fact]
    public async Task Commands_are_serialised_so_concurrent_callers_cannot_burst()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        var first = await guard.AcquireAsync(TestContext.Current.CancellationToken);
        var second = guard.AcquireAsync(TestContext.Current.CancellationToken);

        // The second caller cannot proceed while the first still holds its lease, no matter how
        // much time passes.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(second.IsCompleted);

        first.Dispose();
        time.Advance(TimeSpan.FromMilliseconds(150));
        (await second).Dispose();
    }

    [Fact]
    public async Task A_flood_penalty_delays_the_next_command_by_more_than_the_server_asked()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        guard.PenaliseFor(TimeSpan.FromSeconds(1));
        var next = guard.AcquireAsync(TestContext.Current.CancellationToken);

        // A margin is added on top, so exactly one second is not yet enough.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(next.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(250));
        (await next).Dispose();
    }

    [Fact]
    public async Task A_penalty_without_a_stated_delay_still_backs_off()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        guard.PenaliseFor(null);
        var next = guard.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.False(next.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));
        (await next).Dispose();
    }

    [Fact]
    public async Task A_shorter_penalty_never_shortens_an_existing_one()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);

        guard.PenaliseFor(TimeSpan.FromSeconds(5));
        guard.PenaliseFor(TimeSpan.FromSeconds(1));

        var next = guard.AcquireAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(next.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(4));
        (await next).Dispose();
    }

    [Fact]
    public async Task Cancelling_a_wait_releases_the_turnstile_for_the_next_caller()
    {
        var time = new FakeTimeProvider();
        using var guard = new FloodGuard(TimeSpan.FromMilliseconds(150), time);
        using var cts = new CancellationTokenSource();

        (await guard.AcquireAsync(TestContext.Current.CancellationToken)).Dispose();

        var abandoned = guard.AcquireAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

        // A stuck turnstile here would deadlock every later command.
        time.Advance(TimeSpan.FromMilliseconds(150));
        (await guard.AcquireAsync(TestContext.Current.CancellationToken)).Dispose();
    }
}