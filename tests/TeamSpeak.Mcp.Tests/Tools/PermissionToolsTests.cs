using ModelContextProtocol;

using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

public class PermissionToolsTests
{
    private static Dictionary<string, string> Permission(int id, string name) =>
        new() { ["permid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["permname"] = name, ["permdesc"] = $"About {name}" };

    private static Dictionary<string, string> Overview(int t, int id1, int id2, int p, int v, bool negated = false, bool skip = false) => new()
    {
        ["t"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["id1"] = id1.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["id2"] = id2.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["p"] = p.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["v"] = v.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["n"] = negated ? "1" : "0",
        ["s"] = skip ? "1" : "0",
    };

    private static ToolHarness Harness()
    {
        var harness = new ToolHarness();
        harness.Transport
            .Returns("permissionlist", ToolHarness.Records(
                Permission(89, "i_channel_needed_join_power"),
                Permission(153, "i_client_kick_from_channel_power"),
                Permission(226, "i_client_talk_power"),
                Permission(300, "b_client_skip_channelgroup_permissions")))
            .Returns("servergrouplist", ToolHarness.Records(
                new Dictionary<string, string> { ["sgid"] = "6", ["name"] = "Server Admin", ["type"] = "1" },
                new Dictionary<string, string> { ["sgid"] = "7", ["name"] = "Normal", ["type"] = "1" }))
            .Returns("channelgrouplist", ToolHarness.Records(
                new Dictionary<string, string> { ["cgid"] = "8", ["name"] = "Guest", ["type"] = "1" }));
        return harness;
    }

    private static PermissionTools Tools(ToolHarness harness) => new(harness.Executor, new PermissionNameCache());

    [Fact]
    public async Task A_channel_client_permission_decides_over_the_client_and_its_server_groups()
    {
        // The exact rows the live server returned for i_client_talk_power with all three layers set.
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", ToolHarness.Records(
            Overview(0, 6, 0, 226, 75),
            Overview(1, 3, 0, 226, 10),
            Overview(4, 1, 3, 226, 20)));

        var result = await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_talk_power", channelId: 1, cancellationToken: TestContext.Current.CancellationToken);

        var talk = Assert.Single(result.Permissions);
        Assert.Equal(20, talk.Value);
        Assert.Equal("the client's permission in channel 1", talk.DecidedBy);
        Assert.Equal(["server group", "client", "channel client"], talk.Contributions.Select(c => c.Kind));
        Assert.Equal("server group 'Server Admin' (6)", talk.Contributions[0].Source);
        Assert.Single(talk.Contributions, contribution => contribution.Decided);
    }

    [Fact]
    public async Task A_skip_flag_on_a_server_group_keeps_the_channel_layers_out()
    {
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", ToolHarness.Records(
            Overview(0, 6, 0, 226, 75, skip: true),
            Overview(3, 1, 8, 226, 0)));

