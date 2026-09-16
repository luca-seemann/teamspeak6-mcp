using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>Which tools a deployment lists, when configuration switches groups off.</summary>
public class ToolGroupsTests
{
    private static List<string> ListedTools(IHost host) =>
        host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Select(tool => tool.ProtocolTool.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Every_tool_belongs_to_a_group()
    {
        using var host = McpHostFactory.CreateStdioHost([]);

        Assert.DoesNotContain(ListedTools(host), name => ToolGroups.Of(name) is null);
    }

    [Fact]
    public void Every_group_holds_at_least_one_tool()
    {
        using var host = McpHostFactory.CreateStdioHost([]);
        var used = ListedTools(host).Select(ToolGroups.Of).ToHashSet();

        Assert.All(ToolGroups.Names, group => Assert.Contains(group, used));
    }

    [Theory]
    [InlineData("ts_client_list", "clients")]
    [InlineData("ts_client_groups", "groups")]
    [InlineData("ts_client_channelgroup_set", "groups")]
    [InlineData("ts_channel_list", "channels")]
    [InlineData("ts_channelgroup_list", "groups")]
    [InlineData("ts_instance_info", "servers")]
    [InlineData("ts_query_raw", "raw")]
    [InlineData("ts_profiles_list", "core")]
    public void Names_that_share_a_prefix_land_in_the_right_group(string tool, string group) =>
        Assert.Equal(group, ToolGroups.Of(tool));

    [Fact]
    public void Switched_off_groups_leave_exactly_their_tools_out()
    {
        using var all = McpHostFactory.CreateStdioHost([]);
        using var some = McpHostFactory.CreateStdioHost(["--TeamSpeak:DisabledToolGroups=files, Events"]);

        var expected = ListedTools(all).Where(name => ToolGroups.Of(name) is not ("files" or "events")).ToList();

        Assert.Equal(expected, ListedTools(some));
        Assert.True(expected.Count < ListedTools(all).Count);
    }

    [Fact]
    public void Groups_can_also_be_listed_as_an_array()
    {
        using var host = McpHostFactory.CreateStdioHost(["--TeamSpeak:DisabledToolGroups:0=raw", "--TeamSpeak:DisabledToolGroups:1=access"]);

        var listed = ListedTools(host);

        Assert.DoesNotContain("ts_query_raw", listed);
        Assert.DoesNotContain("ts_apikey_list", listed);
        Assert.Contains("ts_profiles_list", listed);
    }

    [Fact]
    public void An_unknown_group_stops_the_start_and_names_the_real_ones()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => McpHostFactory.CreateStdioHost(["--TeamSpeak:DisabledToolGroups=file"]));

        Assert.Contains("'file'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("files", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_core_group_cannot_be_switched_off() =>
        Assert.Throws<InvalidOperationException>(() => McpHostFactory.CreateStdioHost(["--TeamSpeak:DisabledToolGroups=core"]));
}