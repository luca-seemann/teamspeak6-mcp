using TeamSpeak.Query.Client;
using TeamSpeak.Query.FakeServer;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Tests.Client;

public class QueryConnectionManagerTests
{
    private static QueryProfile SshProfile(string name = "ssh") =>
        new() { Name = name, Host = "h", Password = "secret" };

    private static QueryProfile WebQueryProfile(string name = "web") =>
        new() { Name = name, Host = "h", WebQueryUrl = new Uri("http://h:10080"), ApiKey = "key" };

    [Fact]
    public async Task Opens_one_connection_per_profile_however_many_callers_ask_at_once()
    {
        var opened = 0;
        var gate = new TaskCompletionSource();

        await using var manager = new QueryConnectionManager(
            new ProfileRegistry([SshProfile()]),
            async (_, _, _) =>
            {
                Interlocked.Increment(ref opened);
                await gate.Task;
                return new FakeQueryTransport();
            });

        var callers = Enumerable.Range(0, 8)
            .Select(_ => manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToArray();

        gate.SetResult();
        var transports = await Task.WhenAll(callers);

        // Connections are what earn an IP block; eight concurrent tool calls must not mean eight.
        Assert.Equal(1, opened);
        Assert.All(transports, transport => Assert.Same(transports[0], transport));
    }

    [Fact]
    public async Task Keeps_separate_connections_for_separate_profiles()
    {
        await using var manager = new QueryConnectionManager(
            new ProfileRegistry([SshProfile("a"), SshProfile("b")]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new FakeQueryTransport()));

        var a = await manager.GetTransportAsync("a", TestContext.Current.CancellationToken);
        var b = await manager.GetTransportAsync("B", TestContext.Current.CancellationToken);

        Assert.NotSame(a, b);
        Assert.Same(a, await manager.GetTransportAsync("A", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Auto_prefers_ssh_when_a_password_is_configured()
    {
        var profile = SshProfile();
        profile.WebQueryUrl = new Uri("http://h:10080");
        profile.ApiKey = "key";

        Assert.Equal(PreferredTransport.Ssh, QueryConnectionManager.ResolveTransport(profile));
    }

    [Fact]
    public void Auto_falls_back_to_the_web_query_without_a_password() =>
        Assert.Equal(PreferredTransport.WebQuery, QueryConnectionManager.ResolveTransport(WebQueryProfile()));

    [Fact]
    public void An_explicit_choice_is_honoured()
    {
        var profile = SshProfile();
        profile.WebQueryUrl = new Uri("http://h:10080");
        profile.ApiKey = "key";
        profile.Transport = PreferredTransport.WebQuery;

        Assert.Equal(PreferredTransport.WebQuery, QueryConnectionManager.ResolveTransport(profile));
    }

    [Fact]
    public async Task Hands_the_factory_the_resolved_transport_never_auto()
    {
        PreferredTransport? requested = null;

        await using var manager = new QueryConnectionManager(
            new ProfileRegistry([WebQueryProfile()]),
            (_, transport, _) =>
            {
                requested = transport;
                return Task.FromResult<IQueryTransport>(new FakeQueryTransport(supportsEvents: false));
            });

        await manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PreferredTransport.WebQuery, requested);
    }

    [Fact]
    public async Task Does_not_remember_a_failed_attempt()
    {
        var attempts = 0;

        await using var manager = new QueryConnectionManager(
            new ProfileRegistry([SshProfile()]),
            (_, _, _) => ++attempts == 1
                ? throw new InvalidOperationException("server unreachable")
                : Task.FromResult<IQueryTransport>(new FakeQueryTransport()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken));

        var transport = await manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(transport);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Closes_every_connection_on_disposal_and_refuses_new_callers()
    {
        var fake = new FakeQueryTransport();
        var manager = new QueryConnectionManager(
            new ProfileRegistry([SshProfile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(fake));

        await manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        // An abandoned SSH session holds a query slot for five minutes.
        Assert.True(fake.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.GetTransportAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reports_an_unknown_profile_by_name()
    {
        await using var manager = new QueryConnectionManager(
            new ProfileRegistry([SshProfile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new FakeQueryTransport()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetTransportAsync("nope", TestContext.Current.CancellationToken));

        Assert.Contains("'nope'", ex.Message, StringComparison.Ordinal);
    }
}