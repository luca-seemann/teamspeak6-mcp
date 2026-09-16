using System.Net;

using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Tests.Transport;

public class HttpQueryTransportTests
{
    private static QueryProfile Profile() => new()
    {
        Name = "p",
        Host = "ts.example.com",
        WebQueryUrl = new Uri("http://ts.example.com:10080"),
        ApiKey = "test-key",
        // Keep the tests fast; the real default paces at 150 ms.
        CommandInterval = TimeSpan.Zero,
    };

    [Fact]
    public async Task Sends_the_api_key_header_and_no_basic_auth()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await transport.SendAsync(new QueryCommand("version"), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-key", Assert.Single(request.Headers.GetValues(HttpQueryTransport.ApiKeyHeader)));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Refuses_help_without_sending_anything()
    {
        // Measured: the WebQuery answers /help with 404 "not found".
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            transport.SendAsync(new QueryCommand("help"), TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Addresses_instance_wide_commands_without_a_virtual_server()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await transport.SendAsync(new QueryCommand("version"), TestContext.Current.CancellationToken);

        Assert.Equal("/version", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Scopes_other_commands_to_the_virtual_server_they_name()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await transport.SendAsync(
            new QueryCommand("clientinfo", new Dictionary<string, string> { ["clid"] = "3" }, VirtualServerId: 4),
            TestContext.Current.CancellationToken);

        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("/4/clientinfo", uri.AbsolutePath);
        Assert.Equal("?clid=3", uri.Query);
    }

    [Fact]
    public async Task Falls_back_to_the_profile_default_virtual_server()
    {
        var handler = new StubHandler(Ok);
        var profile = Profile();
        profile.DefaultVirtualServerId = 7;
        await using var transport = new HttpQueryTransport(profile, new HttpClient(handler));

        await transport.SendAsync(new QueryCommand("channellist"), TestContext.Current.CancellationToken);

        Assert.Equal("/7/channellist", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ignores_a_virtual_server_on_an_instance_wide_command()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await transport.SendAsync(
            new QueryCommand("serverlist", VirtualServerId: 4),
            TestContext.Current.CancellationToken);

        Assert.Equal("/serverlist", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Retries_once_after_the_server_reports_flooding()
    {
        var responses = new Queue<string>([
            """{"status":{"code":524,"extra_message":"please wait 1 seconds","message":"client is flooding"}}""",
            Ok,
        ]);
        var handler = new StubHandler(() => responses.Dequeue());
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        var response = await transport.SendAsync(
            new QueryCommand("version"),
            TestContext.Current.CancellationToken);

        Assert.True(response.Error.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Explains_an_empty_reply_rather_than_failing_obscurely()
    {
        var handler = new StubHandler(() => string.Empty);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<QueryProtocolException>(
            () => transport.SendAsync(new QueryCommand("version"), TestContext.Current.CancellationToken));

        // An empty reply is what a flood-blocked server looks like, so the message says so.
        Assert.Contains("flood", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_that_it_cannot_deliver_events()
    {
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(new StubHandler(Ok)));

        Assert.False(transport.SupportsEvents);
        Assert.Throws<NotSupportedException>(
            () => transport.GetEventsAsync(TestContext.Current.CancellationToken));
        await Task.CompletedTask;
    }

    [Fact]
    public void Refuses_a_profile_without_a_key()
    {
        var profile = new QueryProfile { Name = "p", Host = "h", Password = "secret" };

        Assert.Throws<InvalidOperationException>(() => new HttpQueryTransport(profile));
    }

    [Fact]
    public async Task Runs_exclusive_sequences_one_at_a_time_for_its_lasting_internal_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(new StubHandler(Ok)));

        // The WebQuery's internal client keeps its channel between requests, so it counts as a session.
        Assert.True(transport.HoldsSession);

        var inside = 0;
        var mostInside = 0;

        async Task<int> Sequence(QuerySender send)
        {
            var now = Interlocked.Increment(ref inside);
            mostInside = Math.Max(mostInside, now);
            await send(new QueryCommand("version"), ct);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref inside);
            return now;
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => transport.RunExclusiveAsync(Sequence, ct)));

        Assert.Equal(1, mostInside);
    }

    [Fact]
    public async Task Lets_a_command_with_its_own_timeout_wait_longer_than_the_profile_allows()
    {
        var profile = Profile();
        profile.CommandTimeout = TimeSpan.FromMilliseconds(50);
        await using var transport = new HttpQueryTransport(profile, new HttpClient(new StubHandler(Ok, TimeSpan.FromMilliseconds(300))));

        var response = await transport.SendAsync(
            new QueryCommand("serversnapshotdeploy", Timeout: TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Error.IsSuccess);
    }

    [Fact]
    public async Task Reports_a_missed_deadline_as_a_timeout_rather_than_a_cancellation()
    {
        var profile = Profile();
        profile.CommandTimeout = TimeSpan.FromMilliseconds(50);
        await using var transport = new HttpQueryTransport(profile, new HttpClient(new StubHandler(Ok, TimeSpan.FromSeconds(10))));

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.SendAsync(new QueryCommand("version"), TestContext.Current.CancellationToken));
    }

    private const string Ok =
        """{"body":[{"version":"6.0.0-beta12.1"}],"status":{"code":0,"message":"ok"}}""";

    private sealed class StubHandler(Func<string> body, TimeSpan delay = default) : HttpMessageHandler
    {
        public StubHandler(string constantBody, TimeSpan delay = default)
            : this(() => constantBody, delay)
        {
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body()),
            };
        }
    }
}