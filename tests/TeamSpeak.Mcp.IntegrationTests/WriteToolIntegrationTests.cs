using System.Globalization;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Runs the changing tools against a live TeamSpeak 6 server, each as a round trip that puts the
/// server back the way it found it.
/// </summary>
/// <remarks>
/// Everything a test creates carries a unique "tsmcp-live" name and is removed in a finally block, so
/// a failure leaves no more behind than one test's objects. Actions that cannot be undone or would
/// disturb the shared test server — kicks, identity deletion, snapshot deployment, permission reset,
/// stopping the only virtual server — are covered by unit tests only.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class WriteToolIntegrationTests(LiveServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Kept short on purpose: the server refuses a group name over 30 characters with 1541.
    private static string Unique(string what) => $"mcp {what} {Guid.NewGuid().ToString("N")[..8]}";

    private static int Id(ActionResult result, string field) =>
        int.Parse(result.Details[field], CultureInfo.InvariantCulture);

    private QueryConnectionManager Connections() =>
        new(
            new ProfileRegistry([LiveServerFixture.Profile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new ToolIntegrationTests.BorrowedTransport(server.Ssh)));

    private static QueryExecutor Executor(QueryConnectionManager connections) =>
        new(connections, new SafetyPolicy(SafetyLevel.Destructive));

    /// <summary>A real client identity to act on, or null on a server that has none.</summary>
    private static async Task<KnownClient?> SomeIdentityAsync(QueryExecutor executor)
    {
        var page = await new ClientDatabaseTools(executor).ListKnownClientsAsync(limit: 50, virtualServerId: 1, cancellationToken: Ct);
        return page.Clients.FirstOrDefault(client => client.UniqueId != "ServerQuery" && client.UniqueId != "serveradmin");
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_channel_can_be_created_changed_moved_and_deleted()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var admin = new ChannelAdminTools(executor);
        var channels = new ChannelTools(executor);

        var created = await admin.CreateAsync(
            Unique("channel"),
            properties: new Dictionary<string, string> { ["channel_flag_permanent"] = "1", ["channel_topic"] = "created by a test" },
            virtualServerId: 1,
            cancellationToken: Ct);
        var channelId = Id(created, "cid");

        try
        {
            await admin.EditAsync(channelId, new Dictionary<string, string> { ["channel_topic"] = "changed by a test" }, 1, cancellationToken: Ct);
            Assert.Equal("changed by a test", (await channels.ChannelInfoAsync(channelId, 1, cancellationToken: Ct)).Fields["channel_topic"]);

            var parent = (await channels.ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels
                .First(channel => channel.Id != channelId && channel.ParentId == 0).Id;

            await admin.MoveAsync(channelId, parent, virtualServerId: 1, cancellationToken: Ct);
            var moved = (await channels.ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels.Single(channel => channel.Id == channelId);
            Assert.Equal(parent, moved.ParentId);
        }
        finally
        {
            await admin.DeleteAsync(channelId, force: true, virtualServerId: 1, cancellationToken: Ct);
        }

        Assert.DoesNotContain((await channels.ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels, channel => channel.Id == channelId);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_server_group_takes_a_name_permissions_members_and_a_copy_and_is_deleted_again()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var groups = new GroupAdminTools(executor);
        var permissions = new PermissionAdminTools(executor, new PermissionNameCache());
        var assigned = new PermissionTools(executor, new PermissionNameCache());

        var groupId = Id(await groups.ManageServerGroupAsync("create", Unique("group"), virtualServerId: 1, cancellationToken: Ct), "sgid");
        int? copyId = null;

        try
        {
            var renamed = Unique("renamed");
            await groups.ManageServerGroupAsync("rename", renamed, groupId, virtualServerId: 1, cancellationToken: Ct);
            Assert.Contains((await new GroupTools(executor).ListServerGroupsAsync(1, cancellationToken: Ct)).Groups, group => group.Id == groupId && group.Name == renamed);

            await permissions.SetPermissionAsync("grant", "i_client_talk_power", 42, skip: true, serverGroupId: groupId, virtualServerId: 1, cancellationToken: Ct);
            var granted = (await assigned.AssignedPermissionsAsync(serverGroupId: groupId, virtualServerId: 1, cancellationToken: Ct)).Permissions
                .Single(permission => permission.Name == "i_client_talk_power");
            Assert.Equal((42, true), (granted.Value, granted.Skip));

            await permissions.SetPermissionAsync("revoke", "i_client_talk_power", serverGroupId: groupId, virtualServerId: 1, cancellationToken: Ct);
            Assert.DoesNotContain((await assigned.AssignedPermissionsAsync(serverGroupId: groupId, virtualServerId: 1, cancellationToken: Ct)).Permissions,
                permission => permission.Name == "i_client_talk_power");

            if (await SomeIdentityAsync(executor) is { } identity)
            {
                await groups.ServerGroupMembershipAsync("add", groupId, identity.DatabaseId, 1, cancellationToken: Ct);
                Assert.Contains((await new GroupTools(executor).ServerGroupMembersAsync(groupId, 1, cancellationToken: Ct)).Members,
                    member => member.DatabaseId == identity.DatabaseId);

                await groups.ServerGroupMembershipAsync("remove", groupId, identity.DatabaseId, 1, cancellationToken: Ct);
            }

            copyId = Id(await groups.ManageServerGroupAsync("copy", Unique("copy"), groupId, virtualServerId: 1, cancellationToken: Ct), "sgid");
            Assert.NotEqual(groupId, copyId);
        }
        finally
        {
            if (copyId is { } copy)
            {
                await groups.DeleteServerGroupAsync(copy, force: true, virtualServerId: 1, cancellationToken: Ct);
            }

            await groups.DeleteServerGroupAsync(groupId, force: true, virtualServerId: 1, cancellationToken: Ct);
        }

        Assert.DoesNotContain((await new GroupTools(executor).ListServerGroupsAsync(1, cancellationToken: Ct)).Groups, group => group.Id == groupId);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_channel_group_can_be_given_to_an_identity_and_taken_back()
    {
        await using var connections = Connections();
        var executor = Executor(connections);

        if (await SomeIdentityAsync(executor) is not { } identity)
        {
            return;
        }

        var groups = new GroupAdminTools(executor);
        var defaultGroup = int.Parse(
            (await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct)).Fields["virtualserver_default_channel_group"],
            CultureInfo.InvariantCulture);

        // Not channel 1: a snapshot deploy gives every channel a new id.
        var channel = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels
            .Single(entry => entry.IsDefault).Id;

        // Restore whatever the identity held in the channel before, not merely the default group.
        var before = (await new GroupTools(executor).ChannelGroupMembersAsync(channelId: channel, databaseId: identity.DatabaseId, virtualServerId: 1, cancellationToken: Ct)).Assignments;
        var originalGroup = before.Count > 0 ? before[0].ChannelGroupId : defaultGroup;

        var groupId = Id(await groups.ManageChannelGroupAsync("create", Unique("channel group"), virtualServerId: 1, cancellationToken: Ct), "cgid");

        try
        {
            await groups.SetChannelGroupAsync(identity.DatabaseId, channel, groupId, 1, cancellationToken: Ct);
            var assignments = await new GroupTools(executor).ChannelGroupMembersAsync(channelId: channel, databaseId: identity.DatabaseId, virtualServerId: 1, cancellationToken: Ct);
            Assert.Contains(assignments.Assignments, assignment => assignment.ChannelGroupId == groupId);
        }
        finally
        {
            await groups.SetChannelGroupAsync(identity.DatabaseId, channel, originalGroup, 1, cancellationToken: Ct);
            await groups.DeleteChannelGroupAsync(groupId, force: true, virtualServerId: 1, cancellationToken: Ct);
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task Bans_privilege_keys_temporary_passwords_custom_properties_and_log_entries_round_trip()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var moderation = new ModerationAdminTools(executor);
        var reading = new ModerationTools(executor);

        var ban = await moderation.AddBanAsync(ip: "203.0.113.77", durationSeconds: 120, reason: "tsmcp-live ban", virtualServerId: 1, cancellationToken: Ct);
        var banId = Id(ban, "banid");
        try
        {
            var listed = (await reading.ListBansAsync(virtualServerId: 1, cancellationToken: Ct)).Bans.Single(entry => entry.Id == banId);
            Assert.Equal(("203.0.113.77", "tsmcp-live ban", 120L), (listed.Ip, listed.Reason, listed.DurationSeconds));
        }
        finally
        {
            await moderation.DeleteBanAsync(banId, 1, cancellationToken: Ct);
        }

        var groups = new GroupAdminTools(executor);
        var groupId = Id(await groups.ManageServerGroupAsync("create", Unique("token group"), virtualServerId: 1, cancellationToken: Ct), "sgid");
        try
        {
            var description = Unique("token");
            var token = (await moderation.ManageTokenAsync("add", serverGroupId: groupId, description: description, virtualServerId: 1, cancellationToken: Ct)).Details["token"];
            try
            {
                Assert.Contains((await reading.ListPrivilegeKeysAsync(1, cancellationToken: Ct)).Keys, key => key.Token == token && key.GroupId == groupId);
            }
            finally
            {
                await moderation.ManageTokenAsync("delete", token: token, virtualServerId: 1, cancellationToken: Ct);
            }
        }
        finally
        {
            await groups.DeleteServerGroupAsync(groupId, force: true, virtualServerId: 1, cancellationToken: Ct);
        }

        var passwords = new VirtualServerAdminTools(executor);
        var password = Unique("pw").Replace(" ", "-", StringComparison.Ordinal);
        await passwords.TempPasswordAsync("add", password, 300, "tsmcp-live", virtualServerId: 1, cancellationToken: Ct);
        try
        {
            Assert.Contains((await passwords.TempPasswordAsync("list", virtualServerId: 1, cancellationToken: Ct)).Records, entry => entry["pw_clear"] == password);
        }
        finally
        {
            await passwords.TempPasswordAsync("delete", password, virtualServerId: 1, cancellationToken: Ct);
        }

        if (await SomeIdentityAsync(executor) is { } identity)
        {
            await moderation.CustomPropertyAsync("set", identity.DatabaseId, "mcp_test", "round trip", 1, cancellationToken: Ct);
            try
            {
                Assert.Equal("round trip", (await new ClientDatabaseTools(executor).CustomInfoAsync(identity.DatabaseId, 1, cancellationToken: Ct)).Properties["mcp_test"]);
            }
            finally
            {
                await moderation.CustomPropertyAsync("delete", identity.DatabaseId, "mcp_test", virtualServerId: 1, cancellationToken: Ct);
            }
        }

        var entry = Unique("log entry");
        await moderation.AddLogAsync(entry, virtualServerId: 1, cancellationToken: Ct);
        Assert.Contains((await new LogTools(executor).ViewLogAsync(lines: 20, virtualServerId: 1, cancellationToken: Ct)).Entries,
            line => line.Message.Contains(entry, StringComparison.Ordinal));
    }

    [RequiresTeamSpeakServerFact]
    public async Task Api_keys_and_query_logins_can_be_created_and_removed()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var access = new ModerationAdminTools(executor);

        var key = await access.ManageApiKeyAsync("add", "read", lifetimeDays: 1, cancellationToken: Ct);
        var keyId = Id(key, "id");
        try
        {
            Assert.False(string.IsNullOrEmpty(key.Details["apikey"]));
            Assert.Contains((await new AccessTools(executor).ListApiKeysAsync(cancellationToken: Ct)).Keys, listed => listed.Id == keyId && listed.Scope == "read");
        }
        finally
        {
            await access.ManageApiKeyAsync("delete", keyId: keyId, cancellationToken: Ct);
        }

        Assert.DoesNotContain((await new AccessTools(executor).ListApiKeysAsync(cancellationToken: Ct)).Keys, listed => listed.Id == keyId);

        // An identity that already has a query login is left alone: adding would replace its login and
        // the cleanup would then delete the original.
        var existingLogins = (await new AccessTools(executor).ListQueryLoginsAsync(1, cancellationToken: Ct)).Logins;
        if (await SomeIdentityAsync(executor) is { } identity && existingLogins.All(existing => existing.DatabaseId != identity.DatabaseId))
        {
            // Query login names are refused at 23 characters already; nine is well inside the limit.
            var login = "mcp" + Guid.NewGuid().ToString("N")[..6];
            var created = await access.ManageQueryLoginAsync("add", identity.DatabaseId, login, 1, cancellationToken: Ct);
            try
            {
                Assert.False(string.IsNullOrEmpty(created.Details["client_login_password"]));
                Assert.Contains((await new AccessTools(executor).ListQueryLoginsAsync(1, cancellationToken: Ct)).Logins, listed => listed.LoginName == login);
            }
            finally
            {
                await access.ManageQueryLoginAsync("delete", identity.DatabaseId, virtualServerId: 1, cancellationToken: Ct);
            }
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task Messages_reach_the_server_and_a_channel_and_the_session_returns_to_its_channel()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var messages = new ClientAdminTools(executor);

        await messages.SendMessageAsync("server", "tsmcp-live server message", virtualServerId: 1, cancellationToken: Ct);

        var before = await new MetaTools(executor).WhoAmIAsync(cancellationToken: Ct);
        var target = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels
            .First(channel => channel.Id.ToString(CultureInfo.InvariantCulture) != before.Fields["client_channel_id"] && !channel.HasPassword).Id;

        // Other calls on the same session run alongside; the exclusive sequence has to keep them out.
        var result = await messages.SendMessageAsync("channel", "tsmcp-live channel message", channelId: target, virtualServerId: 1, cancellationToken: Ct);
        await Task.WhenAll(
            new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct),
            new MetaTools(executor).InstanceInfoAsync(cancellationToken: Ct),
            new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct),
            messages.SendMessageAsync("channel", "tsmcp-live concurrent channel message", channelId: target, virtualServerId: 1, cancellationToken: Ct));

        Assert.Equal($"Sent the message to channel {target}.", result.Done);

        var after = await new MetaTools(executor).WhoAmIAsync(cancellationToken: Ct);
        Assert.Equal(before.Fields["client_channel_id"], after.Fields["client_channel_id"]);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_connected_client_can_be_found_moved_kicked_from_its_channel_poked_messaged_and_edited()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var admin = new ClientAdminTools(executor);
        var clients = new ClientTools(executor);

        // Needs a real person connected; without one this is reported as skipped rather than passing quietly.
        var people = (await clients.ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients;
        if (people.Count == 0)
        {
            Assert.Skip("No real client is connected to virtual server 1, so the client-targeted actions cannot run.");
        }

        var person = people[0];

        // The read tools, now with a live session behind the identity.
        var fragment = person.Nickname.Length > 3 ? person.Nickname[..3] : person.Nickname;
        Assert.Contains((await clients.FindClientsAsync(fragment, 1, cancellationToken: Ct)).Clients, match => match.ClientId == person.ClientId);

        var identity = await new ClientDatabaseTools(executor).ResolveClientAsync(clientId: person.ClientId, virtualServerId: 1, cancellationToken: Ct);
        Assert.Equal(person.DatabaseId, identity.DatabaseId);
        Assert.Contains(person.ClientId, identity.OnlineClientIds);

        var effective = await new PermissionTools(executor, new PermissionNameCache())
            .EffectivePermissionsAsync(person.DatabaseId, permission: "i_client_talk_power", virtualServerId: 1, cancellationToken: Ct);
        Assert.Equal((person.ChannelId, "the channel the client is in"), (effective.ChannelId, effective.ChannelChoice));

        var channels = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels;
        var defaultChannel = channels.Single(channel => channel.IsDefault).Id;
        var target = channels.First(channel => channel.Id != person.ChannelId && !channel.IsDefault && !channel.HasPassword && channel.MaxClients != 0).Id;

        async Task<int> ChannelOfPersonAsync() =>
            (await clients.ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients.Single(client => client.ClientId == person.ClientId).ChannelId;

        try
        {
            await admin.MoveAsync(person.ClientId, target, virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(target, await ChannelOfPersonAsync());

            // A channel kick puts the client into the default channel; it does not disconnect them.
            await admin.KickAsync(person.ClientId, "channel", "tsmcp-live channel kick", 1, cancellationToken: Ct);
            Assert.Equal(defaultChannel, await ChannelOfPersonAsync());

            await admin.PokeAsync(person.ClientId, "tsmcp-live poke, please ignore", 1, cancellationToken: Ct);
            await admin.SendMessageAsync("client", "tsmcp-live private message, please ignore", clientId: person.ClientId, virtualServerId: 1, cancellationToken: Ct);

            // The description is edited on this server's own session rather than on the person: the
            // server refuses an empty value, so a description set on them could never be cleared.
            var ownClientId = int.Parse((await new MetaTools(executor).WhoAmIAsync(cancellationToken: Ct)).Fields["client_id"], CultureInfo.InvariantCulture);

            // A channel kick rather than a server kick, so a broken guard would move the session, not end it.
            var refused = await Assert.ThrowsAsync<McpException>(() => admin.KickAsync(ownClientId, "channel", virtualServerId: 1, cancellationToken: Ct));
            Assert.Contains("own query session", refused.Message, StringComparison.Ordinal);

            await admin.EditAsync(clientId: ownClientId, description: "tsmcp-live description", virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal("tsmcp-live description", (await clients.ClientInfoAsync(ownClientId, 1, cancellationToken: Ct)).Fields["client_description"]);
        }
        finally
        {
            if (await ChannelOfPersonAsync() != person.ChannelId)
            {
                await admin.MoveAsync(person.ClientId, person.ChannelId, virtualServerId: 1, cancellationToken: Ct);
            }
        }

        Assert.Equal(person.ChannelId, await ChannelOfPersonAsync());
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_client_without_enough_talk_power_can_be_made_a_talker_in_a_moderated_channel()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var admin = new ClientAdminTools(executor);
        var clients = new ClientTools(executor);
        const int NeededTalkPower = 100;

        // Talker status only exists for someone who could not otherwise speak in the channel.
        OnlineClient? person = null;
        foreach (var candidate in (await clients.ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients)
        {
            var power = (await clients.ClientInfoAsync(candidate.ClientId, 1, cancellationToken: Ct)).Fields.GetValueOrDefault("client_talk_power", "0");
            if (int.TryParse(power, CultureInfo.InvariantCulture, out var value) && value < NeededTalkPower)
            {
                person = candidate;
                break;
            }
        }

        if (person is null)
        {
            Assert.Skip($"No connected client has less than {NeededTalkPower} talk power, so nobody can be made a talker.");
        }

        var channelAdmin = new ChannelAdminTools(executor);
        var moderated = Id(
            await channelAdmin.CreateAsync(
                Unique("moderated"),
                properties: new Dictionary<string, string>
                {
                    ["channel_flag_permanent"] = "1",
                    ["channel_needed_talk_power"] = NeededTalkPower.ToString(CultureInfo.InvariantCulture),
                },
                virtualServerId: 1,
                cancellationToken: Ct),
            "cid");

        try
        {
            await admin.MoveAsync(person.ClientId, moderated, virtualServerId: 1, cancellationToken: Ct);

            await admin.EditAsync(clientId: person.ClientId, isTalker: true, virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal("1", (await clients.ClientInfoAsync(person.ClientId, 1, cancellationToken: Ct)).Fields["client_is_talker"]);

            await admin.EditAsync(clientId: person.ClientId, isTalker: false, virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal("0", (await clients.ClientInfoAsync(person.ClientId, 1, cancellationToken: Ct)).Fields["client_is_talker"]);
        }
        finally
        {
            try
            {
                await admin.MoveAsync(person.ClientId, person.ChannelId, virtualServerId: 1, cancellationToken: Ct);
            }
            finally
            {
                await channelAdmin.DeleteAsync(moderated, force: true, virtualServerId: 1, cancellationToken: Ct);
            }
        }
    }

    [RequiresDisruptiveLiveTestFact]
    public async Task A_connected_client_receives_an_offline_message_and_can_be_kicked_off_the_server()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var admin = new ClientAdminTools(executor);
        var clients = new ClientTools(executor);

        var people = (await clients.ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients;
        if (people.Count == 0)
        {
            Assert.Skip("No real client is connected to virtual server 1, so there is nobody to kick.");
        }

        var person = people[0];

        // An offline message cannot be taken back from someone else's inbox, which is why it lives here
        // rather than in the round-trip tests.
        var message = await admin.OfflineMessageAsync(
            "send", person.UniqueId, "tsmcp-live", "Offline message from the teamspeak6-mcp live tests; please ignore.",
            virtualServerId: 1, cancellationToken: Ct);
        Assert.Equal("Left the offline message.", message.Done);

        // Disconnects the person; they have to reconnect themselves.
        await admin.KickAsync(person.ClientId, "server", "tsmcp-live server kick", 1, cancellationToken: Ct);

        var gone = false;
        for (var attempt = 0; attempt < 10 && !gone; attempt++)
        {
            gone = (await clients.ListClientsAsync(virtualServerId: 1, cancellationToken: Ct)).Clients.All(client => client.ClientId != person.ClientId);
            if (!gone)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            }
        }

        Assert.True(gone, $"Client {person.ClientId} was still connected after the server kick.");
    }

    [RequiresTeamSpeakServerFact]
    public async Task Server_and_instance_settings_can_be_changed_and_restored_and_a_snapshot_taken()
    {
        await using var connections = Connections();
        var executor = Executor(connections);
        var admin = new VirtualServerAdminTools(executor);

        var original = (await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct)).Fields["virtualserver_welcomemessage"];
        var changed = Unique("welcome");

        await admin.EditAsync(new Dictionary<string, string> { ["virtualserver_welcomemessage"] = changed }, 1, cancellationToken: Ct);
        try
        {
            Assert.Equal(changed, (await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct)).Fields["virtualserver_welcomemessage"]);
        }
        finally
        {
            // An empty welcome message cannot be sent back, so a server that had none gets a placeholder.
            await admin.EditAsync(
                new Dictionary<string, string> { ["virtualserver_welcomemessage"] = original.Length > 0 ? original : "Welcome" },
                1,
                cancellationToken: Ct);
        }

        // Writing a setting back unchanged proves the edit path without risking the query flood limits.
        var instance = await new MetaTools(executor).InstanceInfoAsync(cancellationToken: Ct);
        var floodCommands = instance.Instance["serverinstance_serverquery_flood_commands"];
        await admin.InstanceEditAsync(new Dictionary<string, string> { ["serverinstance_serverquery_flood_commands"] = floodCommands }, cancellationToken: Ct);

        var snapshot = await admin.SnapshotCreateAsync(virtualServerId: 1, cancellationToken: Ct);
        Assert.False(string.IsNullOrEmpty(snapshot.Details["data"]));

        // The license may cap the number of virtual servers; a refusal saying so is also a correct answer.
        ActionResult? created = null;
        try
        {
            created = await admin.CreateAsync(Unique("server"), cancellationToken: Ct);
        }
        catch (McpException ex) when (ex.Message.Contains("2816", StringComparison.Ordinal))
        {
            Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        if (created is not null)
        {
            var sid = Id(created, "sid");
            var name = (await new VirtualServerTools(executor).ListVirtualServersAsync(cancellationToken: Ct)).VirtualServers.Single(entry => entry.Id == sid).Name;

            try
            {
                await admin.PowerAsync("stop", sid, cancellationToken: Ct);
            }
            finally
            {
                // Removed even if stopping failed; a server that never started refuses a stop.
                await admin.DeleteAsync(sid, name, cancellationToken: Ct);
            }
        }
    }
}