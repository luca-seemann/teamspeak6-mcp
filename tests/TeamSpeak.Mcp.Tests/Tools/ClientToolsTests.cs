using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

public class ClientToolsTests
{
    // Shaped like the captured clientlist -uid -away -groups fixture: two query sessions and a person.
    private static readonly Dictionary<string, string>[] Online =
    [
        new()
        {
            ["clid"] = "4", ["cid"] = "1", ["client_database_id"] = "1", ["client_nickname"] = "serveradmin1",
            ["client_type"] = "1", ["client_away"] = "0", ["client_away_message"] = "",
            ["client_unique_identifier"] = "serveradmin", ["client_servergroups"] = "2",
            ["client_channel_group_id"] = "8",
        },
        new()
        {
            ["clid"] = "1", ["cid"] = "1", ["client_database_id"] = "3", ["client_nickname"] = "Alice",
            ["client_type"] = "0", ["client_away"] = "1", ["client_away_message"] = "brb",
            ["client_unique_identifier"] = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ=",
            ["client_servergroups"] = "6,9", ["client_channel_group_id"] = "8",
        },
    ];

    [Fact]
    public async Task Leaves_out_query_clients_by_default()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientlist", ToolHarness.Records(Online));

        var result = await new ClientTools(harness.Executor).ListClientsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var person = Assert.Single(result.Clients);
        Assert.Equal("Alice", person.Nickname);
        Assert.False(person.IsQueryClient);
    }

    [Fact]
    public async Task Includes_query_clients_on_request()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientlist", ToolHarness.Records(Online));

        var result = await new ClientTools(harness.Executor).ListClientsAsync(
            includeQueryClients: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Clients.Count);
    }

    [Fact]
    public async Task Decodes_groups_away_state_and_identity()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientlist", ToolHarness.Records(Online));

        var person = Assert.Single((await new ClientTools(harness.Executor).ListClientsAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Clients);

        Assert.Equal([6, 9], person.ServerGroupIds);
        Assert.True(person.IsAway);
        Assert.Equal("brb", person.AwayMessage);
        Assert.Equal(1, person.ClientId);
        Assert.Equal(3, person.DatabaseId);
        Assert.Equal(["-uid", "-away", "-groups"], Assert.Single(harness.Transport.SentCommands).Options);
    }

    [Fact]
    public async Task Treats_an_empty_result_set_as_no_clients_rather_than_an_error()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientlist", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var result = await new ClientTools(harness.Executor).ListClientsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Clients);
    }
}