        var talk = Assert.Single((await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_talk_power", channelId: 1, cancellationToken: TestContext.Current.CancellationToken)).Permissions);

        Assert.Equal(75, talk.Value);
        Assert.True(talk.SkipApplied);
        Assert.Equal("channel group 'Guest' (8) in channel 1", talk.Contributions[1].Source);
        Assert.False(talk.Contributions[1].Decided);
        Assert.NotNull(talk.Contributions[1].Note);
    }

    [Fact]
    public async Task A_client_holding_the_skip_channel_group_permission_keeps_its_server_group_value()
    {
        // Measured live: Server Admin grants 75 talk power and b_client_skip_channelgroup_permissions, a
        // channel group grants 62, and the server computes 75.
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", command => command.Parameters!["permid"] == "300"
            ? ToolHarness.Records(Overview(0, 6, 0, 300, 1))
            : ToolHarness.Records(Overview(0, 6, 0, 226, 75), Overview(3, 1, 8, 226, 62)));

        var talk = Assert.Single((await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_talk_power", channelId: 1, cancellationToken: TestContext.Current.CancellationToken)).Permissions);

        Assert.Equal(75, talk.Value);
        Assert.Equal("server group 'Server Admin' (6)", talk.DecidedBy);
        Assert.False(talk.Contributions[1].Decided);
        Assert.Contains("b_client_skip_channelgroup_permissions", talk.Contributions[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_groups_give_the_highest_value_unless_negated_where_the_lowest_wins()
    {
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", ToolHarness.Records(
            Overview(0, 6, 0, 226, 75),
            Overview(0, 7, 0, 226, 25, negated: true)));

        var talk = Assert.Single((await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_talk_power", channelId: 1, cancellationToken: TestContext.Current.CancellationToken)).Permissions);

        Assert.Equal(25, talk.Value);
        Assert.Equal("server group 'Normal' (7)", talk.DecidedBy);
    }

    [Fact]
    public async Task A_channel_permission_is_a_requirement_not_a_grant()
    {
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", ToolHarness.Records(Overview(2, 1, 0, 89, 50)));

        var join = Assert.Single((await Tools(harness).EffectivePermissionsAsync(
            3, search: "needed_join", channelId: 1, cancellationToken: TestContext.Current.CancellationToken)).Permissions);

        Assert.Null(join.Value);
        Assert.False(Assert.Single(join.Contributions).Decided);
        Assert.Contains("requirement", join.Contributions[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_plainly_when_nothing_assigns_the_permission()
    {
        await using var harness = Harness();
        harness.Transport.Returns("permoverview", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var kick = Assert.Single((await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_kick_from_channel_power", channelId: 1, cancellationToken: TestContext.Current.CancellationToken)).Permissions);

        Assert.Null(kick.Value);
        Assert.Empty(kick.Contributions);
    }

    [Fact]
    public async Task Evaluates_an_offline_client_in_the_default_channel()
    {
        await using var harness = Harness();
        harness.Transport
            .Returns("clientgetnamefromdbid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["cldbid"] = "3", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"))
            .Returns("channellist", ToolHarness.Records(
                new Dictionary<string, string> { ["cid"] = "2", ["channel_flag_default"] = "0" },
                new Dictionary<string, string> { ["cid"] = "5", ["channel_flag_default"] = "1" }))
            .Returns("permoverview", ToolHarness.Records(Overview(0, 6, 0, 226, 75)));

        var result = await Tools(harness).EffectivePermissionsAsync(
            3, permission: "i_client_talk_power", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.ChannelId);
        Assert.Contains("offline", result.ChannelChoice, StringComparison.Ordinal);
        Assert.Equal("5", harness.Transport.SentCommands.Last(command => command.Name == "permoverview").Parameters!["cid"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("i_client_talk_power", "talk")]
    public async Task Insists_on_exactly_one_of_permission_and_search(string? permission, string? search)
    {
        await using var harness = Harness();

        await Assert.ThrowsAsync<McpException>(() => Tools(harness).EffectivePermissionsAsync(
            3, permission, search, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Points_to_the_permission_search_for_an_unknown_name()
    {
        await using var harness = Harness();

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(harness).FindPermissionAsync(
            "i_client_teleport_power", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("ts_perm_list", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "permfind");
    }

    [Fact]
    public async Task Finds_holders_of_every_kind_with_the_ids_where_the_server_puts_them()
    {
        // Rows as the live server returned them for i_client_talk_power.
        await using var harness = Harness();
        harness.Transport.Returns("permfind", ToolHarness.Records(
            new Dictionary<string, string> { ["t"] = "0", ["id1"] = "6", ["id2"] = "0", ["p"] = "226" },
            new Dictionary<string, string> { ["t"] = "1", ["id1"] = "3", ["id2"] = "0", ["p"] = "226" },
            new Dictionary<string, string> { ["t"] = "3", ["id1"] = "0", ["id2"] = "8", ["p"] = "226" },
            new Dictionary<string, string> { ["t"] = "4", ["id1"] = "1", ["id2"] = "3", ["p"] = "226" }));

        var result = await Tools(harness).FindPermissionAsync("i_client_talk_power", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["server group", "client", "channel group", "channel client"], result.Holders.Select(holder => holder.Kind));
        Assert.Equal(("Server Admin", 6), (result.Holders[0].GroupName!, result.Holders[0].ServerGroupId!.Value));
        Assert.Equal(3, result.Holders[1].DatabaseId);
        Assert.Equal(("Guest", 8, 0), (result.Holders[2].GroupName!, result.Holders[2].ChannelGroupId!.Value, result.Holders[2].ChannelId!.Value));
        Assert.Equal((1, 3), (result.Holders[3].ChannelId!.Value, result.Holders[3].DatabaseId!.Value));
    }

    [Theory]
    [InlineData(6, null, null, null, "servergrouppermlist")]
    [InlineData(null, 8, null, null, "channelgrouppermlist")]
    [InlineData(null, null, 1, null, "channelpermlist")]
    [InlineData(null, null, null, 3, "clientpermlist")]
    [InlineData(null, null, 1, 3, "channelclientpermlist")]
    public async Task Picks_the_list_command_from_the_target_given(int? sgid, int? cgid, int? cid, int? cldbid, string expected)
    {
        await using var harness = Harness();
        harness.Transport.Returns(expected, ToolHarness.Records(
            new Dictionary<string, string> { ["permid"] = "226", ["permvalue"] = "75", ["permnegated"] = "0", ["permskip"] = "1" }));

        var result = await Tools(harness).AssignedPermissionsAsync(sgid, cgid, cid, cldbid, cancellationToken: TestContext.Current.CancellationToken);

        var value = Assert.Single(result.Permissions);
        Assert.Equal(("i_client_talk_power", 75, true), (value.Name, value.Value, value.Skip));
        Assert.Contains(harness.Transport.SentCommands, command => command.Name == expected);
    }

    [Fact]
    public async Task Refuses_an_ambiguous_target()
    {
        await using var harness = Harness();

        await Assert.ThrowsAsync<McpException>(() => Tools(harness).AssignedPermissionsAsync(
            6, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Searches_names_and_descriptions_and_fetches_the_catalog_only_once()
    {
        await using var harness = Harness();
        var tools = Tools(harness);

        var talk = await tools.ListPermissionsAsync("talk", cancellationToken: TestContext.Current.CancellationToken);
        var about = await tools.ListPermissionsAsync("about i_channel", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("i_client_talk_power", Assert.Single(talk.Permissions).Name);
        Assert.Equal(1, about.TotalMatches);
        Assert.Single(harness.Transport.SentCommands, command => command.Name == "permissionlist");
    }
}