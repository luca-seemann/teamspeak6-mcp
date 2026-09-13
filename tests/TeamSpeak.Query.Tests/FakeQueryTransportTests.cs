using TeamSpeak.Query.FakeServer;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests;

public class FakeQueryTransportTests
{
    [Fact]
    public async Task Returns_registered_response_and_records_the_command()
    {
        var expected = new QueryResponse(
            [new Dictionary<string, string> { ["virtualserver_id"] = "1" }],
            new QueryError(0, "ok"));

        await using var transport = new FakeQueryTransport().Returns("whoami", expected);

        var actual = await transport.SendAsync(new QueryCommand("whoami"), TestContext.Current.CancellationToken);

        Assert.Same(expected, actual);
        Assert.Equal("whoami", Assert.Single(transport.SentCommands).Name);
    }

    [Fact]
    public async Task Unregistered_command_yields_the_command_not_found_error()
    {
        await using var transport = new FakeQueryTransport();

        var response = await transport.SendAsync(new QueryCommand("nonexistent"), TestContext.Current.CancellationToken);

        Assert.False(response.Error.IsSuccess);
        Assert.Equal(256, response.Error.Id);
    }
}