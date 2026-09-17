using System.Globalization;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Live tests for what the first round of phase 6 live tests left out: tools never called live,
/// actions only half covered, and the effective permission resolution checked against the value the
/// server itself computes.
/// </summary>
/// <remarks>
/// Every test here removes what it creates and puts any client it moves back where it was. Tests that
/// need a connected person skip themselves when nobody is connected.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class WriteToolCoverageIntegrationTests(LiveServerFixture server)
{
    private const string TalkPower = "i_client_talk_power";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string what) => $"mcp {what} {Guid.NewGuid().ToString("N")[..8]}";

    private static int Id(ActionResult result, string field) =>
        int.Parse(result.Details[field], CultureInfo.InvariantCulture);

    private QueryConnectionManager Connections() =>
        new(
            new ProfileRegistry([LiveServerFixture.Profile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new ToolIntegrationTests.BorrowedTransport(server.Ssh)));

    private static QueryExecutor Executor(QueryConnectionManager connections) =>
        new(connections, new SafetyPolicy(SafetyLevel.Destructive));

    private static async Task<OnlineClient?> SomeoneOnlineAsync(QueryExecutor executor)
    {
        var people = (await new ClientTools(executor).ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients;
        return people.Count > 0 ? people[0] : null;
    }

    private static async Task<int> OwnDatabaseIdAsync(QueryExecutor executor) =>
        int.Parse((await new MetaTools(executor).WhoAmIAsync(cancellationToken: Ct)).Fields["client_database_id"], CultureInfo.InvariantCulture);

    [RequiresTeamSpeakServerFact]
    public async Task Profiles_channel_groups_and_an_instance_wide_message_answer_live()
    {
        await using var connections = Connections();
        var executor = Executor(connections);

        var profile = Assert.Single(new MetaTools(executor).ListProfiles().Profiles);
        Assert.Equal((LiveServerFixture.Profile().Name, "ssh", true), (profile.Name, profile.Interface, profile.EventsAvailable));

        var channelGroups = (await new GroupTools(executor).ListChannelGroupsAsync(1, cancellationToken: Ct)).Groups;
        Assert.Contains(channelGroups, group => group.Type == "regular");

        var sent = await new ClientAdminTools(executor).SendMessageAsync("instance", "tsmcp-live instance-wide message, please ignore", cancellationToken: Ct);
        Assert.Equal("Sent the message to every virtual server.", sent.Done);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_permission_can_be_granted_and_revoked_on_every_kind_of_target()
    {
        await using var connections = Connections();
        var executor = Executor(connections);

        var identity = (await new ClientDatabaseTools(executor).ListKnownClientsAsync(limit: 50, virtualServerId: 1, cancellationToken: Ct)).Clients
            .FirstOrDefault(client => client.UniqueId is not "ServerQuery" and not "serveradmin");
        if (identity is null)
        {
            Assert.Skip("The server knows no client identity to grant a permission to.");
        }

        var channels = new ChannelAdminTools(executor);
        var groups = new GroupAdminTools(executor);
        var permissions = new PermissionAdminTools(executor, new PermissionNameCache());
        var assigned = new PermissionTools(executor, new PermissionNameCache());

        var channel = Id(await channels.CreateAsync(Unique("perm"), properties: new Dictionary<string, string> { ["channel_flag_permanent"] = "1" }, virtualServerId: 1, cancellationToken: Ct), "cid");
        var channelGroup = Id(await groups.ManageChannelGroupAsync("create", Unique("perm"), virtualServerId: 1, cancellationToken: Ct), "cgid");

        try
        {
            // A channel takes channel permissions: the one used is what the live server's default channel
            // itself carries (permission 89). The other targets take a client permission.
            (string Permission, int? ChannelGroupId, int? ChannelId, int? DatabaseId)[] targets =
            [
                (TalkPower, channelGroup, null, null),
                ("i_channel_needed_permission_modify_power", null, channel, null),
                (TalkPower, null, null, identity.DatabaseId),
                (TalkPower, null, channel, identity.DatabaseId),
            ];

            foreach (var (permission, channelGroupId, channelId, databaseId) in targets)
            {
                await permissions.SetPermissionAsync("grant", permission, 23, channelGroupId: channelGroupId, channelId: channelId, databaseId: databaseId, virtualServerId: 1, cancellationToken: Ct);

                var granted = await assigned.AssignedPermissionsAsync(channelGroupId: channelGroupId, channelId: channelId, databaseId: databaseId, virtualServerId: 1, cancellationToken: Ct);
                Assert.Contains(granted.Permissions, value => value.Name == permission && value.Value == 23);

                await permissions.SetPermissionAsync("revoke", permission, channelGroupId: channelGroupId, channelId: channelId, databaseId: databaseId, virtualServerId: 1, cancellationToken: Ct);

                var revoked = await assigned.AssignedPermissionsAsync(channelGroupId: channelGroupId, channelId: channelId, databaseId: databaseId, virtualServerId: 1, cancellationToken: Ct);
                Assert.DoesNotContain(revoked.Permissions, value => value.Name == permission);
            }
        }
        finally
        {
            try
            {
                await groups.DeleteChannelGroupAsync(channelGroup, await LiveNames.ChannelGroupAsync(executor, channelGroup), force: true, virtualServerId: 1, cancellationToken: Ct);
            }
            finally
            {
                await channels.DeleteAsync(channel, await LiveNames.ChannelAsync(executor, channel), force: true, virtualServerId: 1, cancellationToken: Ct);
            }
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_channel_group_privilege_key_and_a_custom_property_search_round_trip()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var moderation = new ModerationAdminTools(executor);
        var groups = new GroupAdminTools(executor);

        var defaultChannel = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels.Single(channel => channel.IsDefault).Id;
        var channelGroup = Id(await groups.ManageChannelGroupAsync("create", Unique("key"), virtualServerId: 1, cancellationToken: Ct), "cgid");

        try
        {
            var token = (await moderation.ManageTokenAsync("add", channelGroupId: channelGroup, channelId: defaultChannel, description: Unique("key"), virtualServerId: 1, cancellationToken: Ct)).Details["token"];
            try
            {
                var key = Assert.Single((await new ModerationTools(executor).ListPrivilegeKeysAsync(virtualServerId: 1, cancellationToken: Ct)).Keys, listed => listed.Token == token);
                Assert.Equal(("channel group", channelGroup, defaultChannel), (key.GrantsA, key.GroupId, key.ChannelId!.Value));
            }
            finally
            {
                await moderation.ManageTokenAsync("delete", token: token, virtualServerId: 1, cancellationToken: Ct);
            }
        }
        finally
        {
            await groups.DeleteChannelGroupAsync(channelGroup, await LiveNames.ChannelGroupAsync(executor, channelGroup), force: true, virtualServerId: 1, cancellationToken: Ct);
        }

        var identity = (await new ClientDatabaseTools(executor).ListKnownClientsAsync(limit: 50, virtualServerId: 1, cancellationToken: Ct)).Clients
            .FirstOrDefault(client => client.UniqueId is not "ServerQuery" and not "serveradmin");
        if (identity is null)
        {
            return;
        }

        var needle = Guid.NewGuid().ToString("N")[..10];
        await moderation.CustomPropertyAsync("set", identity.DatabaseId, "mcp_search", $"value {needle} end", 1, cancellationToken: Ct);
        try
        {
            var found = await new ClientDatabaseTools(executor).CustomSearchAsync("mcp_search", needle, 1, cancellationToken: Ct);
            Assert.Contains(found.Matches, match => match.DatabaseId == identity.DatabaseId && match.Value == $"value {needle} end");
        }
        finally
        {
            await moderation.CustomPropertyAsync("delete", identity.DatabaseId, "mcp_search", virtualServerId: 1, cancellationToken: Ct);
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_complaint_about_a_connected_client_can_be_listed_and_deleted()
    {
        await using var connections = Connections();
        var executor = Executor(connections);

        var person = await SomeoneOnlineAsync(executor);
        if (person is null)
        {
            Assert.Skip("No real client is connected; the server only accepts complaints about someone online.");
            return;
        }

        var own = await OwnDatabaseIdAsync(executor);
        var message = Unique("complaint");

        // There is no dedicated tool for filing a complaint, which people do from their client.
        await new MetaTools(executor).QueryRawAsync(
            "complainadd",
            new Dictionary<string, string> { ["tcldbid"] = person.DatabaseId.ToString(CultureInfo.InvariantCulture), ["message"] = message },
            virtualServerId: 1,
            cancellationToken: Ct);

        try
        {
            var complaint = Assert.Single((await new ModerationTools(executor).ListComplaintsAsync(person.DatabaseId, virtualServerId: 1, cancellationToken: Ct)).Complaints, listed => listed.Message == message);
            Assert.Equal((person.DatabaseId, own), (complaint.TargetDatabaseId, complaint.FromDatabaseId));
            Assert.NotNull(complaint.Timestamp);
        }
        finally
        {
            await new ModerationAdminTools(executor).DeleteComplaintAsync(person.DatabaseId, own, 1, cancellationToken: Ct);
        }

        Assert.DoesNotContain((await new ModerationTools(executor).ListComplaintsAsync(person.DatabaseId, virtualServerId: 1, cancellationToken: Ct)).Complaints, listed => listed.Message == message);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Effective_talk_power_matches_what_the_server_computes_for_a_connected_client()
    {
        await using var connections = Connections();
        var executor = Executor(connections);

        var person = await SomeoneOnlineAsync(executor);
        if (person is null)
        {
            Assert.Skip("No real client is connected, so there is no server-computed talk power to compare against.");
            return;
        }

        var clients = new ClientTools(executor);
        var admin = new ClientAdminTools(executor);
        var channels = new ChannelAdminTools(executor);
        var groups = new GroupAdminTools(executor);
        var permissions = new PermissionAdminTools(executor, new PermissionNameCache());
        var effective = new PermissionTools(executor, new PermissionNameCache());

        var defaultChannelGroup = int.Parse(
            (await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct)).Fields["virtualserver_default_channel_group"],
            CultureInfo.InvariantCulture);

        var channel = Id(await channels.CreateAsync(Unique("power"), properties: new Dictionary<string, string> { ["channel_flag_permanent"] = "1" }, virtualServerId: 1, cancellationToken: Ct), "cid");
        var serverGroup = Id(await groups.ManageServerGroupAsync("create", Unique("power"), virtualServerId: 1, cancellationToken: Ct), "sgid");
        var channelGroup = Id(await groups.ManageChannelGroupAsync("create", Unique("power"), virtualServerId: 1, cancellationToken: Ct), "cgid");

        // The server's own answer: client_talk_power is what it computed for the client where it is now.
        async Task CompareAsync(string scenario)
        {
            string? mine = null;
            string? theirs = null;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var resolved = Assert.Single((await effective.EffectivePermissionsAsync(person.DatabaseId, permission: TalkPower, channelId: channel, virtualServerId: 1, cancellationToken: Ct)).Permissions);
                mine = (resolved.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                theirs = (await clients.ClientInfoAsync(person.ClientId, 1, cancellationToken: Ct)).Fields["client_talk_power"];

                if (mine == theirs)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
            }

            Assert.Fail($"{scenario}: ts_perm_effective says {mine}, the server computes {theirs}.");
        }

        try
        {
            await admin.MoveAsync(person.ClientId, channel, virtualServerId: 1, cancellationToken: Ct);
            await groups.ServerGroupMembershipAsync("add", serverGroup, person.DatabaseId, 1, cancellationToken: Ct);

            await CompareAsync("without the test groups' permissions");

            await permissions.SetPermissionAsync("grant", TalkPower, 31, serverGroupId: serverGroup, virtualServerId: 1, cancellationToken: Ct);
            await CompareAsync("with a server group granting 31");

            await groups.SetChannelGroupAsync(person.DatabaseId, channel, channelGroup, 1, cancellationToken: Ct);
            await permissions.SetPermissionAsync("grant", TalkPower, 62, channelGroupId: channelGroup, virtualServerId: 1, cancellationToken: Ct);
            await CompareAsync("with a channel group granting 62 on top");

            await permissions.SetPermissionAsync("grant", TalkPower, 31, skip: true, serverGroupId: serverGroup, virtualServerId: 1, cancellationToken: Ct);
            await CompareAsync("with the skip flag on the server group");

            await permissions.SetPermissionAsync("grant", TalkPower, 5, negated: true, skip: true, serverGroupId: serverGroup, virtualServerId: 1, cancellationToken: Ct);
            await CompareAsync("with the negate flag on the server group");
        }
        finally
        {
            try
            {
                await admin.MoveAsync(person.ClientId, person.ChannelId, virtualServerId: 1, cancellationToken: Ct);
            }
            finally
            {
                try
                {
                    await groups.SetChannelGroupAsync(person.DatabaseId, channel, defaultChannelGroup, 1, cancellationToken: Ct);
                    await groups.ServerGroupMembershipAsync("remove", serverGroup, person.DatabaseId, 1, cancellationToken: Ct);
                }
                finally
                {
                    await groups.DeleteChannelGroupAsync(channelGroup, await LiveNames.ChannelGroupAsync(executor, channelGroup), force: true, virtualServerId: 1, cancellationToken: Ct);
                    await groups.DeleteServerGroupAsync(serverGroup, await LiveNames.ServerGroupAsync(executor, serverGroup), force: true, virtualServerId: 1, cancellationToken: Ct);
                    await channels.DeleteAsync(channel, await LiveNames.ChannelAsync(executor, channel), force: true, virtualServerId: 1, cancellationToken: Ct);
                }
            }
        }
    }
}