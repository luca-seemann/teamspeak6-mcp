using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The phase 6 tools that change things: safety, safeguards, and the commands they send.</summary>
public class WriteToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value);

    [Fact]
    public async Task A_read_only_profile_refuses_every_change_before_sending_anything()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly);
        var names = new PermissionNameCache();

        Func<Task>[] changes =
        [
            () => new VirtualServerAdminTools(harness.Executor).CreateAsync("x", cancellationToken: Ct),
            () => new VirtualServerAdminTools(harness.Executor).EditAsync(new Dictionary<string, string> { ["virtualserver_name"] = "x" }, cancellationToken: Ct),
            () => new VirtualServerAdminTools(harness.Executor).PowerAsync("start", 1, cancellationToken: Ct),
            () => new VirtualServerAdminTools(harness.Executor).DeleteAsync(1, "x", cancellationToken: Ct),
            () => new VirtualServerAdminTools(harness.Executor).SnapshotDeployAsync("3", "data", "x", cancellationToken: Ct),
            () => new VirtualServerAdminTools(harness.Executor).TempPasswordAsync("list", cancellationToken: Ct),
            () => new ChannelAdminTools(harness.Executor).CreateAsync("x", cancellationToken: Ct),
            () => new ChannelAdminTools(harness.Executor).MoveAsync(1, 0, cancellationToken: Ct),
            () => new ChannelAdminTools(harness.Executor).DeleteAsync(1, cancellationToken: Ct),
            () => new ClientAdminTools(harness.Executor).MoveAsync(1, 2, cancellationToken: Ct),
            () => new ClientAdminTools(harness.Executor).KickAsync(1, "server", cancellationToken: Ct),
            () => new ClientAdminTools(harness.Executor).SendMessageAsync("client", "hi", clientId: 2, cancellationToken: Ct),
            () => new ClientAdminTools(harness.Executor).SendMessageAsync("channel", "hi", channelId: 2, cancellationToken: Ct),
            () => new GroupAdminTools(harness.Executor).ManageServerGroupAsync("create", "x", cancellationToken: Ct),
            () => new PermissionAdminTools(harness.Executor, names).SetPermissionAsync("grant", "i_client_talk_power", 1, serverGroupId: 6, cancellationToken: Ct),
            () => new PermissionAdminTools(harness.Executor, names).ResetAsync("x", cancellationToken: Ct),
            () => new ModerationAdminTools(harness.Executor).AddBanAsync(ip: "203.0.113.7", cancellationToken: Ct),
            () => new ModerationAdminTools(harness.Executor).AddBanAsync(clientId: 5, cancellationToken: Ct),
            () => new ModerationAdminTools(harness.Executor).ManageTokenAsync("delete", token: "key", cancellationToken: Ct),
            () => new ModerationAdminTools(harness.Executor).ManageApiKeyAsync("delete", keyId: 1, cancellationToken: Ct),
            () => new ModerationAdminTools(harness.Executor).ManageQueryLoginAsync("delete", 3, cancellationToken: Ct),
        ];

        foreach (var change in changes)
        {
            await Assert.ThrowsAsync<McpException>(change);
        }

        // Not even a read: the permission names and the confirmation lookups come after the check.
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_write_profile_allows_routine_changes_but_not_destructive_ones()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport
            .Returns("serverstart", ToolHarness.Records())
            .Returns("privilegekeydelete", ToolHarness.Records());

        await new VirtualServerAdminTools(harness.Executor).PowerAsync("start", 2, cancellationToken: Ct);
        await new ModerationAdminTools(harness.Executor).ManageTokenAsync("delete", token: "key", cancellationToken: Ct);

        await Assert.ThrowsAsync<McpException>(() => new VirtualServerAdminTools(harness.Executor).PowerAsync("stop", 2, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new VirtualServerAdminTools(harness.Executor).CreateAsync("x", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new ModerationAdminTools(harness.Executor).ManageTokenAsync("add", serverGroupId: 6, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(harness.Executor).KickAsync(5, "server", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new ChannelAdminTools(harness.Executor).DeleteAsync(3, cancellationToken: Ct));

        Assert.Equal(["serverstart", "privilegekeydelete"], harness.Transport.SentCommands.Select(command => command.Name));
    }

    [Fact]
    public async Task Deleting_a_virtual_server_requires_its_exact_name()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("serverlist", ToolHarness.Records(Fields(("virtualserver_id", "2"), ("virtualserver_name", "Old Server"))))
            .Returns("serverdelete", ToolHarness.Records());
        var tools = new VirtualServerAdminTools(harness.Executor);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.DeleteAsync(2, "old server", cancellationToken: Ct));
        Assert.Contains("'Old Server'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "serverdelete");

        await tools.DeleteAsync(2, " Old Server ", cancellationToken: Ct);
        Assert.Equal("2", harness.Transport.SentCommands.Single(command => command.Name == "serverdelete").Parameters!["sid"]);
    }

    [Fact]
    public async Task A_server_whose_name_cannot_be_read_cannot_be_confirmed_even_with_an_empty_name()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("serverlist", ToolHarness.Records(Fields(("virtualserver_id", "2"), ("virtualserver_name", ""))));

        await Assert.ThrowsAsync<McpException>(() => new VirtualServerAdminTools(harness.Executor).DeleteAsync(2, "", cancellationToken: Ct));

        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "serverdelete");
    }

    [Fact]
    public async Task Stopping_a_virtual_server_passes_the_reason()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("serverstop", ToolHarness.Records());

        await new VirtualServerAdminTools(harness.Executor).PowerAsync("STOP", 3, "maintenance", cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("3", "maintenance"), (sent["sid"], sent["reasonmsg"]));
    }

    [Fact]
    public async Task Deploying_a_snapshot_confirms_the_target_and_asks_for_the_channel_mapping()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("serverinfo", ToolHarness.Records(Fields(("virtualserver_name", "Main"))))
            .Returns("serversnapshotdeploy", ToolHarness.Records(Fields(("ocid", "36"), ("ncid", "40")), Fields(("ocid", "37"), ("ncid", "41"))));
        var tools = new VirtualServerAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.SnapshotDeployAsync("3", "KLUv", "Other", cancellationToken: Ct));

        var result = await tools.SnapshotDeployAsync("3", "KLUv", "Main", salt: "s", cancellationToken: Ct);

        var deploy = harness.Transport.SentCommands.Single(command => command.Name == "serversnapshotdeploy");
        Assert.Equal(["-mapping"], deploy.Options);
        Assert.Equal(TimeSpan.FromMinutes(10), deploy.Timeout);
        Assert.Equal(("3", "KLUv", "s"), (deploy.Parameters!["version"], deploy.Parameters["data"], deploy.Parameters["salt"]));
        Assert.Equal(2, result.Records.Count);
    }

    [Theory]
    [InlineData("-keepfiles")]
    [InlineData("keepfiles")]
    [InlineData(" -KEEPFILES ")]
    public async Task A_deploy_with_keepfiles_is_refused_on_every_path_without_sending_anything(string option)
    {
        // On TeamSpeak 6.0.0-beta12.1 it hung once and crashed the server once, both times leaving the
        // virtual server unrecoverable.
        await using var harness = new ToolHarness(SafetyLevel.Destructive);

        var raw = await Assert.ThrowsAsync<McpException>(() => new MetaTools(harness.Executor).QueryRawAsync(
            "serversnapshotdeploy", new Dictionary<string, string> { ["version"] = "3", ["data"] = "KLUv" }, [option], virtualServerId: 1, cancellationToken: Ct));

        Assert.Contains("crashed", raw.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Property_maps_may_only_carry_non_empty_properties_of_the_object_being_changed()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);

        await Assert.ThrowsAsync<McpException>(() => new ChannelAdminTools(harness.Executor).EditAsync(
            1, new Dictionary<string, string> { ["cid"] = "7" }, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new ChannelAdminTools(harness.Executor).EditAsync(
            1, new Dictionary<string, string> { ["channel_topic"] = "" }, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new VirtualServerAdminTools(harness.Executor).InstanceEditAsync(
            new Dictionary<string, string> { ["virtualserver_name"] = "x" }, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => new VirtualServerAdminTools(harness.Executor).EditAsync(
            new Dictionary<string, string>(), cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Creates_a_channel_under_a_parent_with_its_properties()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("channelcreate", ToolHarness.Records(Fields(("cid", "16"))));

        var result = await new ChannelAdminTools(harness.Executor).CreateAsync(
            "Raid", 3, new Dictionary<string, string> { ["channel_topic"] = "Tonight", ["channel_flag_permanent"] = "1" }, cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("Raid", "3", "Tonight"), (sent["channel_name"], sent["cpid"], sent["channel_topic"]));
        Assert.Equal("16", result.Details["cid"]);
    }

    [Fact]
    public async Task Moves_a_channel_first_under_its_parent_by_default()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport
            .Returns("channelinfo", ToolHarness.Records(Fields(("pid", "0"))))
            .Returns("channelmove", ToolHarness.Records());

        await new ChannelAdminTools(harness.Executor).MoveAsync(5, 2, cancellationToken: Ct);

        var move = harness.Transport.SentCommands[^1];
        Assert.Equal(("channelmove", "2", "0"), (move.Name, move.Parameters!["cpid"], move.Parameters["order"]));
    }

    [Fact]
    public async Task Reorders_a_channel_within_its_parent_by_setting_its_order()
    {
        // Measured live: channelmove to the parent a channel already has is 770, whatever the order.
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport
            .Returns("channelinfo", ToolHarness.Records(Fields(("pid", "2"))))
            .Returns("channeledit", ToolHarness.Records())
            .Returns("channelmove", ToolHarness.Error(QueryErrorCode.AlreadyMemberOfChannel, "already member of channel"));

        var result = await new ChannelAdminTools(harness.Executor).MoveAsync(5, 2, belowChannelId: 7, cancellationToken: Ct);

        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "channelmove");
        var edit = harness.Transport.SentCommands[^1];
        Assert.Equal(("channeledit", "5", "7"), (edit.Name, edit.Parameters!["cid"], edit.Parameters["channel_order"]));
        Assert.Contains("below channel 7", result.Done, StringComparison.Ordinal);
    }

    private static ToolHarness KickHarness(int ownClientId, bool holdsSession = true)
    {
        var harness = new ToolHarness(SafetyLevel.Destructive, holdsSession: holdsSession);
        harness.Transport
            .Returns("clientinfo", ToolHarness.Records(Fields(("client_nickname", "someone"))))
            .Returns("whoami", ToolHarness.Records(Fields(("client_id", ownClientId.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
            .Returns("clientkick", ToolHarness.Records())
            .Returns("banclient", ToolHarness.Records(Fields(("banid", "2"))));
        return harness;
    }

    [Fact]
    public async Task Kicks_from_the_server_and_refuses_a_reason_the_server_would_cut()
    {
        await using var harness = KickHarness(ownClientId: 7);
        var tools = new ClientAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.KickAsync(5, "server", new string('x', 41), cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.KickAsync(5, "planet", cancellationToken: Ct));
        await tools.KickAsync(5, "server", "Spam", cancellationToken: Ct);

        var kick = harness.Transport.SentCommands.Single(command => command.Name == "clientkick").Parameters!;
        Assert.Equal(("5", "5", "Spam"), (kick["clid"], kick["reasonid"], kick["reasonmsg"]));
        Assert.Equal(1, harness.Transport.ExclusiveSequences);
    }

    [Fact]
    public async Task Refuses_to_kick_or_ban_this_servers_own_query_session()
    {
        await using var harness = KickHarness(ownClientId: 5);

        var kick = await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(harness.Executor).KickAsync(5, "server", cancellationToken: Ct));
        var ban = await Assert.ThrowsAsync<McpException>(() => new ModerationAdminTools(harness.Executor).AddBanAsync(clientId: 5, cancellationToken: Ct));

        Assert.Contains("own query session", kick.Message, StringComparison.Ordinal);
        Assert.Contains("own query session", ban.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name is "clientkick" or "banclient");
    }

    [Fact]
    public async Task Without_a_lasting_client_there_is_no_own_session_to_protect()
    {
        await using var harness = KickHarness(ownClientId: 5, holdsSession: false);

        await new ClientAdminTools(harness.Executor).KickAsync(5, "channel", cancellationToken: Ct);

        Assert.Equal(["clientinfo", "clientkick"], harness.Transport.SentCommands.Select(command => command.Name));
    }

    [Fact]
    public async Task Edits_an_offline_identity_through_the_database_and_insists_on_one_id_and_a_description()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("clientdbedit", ToolHarness.Records());
        var tools = new ClientAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.EditAsync(clientId: 1, databaseId: 3, description: "x", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.EditAsync(databaseId: 3, description: " ", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.EditAsync(databaseId: 3, isTalker: true, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.EditAsync(databaseId: 3, cancellationToken: Ct));
        await tools.EditAsync(databaseId: 3, description: "Guild lead", cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands);
        Assert.Equal(("clientdbedit", "3", "Guild lead"), (sent.Name, sent.Parameters!["cldbid"], sent.Parameters["client_description"]));
    }

    [Fact]
    public async Task Grants_and_withdraws_talker_status_on_a_connected_client_alone()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("clientedit", ToolHarness.Records());
        var tools = new ClientAdminTools(harness.Executor);

        await tools.EditAsync(clientId: 61, isTalker: true, cancellationToken: Ct);
        await tools.EditAsync(clientId: 61, isTalker: false, cancellationToken: Ct);

        var sent = harness.Transport.SentCommands;
        Assert.Equal(("61", "1"), (sent[0].Parameters!["clid"], sent[0].Parameters!["client_is_talker"]));
        Assert.Equal("0", sent[1].Parameters!["client_is_talker"]);
        Assert.False(sent[0].Parameters!.ContainsKey("client_description"));
    }

    private static ToolHarness ChannelMessageHarness(string currentChannel = "1", bool holdsSession = true)
    {
        var harness = new ToolHarness(SafetyLevel.Write, holdsSession: holdsSession);
        harness.Transport
            .Returns("channelinfo", ToolHarness.Records(Fields(("channel_name", "Games"))))
            .Returns("whoami", ToolHarness.Records(Fields(("virtualserver_id", "1"), ("client_id", "7"), ("client_channel_id", currentChannel))))
            .Returns("clientmove", ToolHarness.Records())
            .Returns("sendtextmessage", ToolHarness.Records());
        return harness;
    }

    [Fact]
    public async Task A_channel_message_moves_the_own_session_there_sends_and_moves_it_back_in_one_sequence()
    {
        await using var harness = ChannelMessageHarness();

        var result = await new ClientAdminTools(harness.Executor).SendMessageAsync(
            "channel", "Raid starts now", channelId: 3, channelPassword: "secret", cancellationToken: Ct);

        var sent = harness.Transport.SentCommands;
        Assert.Equal(["channelinfo", "whoami", "clientmove", "sendtextmessage", "clientmove"], sent.Select(command => command.Name));
        Assert.Equal(("7", "3", "secret"), (sent[2].Parameters!["clid"], sent[2].Parameters!["cid"], sent[2].Parameters!["cpw"]));
        Assert.Equal(("2", "3", "Raid starts now"), (sent[3].Parameters!["targetmode"], sent[3].Parameters!["target"], sent[3].Parameters!["msg"]));
        Assert.Equal("1", sent[4].Parameters!["cid"]);
        Assert.Equal(1, harness.Transport.ExclusiveSequences);
        Assert.Equal("Sent the message to channel 3.", result.Done);
    }

    [Fact]
    public async Task A_channel_message_refuses_when_the_own_client_is_on_another_virtual_server()
    {
        await using var harness = ChannelMessageHarness();

        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(harness.Executor)
            .SendMessageAsync("channel", "hi", channelId: 3, virtualServerId: 2, cancellationToken: Ct));

        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name is "clientmove" or "sendtextmessage");
    }

    [Fact]
    public async Task A_channel_message_does_not_move_a_session_already_in_that_channel()
    {
        await using var harness = ChannelMessageHarness(currentChannel: "3");

        await new ClientAdminTools(harness.Executor).SendMessageAsync("channel", "hi", channelId: 3, cancellationToken: Ct);

        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "clientmove");
    }

    [Fact]
    public async Task A_channel_message_moves_back_even_when_sending_is_refused()
    {
        await using var harness = ChannelMessageHarness();
        harness.Transport.Returns("sendtextmessage", ToolHarness.Error(2568, "insufficient client permissions"));

        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(harness.Executor).SendMessageAsync("channel", "hi", channelId: 3, cancellationToken: Ct));

        Assert.Equal("clientmove", harness.Transport.SentCommands[^1].Name);
        Assert.Equal("1", harness.Transport.SentCommands[^1].Parameters!["cid"]);
    }

    [Fact]
    public async Task A_channel_message_says_so_when_the_session_could_not_move_back()
    {
        await using var harness = ChannelMessageHarness();
        harness.Transport.Returns("clientmove", command => command.Parameters!["cid"] == "3"
            ? ToolHarness.Records()
            : ToolHarness.Error(770, "already member of channel"));

        var result = await new ClientAdminTools(harness.Executor).SendMessageAsync("channel", "hi", channelId: 3, cancellationToken: Ct);

        Assert.Contains("could not move back", result.Done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_channel_message_is_refused_by_a_transport_without_a_lasting_client()
    {
        await using var harness = ChannelMessageHarness(holdsSession: false);

        var ex = await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(harness.Executor).SendMessageAsync("channel", "hi", channelId: 3, cancellationToken: Ct));

        Assert.Contains("Nothing was sent", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_server_message_goes_to_the_default_virtual_server_and_an_instance_message_uses_gm()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("sendtextmessage", ToolHarness.Records()).Returns("gm", ToolHarness.Records());
        var tools = new ClientAdminTools(harness.Executor);

        await tools.SendMessageAsync("server", "Restart in 5 minutes", cancellationToken: Ct);
        await tools.SendMessageAsync("instance", "Maintenance tonight", cancellationToken: Ct);

        Assert.Equal(("3", "1"), (harness.Transport.SentCommands[0].Parameters!["targetmode"], harness.Transport.SentCommands[0].Parameters!["target"]));
        Assert.Equal("gm", harness.Transport.SentCommands[1].Name);
    }

    private static ToolHarness PermissionHarness(SafetyLevel level = SafetyLevel.Write)
    {
        var harness = new ToolHarness(level);
        harness.Transport.Returns("permissionlist", ToolHarness.Records(Fields(("permid", "226"), ("permname", "i_client_talk_power"), ("permdesc", ""))));
        return harness;
    }

    [Fact]
    public async Task Grants_on_a_server_group_with_both_flags_and_revokes_without_a_value()
    {
        await using var harness = PermissionHarness();
        harness.Transport.Returns("servergroupaddperm", ToolHarness.Records()).Returns("servergroupdelperm", ToolHarness.Records());
        var tools = new PermissionAdminTools(harness.Executor, new PermissionNameCache());

        await tools.SetPermissionAsync("grant", "i_client_talk_power", 50, skip: true, serverGroupId: 6, cancellationToken: Ct);
        await tools.SetPermissionAsync("revoke", "i_client_talk_power", serverGroupId: 6, cancellationToken: Ct);

        var grant = harness.Transport.SentCommands.Single(command => command.Name == "servergroupaddperm").Parameters!;
        Assert.Equal(("6", "226", "50", "0", "1"), (grant["sgid"], grant["permid"], grant["permvalue"], grant["permnegated"], grant["permskip"]));

        var revoke = harness.Transport.SentCommands.Single(command => command.Name == "servergroupdelperm").Parameters!;
        Assert.False(revoke.ContainsKey("permvalue"));
    }

    [Fact]
    public async Task Grants_in_a_channel_for_one_client_without_flags_it_cannot_take()
    {
        await using var harness = PermissionHarness();
        harness.Transport.Returns("channelclientaddperm", ToolHarness.Records());
        var tools = new PermissionAdminTools(harness.Executor, new PermissionNameCache());

        await Assert.ThrowsAsync<McpException>(() => tools.SetPermissionAsync("grant", "i_client_talk_power", 20, skip: true, channelId: 1, databaseId: 3, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.SetPermissionAsync("grant", "i_client_talk_power", 20, negated: true, databaseId: 3, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.SetPermissionAsync("grant", "i_client_talk_power", channelId: 1, databaseId: 3, cancellationToken: Ct));

        await tools.SetPermissionAsync("grant", "i_client_talk_power", 20, channelId: 1, databaseId: 3, cancellationToken: Ct);

        var sent = harness.Transport.SentCommands.Single(command => command.Name == "channelclientaddperm").Parameters!;
        Assert.Equal(["cid", "cldbid", "permid", "permvalue"], sent.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Resetting_permissions_requires_the_virtual_server_name()
    {
        await using var harness = PermissionHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("serverinfo", ToolHarness.Records(Fields(("virtualserver_name", "Main"))));

        await Assert.ThrowsAsync<McpException>(() => new PermissionAdminTools(harness.Executor, new PermissionNameCache())
            .ResetAsync("main", cancellationToken: Ct));

        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "permreset");
    }

    [Fact]
    public async Task Bans_a_client_or_a_rule_but_not_both_at_once()
    {
        await using var harness = KickHarness(ownClientId: 7);
        harness.Transport.Returns("banadd", ToolHarness.Records(Fields(("banid", "4"))));
        harness.Transport.Returns("banclient", ToolHarness.Records(Fields(("banid", "2")), Fields(("banid", "3"))));
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.AddBanAsync(clientId: 5, ip: "203.0.113.7", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.AddBanAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.AddBanAsync(ip: "203.0.113.7", durationSeconds: -1, cancellationToken: Ct));

        var client = await tools.AddBanAsync(clientId: 5, cancellationToken: Ct);
        await tools.AddBanAsync(ip: "203.0.113.7", durationSeconds: 600, reason: "spam", cancellationToken: Ct);

        Assert.Contains("2, 3", client.Done, StringComparison.Ordinal);
        Assert.False(harness.Transport.SentCommands.Single(command => command.Name == "banclient").Parameters!.ContainsKey("time"));
        var rule = harness.Transport.SentCommands.Single(command => command.Name == "banadd").Parameters!;
        Assert.Equal(("203.0.113.7", "600", "spam"), (rule["ip"], rule["time"], rule["banreason"]));
    }

    [Fact]
    public async Task A_channel_group_key_needs_its_channel()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("privilegekeyadd", ToolHarness.Records(Fields(("token", "key"))));
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageTokenAsync("add", channelGroupId: 5, cancellationToken: Ct));
        var result = await tools.ManageTokenAsync("add", channelGroupId: 5, channelId: 4, description: "Team lead", cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("1", "5", "4", "Team lead"), (sent["tokentype"], sent["tokenid1"], sent["tokenid2"], sent["tokendescription"]));
        Assert.Equal("key", result.Details["token"]);
    }

    [Fact]
    public async Task Creates_api_keys_only_with_a_known_scope_on_the_named_virtual_server()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("apikeyadd", ToolHarness.Records(Fields(("apikey", "secret"), ("id", "5"))));
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageApiKeyAsync("add", "admin", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.ManageApiKeyAsync("add", cancellationToken: Ct));
        await tools.ManageApiKeyAsync("add", "read", lifetimeDays: 0, databaseId: 3, virtualServerId: 2, cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands);
        Assert.Equal(("read", "0", "3", 2), (sent.Parameters!["scope"], sent.Parameters["lifetime"], sent.Parameters["cldbid"], sent.VirtualServerId));
    }

    [Fact]
    public async Task Temporary_passwords_need_a_duration_and_list_in_clear_text()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("servertemppasswordlist", ToolHarness.Records(Fields(("pw_clear", "secret"), ("desc", "guests"))))
            .Returns("servertemppasswordadd", ToolHarness.Records());
        var tools = new VirtualServerAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.TempPasswordAsync("add", "secret", cancellationToken: Ct));
        await tools.TempPasswordAsync("add", "secret", 3600, cancellationToken: Ct);
        var list = await tools.TempPasswordAsync("list", cancellationToken: Ct);

        var add = harness.Transport.SentCommands.Single(command => command.Name == "servertemppasswordadd").Parameters!;
        Assert.Equal(("secret", "3600", "0"), (add["pw"], add["duration"], add["tcid"]));
        Assert.False(add.ContainsKey("tcpw"));
        Assert.Equal("secret", Assert.Single(list.Records)["pw_clear"]);
    }

    [Fact]
    public async Task Copies_a_group_into_a_new_one_and_renames_only_a_named_group()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("servergroupcopy", ToolHarness.Records(Fields(("sgid", "21"))));
        var tools = new GroupAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageServerGroupAsync("rename", "New", cancellationToken: Ct));
        var copy = await tools.ManageServerGroupAsync("copy", "Moderator", 6, "template", cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("6", "0", "Moderator", "0"), (sent["ssgid"], sent["tsgid"], sent["name"], sent["type"]));
        Assert.Equal("21", copy.Details["sgid"]);
    }

    [Theory]
    [InlineData("info", "4")]
    [InlineData("error", "1")]
    [InlineData("Warning", "2")]
    [InlineData("debug", "3")]
    public async Task Maps_log_levels_to_the_server_codes(string level, string expected)
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("logadd", ToolHarness.Records());

        await new ModerationAdminTools(harness.Executor).AddLogAsync("Moved the raid channel", level, cancellationToken: Ct);

        Assert.Equal(expected, Assert.Single(harness.Transport.SentCommands).Parameters!["loglevel"]);
    }

    [Fact]
    public async Task Query_logins_and_custom_properties_insist_on_their_required_values()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageQueryLoginAsync("add", 3, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.CustomPropertyAsync("set", 3, "forum_account", cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }
}