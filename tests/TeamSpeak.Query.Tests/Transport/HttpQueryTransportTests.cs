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
    public async Task Addresses_instance_wide_commands_without_a_virtual_server()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler));

        await transport.SendAsync(new QueryCommand("version"), TestContext.Current.CancellationToken);

        Assert.Equal("/version", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Scopes_other_commands_to_the_virtual_server()
    {
        var handler = new StubHandler(Ok);
        await using var transport = new HttpQueryTransport(Profile(), new HttpClient(handler))
        {
            VirtualServerId = 4,
        };

        await transport.SendAsync(
            new QueryCommand("clientinfo", new Dictionary<string, string> { ["clid"] = "3" }),
            TestContext.Current.CancellationToken);

        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("/4/clientinfo", uri.AbsolutePath);
        Assert.Equal("?clid=3", uri.Query);
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

    private const string Ok =
        """{"body":[{"version":"6.0.0-beta12.1"}],"status":{"code":0,"message":"ok"}}""";

    private sealed class StubHandler(Func<string> body) : HttpMessageHandler
    {
        public StubHandler(string constantBody)
            : this(() => constantBody)
        {
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body()),
            });
        }
    }
}