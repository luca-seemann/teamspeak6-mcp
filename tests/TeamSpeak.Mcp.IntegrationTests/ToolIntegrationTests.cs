using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Runs the MCP tools against a live TeamSpeak 6 server, through the real executor, safety policy
/// and connection manager.
/// </summary>
/// <remarks>
/// Over SSH the tools borrow the collection's shared session rather than opening their own. The
/// test instance is nearly empty, so these check that every tool reaches the server and decodes a
/// real response, not the finer points of each result.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class ToolIntegrationTests(LiveServerFixture server)
{
    private QueryConnectionManager SharedSshConnections() =>
        new(
            new ProfileRegistry([LiveServerFixture.Profile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new BorrowedTransport(server.Ssh)));

    [RequiresTeamSpeakServerFact]
    public async Task Instance_and_virtual_server_tools_answer_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        var whoami = await new MetaTools(executor).WhoAmIAsync(cancellationToken: ct);
        Assert.Equal("serveradmin", whoami.Fields["client_login_name"]);

        var instance = await new MetaTools(executor).InstanceInfoAsync(cancellationToken: ct);
        Assert.StartsWith("6.", instance.Version["version"], StringComparison.Ordinal);
        Assert.NotEmpty(instance.BoundAddresses);

        var servers = await new VirtualServerTools(executor).ListVirtualServersAsync(cancellationToken: ct);
        var first = Assert.Single(servers.VirtualServers, candidate => candidate.Id == 1);
        Assert.False(string.IsNullOrEmpty(first.Name));

        var info = await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: ct);
        Assert.Equal(first.Name, info.Fields["virtualserver_name"]);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Channel_and_client_tools_answer_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        var tree = await new ChannelTools(executor).ListChannelsAsync(tree: true, virtualServerId: 1, cancellationToken: ct);
        Assert.NotEmpty(tree.Channels);

        var channel = await new ChannelTools(executor).ChannelInfoAsync(tree.Channels[0].Id, 1, cancellationToken: ct);
        Assert.Equal(tree.Channels[0].Name, channel.Fields["channel_name"]);

        // This session is itself a query client, so the list is never empty with query clients included.
        // Other query sessions may share the login name, so identify this one by its client id; the
        // session has virtual server 1 selected by now, which is what whoami reports against.
        var clients = await new ClientTools(executor).ListClientsAsync(includeQueryClients: true, virtualServerId: 1, cancellationToken: ct);
        var whoami = await new MetaTools(executor).WhoAmIAsync(cancellationToken: ct);
        var self = Assert.Single(clients.Clients, client => client.ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) == whoami.Fields["client_id"]);
        Assert.True(self.IsQueryClient);

        var detail = await new ClientTools(executor).ClientInfoAsync(self.ClientId, 1, cancellationToken: ct);
        Assert.Equal(self.Nickname, detail.Fields["client_nickname"]);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_server_refusal_reaches_the_model_as_a_tool_error()
    {
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        var ex = await Assert.ThrowsAsync<McpException>(() => new ClientTools(executor).ClientInfoAsync(
            99999,
            1,
            cancellationToken: TestContext.Current.CancellationToken));

        // TeamSpeak 6 reports an unknown client id as a conversion error, not as "invalid client".
        Assert.Contains("1540", ex.Message, StringComparison.Ordinal);
    }

    [RequiresTeamSpeakServerFact]
    public async Task The_raw_query_runs_a_read_command_and_refuses_a_change_on_a_read_only_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var tools = new MetaTools(new QueryExecutor(connections, new SafetyPolicy()));

        var result = await tools.QueryRawAsync("channellist", virtualServerId: 1, cancellationToken: ct);
        Assert.NotEmpty(result.Records);
        Assert.Equal("ReadOnly", result.SafetyLevel);

        // An empty value counts as a missing parameter on TeamSpeak 6, with its own status code.
        var missing = await Assert.ThrowsAsync<McpException>(() => tools.QueryRawAsync(
            "channelfind",
            new Dictionary<string, string> { ["pattern"] = "" },
            virtualServerId: 1,
            cancellationToken: ct));
        Assert.Contains("1542", missing.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<McpException>(() => tools.QueryRawAsync(
            "channeledit",
            new Dictionary<string, string> { ["cid"] = "1", ["channel_topic"] = "must never be set" },
            virtualServerId: 1,
            cancellationToken: ct));
    }

    [RequiresWebQueryFact]
    public async Task The_same_tools_answer_over_the_web_query()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = LiveServerFixture.Profile();
        profile.Transport = PreferredTransport.WebQuery;

        // The real factory: the WebQuery holds no session, so opening one here costs nothing lasting.
        await using var connections = new QueryConnectionManager(new ProfileRegistry([profile]));
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        var servers = await new VirtualServerTools(executor).ListVirtualServersAsync(cancellationToken: ct);
        Assert.Contains(servers.VirtualServers, candidate => candidate.Id == 1);

        var channels = await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: ct);
        Assert.NotEmpty(channels.Channels);
    }

    /// <summary>Lends the fixture's session to a connection manager without letting it close it.</summary>
    private sealed class BorrowedTransport(IQueryTransport inner) : IQueryTransport
    {
        public bool SupportsEvents => inner.SupportsEvents;

        public Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default) =>
            inner.SendAsync(command, cancellationToken);

        public IAsyncEnumerable<QueryEvent> GetEventsAsync(CancellationToken cancellationToken = default) =>
            inner.GetEventsAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}