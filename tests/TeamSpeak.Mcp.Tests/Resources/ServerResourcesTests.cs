using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Mcp.Resources;
using TeamSpeak.Mcp.Tests.Tools;
using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.Tests.Resources;

public class ServerResourcesTests
{
    [Fact]
    public void Serves_the_planned_resource_templates()
    {
        using var host = McpHostFactory.CreateStdioHost([]);

        var uris = host.Services.GetServices<McpServerResource>()
            .Select(resource => resource.ProtocolResourceTemplate.UriTemplate)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "ts://profiles",
                "ts://{profile}/permissions",
                "ts://{profile}/{virtualServerId}/channels",
                "ts://{profile}/{virtualServerId}/clients",
                "ts://{profile}/{virtualServerId}/groups",
                "ts://{profile}/{virtualServerId}/info",
            ],
            uris);
    }

    [Fact]
    public async Task The_channel_resource_is_the_channel_tree_as_json()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channellist", ToolHarness.Records(
            new Dictionary<string, string> { ["cid"] = "1", ["pid"] = "0", ["channel_order"] = "0", ["channel_name"] = "Lobby" },
            new Dictionary<string, string> { ["cid"] = "2", ["pid"] = "1", ["channel_order"] = "0", ["channel_name"] = "Games" }));

        var json = await new ServerResources(harness.Executor, new PermissionNameCache())
            .ChannelsAsync("test", 1, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var lobby = document.RootElement.GetProperty("channels")[0];
        Assert.Equal("Lobby", lobby.GetProperty("name").GetString());
        Assert.Equal("Games", lobby.GetProperty("children")[0].GetProperty("name").GetString());
        Assert.Equal(1, Assert.Single(harness.Transport.SentCommands).VirtualServerId);
    }

    [Fact]
    public async Task The_group_resource_combines_both_kinds_of_group()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("servergrouplist", ToolHarness.Records(new Dictionary<string, string> { ["sgid"] = "6", ["name"] = "Server Admin", ["type"] = "1" }))
            .Returns("channelgrouplist", ToolHarness.Records(new Dictionary<string, string> { ["cgid"] = "5", ["name"] = "Channel Admin", ["type"] = "1" }));

        var json = await new ServerResources(harness.Executor, new PermissionNameCache())
            .GroupsAsync("test", 1, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Server Admin", document.RootElement.GetProperty("serverGroups")[0].GetProperty("name").GetString());
        Assert.Equal("Channel Admin", document.RootElement.GetProperty("channelGroups")[0].GetProperty("name").GetString());
    }
}