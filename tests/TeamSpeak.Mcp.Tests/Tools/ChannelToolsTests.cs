using ModelContextProtocol;

using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

public class ChannelToolsTests
{
    private static Dictionary<string, string> Channel(int cid, int pid, int order, string name) => new()
    {
        ["cid"] = cid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pid"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["channel_order"] = order.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["channel_name"] = name,
        ["total_clients"] = "0",
    };

    // Returned deliberately out of display order, as the server does. channel_order names the
    // sibling directly above: Lobby, Games (with CS then LoL beneath it), AFK.
    private static readonly Dictionary<string, string>[] Server =
    [
        Channel(2, 0, 3, "AFK"),
        Channel(5, 3, 4, "LoL"),
        Channel(1, 0, 0, "Lobby"),
        Channel(4, 3, 0, "CS"),
        Channel(3, 0, 1, "Games"),
    ];

    [Fact]
    public async Task Lists_channels_flat_in_display_order()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var result = await new ChannelTools(harness.Executor).ListChannelsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Lobby", "Games", "CS", "LoL", "AFK"], result.Channels.Select(channel => channel.Name));
        Assert.All(result.Channels, channel => Assert.Null(channel.Children));
    }

    [Fact]
    public async Task Nests_sub_channels_when_a_tree_is_requested()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var result = await new ChannelTools(harness.Executor).ListChannelsAsync(
            tree: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Lobby", "Games", "AFK"], result.Channels.Select(channel => channel.Name));
        Assert.Equal(["CS", "LoL"], result.Channels[1].Children!.Select(channel => channel.Name));
        Assert.Null(result.Channels[0].Children);
    }

    [Fact]
    public async Task Pages_a_flat_list_in_display_order_and_counts_every_channel()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var result = await new ChannelTools(harness.Executor).ListChannelsAsync(
            offset: 1,
            limit: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Games", "CS"], result.Channels.Select(channel => channel.Name));
        Assert.Equal((5, 1), (result.Total, result.Offset));
    }

    [Fact]
    public async Task Clamps_a_negative_offset_and_a_zero_limit()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var result = await new ChannelTools(harness.Executor).ListChannelsAsync(
            offset: -3,
            limit: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Lobby", Assert.Single(result.Channels).Name);
        Assert.Equal((5, 0), (result.Total, result.Offset));
    }

    [Fact]
    public async Task Refuses_to_cut_a_tree_with_more_channels_than_the_limit()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var ex = await Assert.ThrowsAsync<McpException>(() => new ChannelTools(harness.Executor).ListChannelsAsync(
            tree: true,
            limit: 4,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("5 channels", ex.Message, StringComparison.Ordinal);
        Assert.Contains("limit 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_a_whole_tree_within_the_limit_with_the_channel_count()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        var result = await new ChannelTools(harness.Executor).ListChannelsAsync(
            tree: true,
            limit: 5,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Channels.Count);
        Assert.Equal((5, 0), (result.Total, result.Offset));
    }

    [Fact]
    public async Task Refuses_an_offset_on_a_tree_before_asking_the_server()
    {
        await using var harness = new ToolHarness();

        var ex = await Assert.ThrowsAsync<McpException>(() => new ChannelTools(harness.Executor).ListChannelsAsync(
            tree: true,
            offset: 2,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("offset", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Asks_for_topics_flags_and_limits_on_the_named_virtual_server()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(Server));

        await new ChannelTools(harness.Executor).ListChannelsAsync(
            virtualServerId: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        var sent = Assert.Single(harness.Transport.SentCommands);
        Assert.Equal(["-topic", "-flags", "-limits"], sent.Options);
        Assert.Equal(3, sent.VirtualServerId);
    }

    [Fact]
    public void Keeps_every_channel_when_the_order_chain_is_broken()
    {
        // A cycle in parents and a chain pointing at a missing sibling must not lose channels.
        var records = new[]
        {
            Channel(1, 0, 99, "Dangling"),
            Channel(2, 3, 0, "CycleA"),
            Channel(3, 2, 0, "CycleB"),
        }.Select(fields => new QueryRecord(fields));

        var channels = ChannelTree.Build(records, nested: false);

        Assert.Equal(3, channels.Count);
    }

    [Fact]
    public void Describes_flags_and_limits_in_plain_terms()
    {
        var fields = Channel(1, 0, 0, "Lobby");
        fields["channel_topic"] = string.Empty;
        fields["channel_flag_default"] = "1";
        fields["channel_flag_password"] = "1";
        fields["channel_flag_permanent"] = "0";
        fields["channel_flag_semi_permanent"] = "1";

        var channel = Assert.Single(ChannelTree.Build([new QueryRecord(fields)], nested: false));

        Assert.Null(channel.Topic);
        Assert.True(channel.IsDefault);
        Assert.True(channel.HasPassword);
        Assert.Equal("semi-permanent", channel.Lifetime);
        Assert.Equal(-1, channel.MaxClients);
    }

    [Fact]
    public async Task Channel_info_includes_the_id_it_was_asked_about()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channelinfo", ToolHarness.Records(new Dictionary<string, string>
        {
            ["channel_name"] = "Lobby",
        }));

        var result = await new ChannelTools(harness.Executor).ChannelInfoAsync(
            7,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("7", result.Fields["cid"]);
        Assert.Equal("Lobby", result.Fields["channel_name"]);
        Assert.Equal("7", Assert.Single(harness.Transport.SentCommands).Parameters!["cid"]);
    }
}