using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Resources;
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
    public async Task Command_help_returns_a_whole_page_and_the_session_stays_in_step()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var tools = new MetaTools(new QueryExecutor(connections, new SafetyPolicy()));

        // servernotifyregister quotes two indented status lines before its real one.
        var help = await tools.CommandHelpAsync("servernotifyregister", cancellationToken: ct);
        Assert.StartsWith("Usage: servernotifyregister", help.Text, StringComparison.Ordinal);
        Assert.EndsWith("servernotifyregister event=channel id=123\n  error id=0 msg=ok", help.Text, StringComparison.Ordinal);

        // Had the page ended at the first quoted status line, its rest would now be read as this answer.
        var who = await tools.WhoAmIAsync(cancellationToken: ct);
        Assert.Equal("serveradmin", who.Fields["client_login_name"]);

        var overview = await tools.CommandHelpAsync(cancellationToken: ct);
        Assert.Contains("channeledit", overview.Text, StringComparison.Ordinal);

        await Assert.ThrowsAsync<McpException>(() => tools.CommandHelpAsync("nosuchcommand", cancellationToken: ct));
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

    [RequiresTeamSpeakServerFact]
    public async Task Identity_group_and_permission_tools_answer_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        // This session's own login is the one identity every test server is sure to have.
        await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: ct);
        var whoami = await new MetaTools(executor).WhoAmIAsync(cancellationToken: ct);
        var databaseId = int.Parse(whoami.Fields["client_database_id"], System.Globalization.CultureInfo.InvariantCulture);

        var identity = await new ClientDatabaseTools(executor).ResolveClientAsync(databaseId: databaseId, virtualServerId: 1, cancellationToken: ct);
        Assert.Equal("serveradmin", identity.UniqueId);
        Assert.NotEmpty(identity.OnlineClientIds);

        var groups = await new ClientGroupsFor(executor).ReadAsync(databaseId, ct);
        Assert.NotEmpty(groups.ServerGroups);

        var serverGroups = await new GroupTools(executor).ListServerGroupsAsync(1, cancellationToken: ct);
        Assert.Contains(serverGroups.Groups, group => group.Type == "regular");

        var permissions = new PermissionTools(executor, new PermissionNameCache());

        var effective = await permissions.EffectivePermissionsAsync(
            databaseId, permission: "b_virtualserver_info_view", channelId: 1, virtualServerId: 1, cancellationToken: ct);
        var infoView = Assert.Single(effective.Permissions);
        Assert.Equal(1, infoView.Value);
        Assert.StartsWith("server group", infoView.DecidedBy, StringComparison.Ordinal);

        var holders = await permissions.FindPermissionAsync("i_client_talk_power", 1, cancellationToken: ct);
        Assert.Contains(holders.Holders, holder => holder.Kind == "server group" && holder.GroupName is not null);

        var assigned = await permissions.AssignedPermissionsAsync(serverGroupId: groups.ServerGroups[0].Id, virtualServerId: 1, cancellationToken: ct);
        Assert.All(assigned.Permissions, permission => Assert.DoesNotContain("#", permission.Name, StringComparison.Ordinal));
    }

    [RequiresTeamSpeakServerFact]
    public async Task Moderation_access_log_and_health_tools_answer_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(Configuration.SafetyLevel.Write));

        var health = await new VirtualServerTools(executor).HealthReportAsync(1, cancellationToken: ct);
        Assert.Equal("online", health.Status);
        Assert.True(health.QueryClientsOnline >= 1);

        var log = await new LogTools(executor).ViewLogAsync(lines: 5, virtualServerId: 1, cancellationToken: ct);
        Assert.NotEmpty(log.Entries);
        Assert.All(log.Entries, entry => Assert.NotEmpty(entry.Level));

        // Empty on a fresh server, which is exactly the case that has to come back as a list, not an error.
        await new ModerationTools(executor).ListBansAsync(virtualServerId: 1, cancellationToken: ct);
        await new ModerationTools(executor).ListComplaintsAsync(virtualServerId: 1, cancellationToken: ct);
        await new ModerationTools(executor).ListPrivilegeKeysAsync(virtualServerId: 1, cancellationToken: ct);
        await new AccessTools(executor).ListApiKeysAsync(cancellationToken: ct);
        await new AccessTools(executor).ListQueryLoginsAsync(virtualServerId: 1, cancellationToken: ct);
        await new AccessTools(executor).ListMessagesAsync(virtualServerId: 1, cancellationToken: ct);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Search_membership_and_custom_property_tools_answer_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var executor = new QueryExecutor(connections, new SafetyPolicy());

        // Search for part of a channel that exists, since the test server's channel names are not fixed.
        var existing = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: ct)).Channels
            .First(channel => !channel.Name.StartsWith('[') && channel.Name.Length >= 4);
        var fragment = existing.Name[^4..];
        var channels = await new ChannelTools(executor).FindChannelsAsync(fragment, 1, cancellationToken: ct);
        Assert.Contains(channels.Channels, channel => channel.Id == existing.Id);

        // The server refuses a search that matches nothing with 768; the tool answers with no channels.
        Assert.Empty((await new ChannelTools(executor).FindChannelsAsync("zzqx-no-such-channel", 1, cancellationToken: ct)).Channels);

        var whoami = await new MetaTools(executor).WhoAmIAsync(cancellationToken: ct);
        var ownClientId = int.Parse(whoami.Fields["client_id"], System.Globalization.CultureInfo.InvariantCulture);
        var ownDatabaseId = int.Parse(whoami.Fields["client_database_id"], System.Globalization.CultureInfo.InvariantCulture);

        var online = await new ClientTools(executor).FindClientsAsync("serveradmin", 1, cancellationToken: ct);
        Assert.Contains(online.Clients, client => client.ClientId == ownClientId);

        // Likewise with 512 for clients.
        Assert.Empty((await new ClientTools(executor).FindClientsAsync("zzqx-no-such-client", 1, cancellationToken: ct)).Clients);

        // Any identity the server knows will do; the test server's list is not fixed.
        var page = await new ClientDatabaseTools(executor).ListKnownClientsAsync(limit: 5, virtualServerId: 1, cancellationToken: ct);
        Assert.True(page.Total >= page.Clients.Count);
        Assert.NotEmpty(page.Clients);
        var known = page.Clients[0];

        var byUid = await new ClientDatabaseTools(executor).FindKnownClientsAsync(known.UniqueId, byUniqueId: true, virtualServerId: 1, cancellationToken: ct);
        Assert.Contains(byUid.Clients, client => client.DatabaseId == known.DatabaseId);

        var info = await new ClientDatabaseTools(executor).KnownClientInfoAsync(known.DatabaseId, 1, cancellationToken: ct);
        Assert.Equal(known.UniqueId, info.Fields["client_unique_identifier"]);

        await new ClientDatabaseTools(executor).CustomInfoAsync(known.DatabaseId, 1, cancellationToken: ct);

        var ownGroups = await new GroupTools(executor).ClientGroupsAsync(ownDatabaseId, 1, cancellationToken: ct);
        var members = await new GroupTools(executor).ServerGroupMembersAsync(ownGroups.ServerGroups[0].Id, virtualServerId: 1, cancellationToken: ct);
        Assert.Contains(members.Members, member => member.DatabaseId == ownDatabaseId);

        await new GroupTools(executor).ChannelGroupMembersAsync(channelId: 1, virtualServerId: 1, cancellationToken: ct);

        var catalog = await new PermissionTools(executor, new PermissionNameCache()).ListPermissionsAsync("talk_power", cancellationToken: ct);
        Assert.Contains(catalog.Permissions, permission => permission.Name == "i_client_talk_power");
    }

    [RequiresTeamSpeakServerFact]
    public async Task Every_resource_reads_as_json_over_ssh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connections = SharedSshConnections();
        var resources = new ServerResources(new QueryExecutor(connections, new SafetyPolicy()), new PermissionNameCache());
        var profile = LiveServerFixture.Profile().Name;

        using var profiles = System.Text.Json.JsonDocument.Parse(resources.Profiles());
        using var channels = System.Text.Json.JsonDocument.Parse(await resources.ChannelsAsync(profile, 1, ct));
        using var clients = System.Text.Json.JsonDocument.Parse(await resources.ClientsAsync(profile, 1, ct));
        using var groups = System.Text.Json.JsonDocument.Parse(await resources.GroupsAsync(profile, 1, ct));
        using var info = System.Text.Json.JsonDocument.Parse(await resources.InfoAsync(profile, 1, ct));
        using var permissions = System.Text.Json.JsonDocument.Parse(await resources.PermissionsAsync(profile, ct));

        Assert.Equal(profile, profiles.RootElement.GetProperty("profiles")[0].GetProperty("name").GetString());
        Assert.True(channels.RootElement.GetProperty("channels").GetArrayLength() > 0);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, clients.RootElement.GetProperty("clients").ValueKind);
        Assert.True(groups.RootElement.GetProperty("serverGroups").GetArrayLength() > 0);
        Assert.Equal("1", info.RootElement.GetProperty("fields").GetProperty("virtualserver_id").GetString());
        Assert.True(permissions.RootElement.GetArrayLength() > 100);
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

    /// <summary>Reads a client's groups on virtual server 1.</summary>
    private sealed class ClientGroupsFor(QueryExecutor executor)
    {
        public Task<ClientGroupMemberships> ReadAsync(int databaseId, CancellationToken cancellationToken) =>
            new GroupTools(executor).ClientGroupsAsync(databaseId, 1, cancellationToken: cancellationToken);
    }

    /// <summary>Lends the fixture's session to a connection manager without letting it close it.</summary>
    internal sealed class BorrowedTransport(IQueryTransport inner) : IQueryTransport
    {
        public bool SupportsEvents => inner.SupportsEvents;

        public bool HoldsSession => inner.HoldsSession;

        public Task<T> RunExclusiveAsync<T>(Func<QuerySender, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.RunExclusiveAsync(work, cancellationToken);

        public Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default) =>
            inner.SendAsync(command, cancellationToken);

        public IAsyncEnumerable<QueryEvent> GetEventsAsync(CancellationToken cancellationToken = default) =>
            inner.GetEventsAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}