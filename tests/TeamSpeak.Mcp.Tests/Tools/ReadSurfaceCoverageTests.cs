using System.Text.Json;

using ModelContextProtocol;

using TeamSpeak.Mcp.Resources;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The phase 5 tools and resources the first round of tests left uncovered.</summary>
public class ReadSurfaceCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_server_group_members_with_names_and_asks_for_them()
    {
        // Shaped like the live answer to servergroupclientlist sgid=6 -names.
        await using var harness = new ToolHarness();
        harness.Transport.Returns("servergroupclientlist", ToolHarness.Records(
            new Dictionary<string, string> { ["cldbid"] = "3", ["client_nickname"] = "Alice", ["client_unique_identifier"] = "abc=" },
            new Dictionary<string, string> { ["cldbid"] = "0" }));

        var members = await new GroupTools(harness.Executor).ServerGroupMembersAsync(6, cancellationToken: Ct);

        Assert.Equal(new GroupMember(3, "Alice", "abc="), Assert.Single(members.Members));
        var sent = Assert.Single(harness.Transport.SentCommands);
        Assert.Equal(("6", "-names"), (sent.Parameters!["sgid"], Assert.Single(sent.Options!)));
    }

    [Fact]
    public async Task Filters_channel_group_assignments_only_by_what_was_given()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channelgroupclientlist", ToolHarness.Records(
            new Dictionary<string, string> { ["cid"] = "2", ["cldbid"] = "9", ["cgid"] = "5" }));

        var result = await new GroupTools(harness.Executor).ChannelGroupMembersAsync(channelId: 2, groupId: 5, cancellationToken: Ct);

        Assert.Equal(new ChannelGroupAssignment(2, 9, 5), Assert.Single(result.Assignments));
        Assert.Equal(["cid", "cgid"], Assert.Single(harness.Transport.SentCommands).Parameters!.Keys.Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task Collects_custom_properties_by_identifier()
    {
        // custominfo carries cldbid on the first record only.
        await using var harness = new ToolHarness();
        harness.Transport.Returns("custominfo", ToolHarness.Records(
            new Dictionary<string, string> { ["cldbid"] = "3", ["ident"] = "forum_account", ["value"] = "alice" },
            new Dictionary<string, string> { ["ident"] = "forum_id", ["value"] = "123" }));

        var result = await new ClientDatabaseTools(harness.Executor).CustomInfoAsync(3, cancellationToken: Ct);

        Assert.Equal(3, result.DatabaseId);
        Assert.Equal("123", result.Properties["forum_id"]);
        Assert.Equal(2, result.Properties.Count);
    }

    [Fact]
    public async Task Searches_custom_properties_with_a_substring_pattern()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("customsearch", ToolHarness.Records(
            new Dictionary<string, string> { ["cldbid"] = "2", ["ident"] = "forum_account", ["value"] = "alice" }));

        var result = await new ClientDatabaseTools(harness.Executor).CustomSearchAsync("forum_account", "teamspeak", cancellationToken: Ct);

        Assert.Equal(new CustomPropertyMatch(2, "forum_account", "alice"), Assert.Single(result.Matches));
        Assert.Equal("%teamspeak%", Assert.Single(harness.Transport.SentCommands).Parameters!["pattern"]);
    }

    [Fact]
    public async Task Refuses_a_custom_search_without_an_identifier()
    {
        await using var harness = new ToolHarness();

        await Assert.ThrowsAsync<McpException>(() => new ClientDatabaseTools(harness.Executor).CustomSearchAsync("", "x", cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Shows_a_known_identity_with_the_id_it_was_asked_about()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientdbinfo", ToolHarness.Records(
            new Dictionary<string, string> { ["client_unique_identifier"] = "abc=", ["client_nickname"] = "Alice" }));

        var result = await new ClientDatabaseTools(harness.Executor).KnownClientInfoAsync(3, cancellationToken: Ct);

        Assert.Equal(("3", "Alice"), (result.Fields["cldbid"], result.Fields["client_nickname"]));
    }

    [Fact]
    public async Task Searches_unique_identities_exactly_and_with_the_uid_flag()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("clientdbfind", ToolHarness.Records(new Dictionary<string, string> { ["cldbid"] = "3" }))
            .Returns("clientgetnamefromdbid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var result = await new ClientDatabaseTools(harness.Executor).FindKnownClientsAsync("abc=", byUniqueId: true, cancellationToken: Ct);

        Assert.False(result.Truncated);
        Assert.Equal(3, Assert.Single(result.Clients).DatabaseId);

        var find = harness.Transport.SentCommands[0];
        Assert.Equal(("abc=", "-uid"), (find.Parameters!["pattern"], Assert.Single(find.Options!)));
    }

    [Fact]
    public async Task Finds_online_clients_and_channels_by_name()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("clientfind", ToolHarness.Records(new Dictionary<string, string> { ["clid"] = "20", ["client_nickname"] = "serveradmin" }))
            .Returns("channelfind", ToolHarness.Records(new Dictionary<string, string> { ["cid"] = "1", ["channel_name"] = "Default Channel" }));

        var clients = await new ClientTools(harness.Executor).FindClientsAsync("server", cancellationToken: Ct);
        var channels = await new ChannelTools(harness.Executor).FindChannelsAsync("Default", cancellationToken: Ct);

        Assert.Equal(new OnlineClientMatch(20, "serveradmin"), Assert.Single(clients.Clients));
        Assert.Equal(new ChannelMatch(1, "Default Channel"), Assert.Single(channels.Channels));
    }

    [Fact]
    public async Task Lists_complaints_about_one_client()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("complainlist", ToolHarness.Records(
            new Dictionary<string, string> { ["tcldbid"] = "3", ["tname"] = "Bob", ["fcldbid"] = "56", ["fname"] = "Carol", ["message"] = "Bad guy!", ["timestamp"] = "1259440948" }));

        var complaint = Assert.Single((await new ModerationTools(harness.Executor).ListComplaintsAsync(3, cancellationToken: Ct)).Complaints);

        Assert.Equal(new Complaint(3, "Bob", 56, "Carol", "Bad guy!", DateTimeOffset.FromUnixTimeSeconds(1259440948)), complaint);
        Assert.Equal("3", Assert.Single(harness.Transport.SentCommands).Parameters!["tcldbid"]);
    }

    [Fact]
    public async Task Lists_query_logins_and_offline_messages()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("queryloginlist", ToolHarness.Records(new Dictionary<string, string> { ["cldbid"] = "25", ["sid"] = "1", ["client_login_name"] = "bot" }))
            .Returns("messagelist", ToolHarness.Records(new Dictionary<string, string> { ["msgid"] = "4", ["cluid"] = "abc=", ["subject"] = "Hi", ["timestamp"] = "1259439465", ["flag_read"] = "1" }))
            .Returns("messageget", ToolHarness.Records(new Dictionary<string, string> { ["msgid"] = "4", ["cluid"] = "abc=", ["subject"] = "Hi", ["message"] = "Where are you?" }));

        var tools = new AccessTools(harness.Executor);

        Assert.Equal(new QueryLogin("bot", 25, 1), Assert.Single((await tools.ListQueryLoginsAsync(cancellationToken: Ct)).Logins));
        Assert.True(Assert.Single((await tools.ListMessagesAsync(cancellationToken: Ct)).Messages).IsRead);
        Assert.Equal("Where are you?", (await tools.GetMessageAsync(4, cancellationToken: Ct)).Fields["message"]);
    }

    [Fact]
    public async Task The_health_report_tool_reads_serverinfo_of_the_named_virtual_server()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("serverinfo", ToolHarness.Records(new Dictionary<string, string>
        {
            ["virtualserver_name"] = "Test",
            ["virtualserver_status"] = "online",
            ["virtualserver_clientsonline"] = "1",
            ["virtualserver_queryclientsonline"] = "1",
            ["virtualserver_maxclients"] = "32",
            ["virtualserver_channelsonline"] = "6",
        }));

        var report = await new VirtualServerTools(harness.Executor).HealthReportAsync(2, cancellationToken: Ct);

        Assert.Equal((0, 6), (report.ClientsOnline, report.ChannelsOnline));
        Assert.Equal(2, Assert.Single(harness.Transport.SentCommands).VirtualServerId);
    }

    [Fact]
    public async Task Evaluates_an_online_client_in_the_channel_it_is_in()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("permissionlist", ToolHarness.Records(new Dictionary<string, string> { ["permid"] = "226", ["permname"] = "i_client_talk_power", ["permdesc"] = "" }))
            .Returns("clientgetnamefromdbid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["clid"] = "8" }))
            .Returns("clientinfo", ToolHarness.Records(new Dictionary<string, string> { ["cid"] = "4" }))
            .Returns("permoverview", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var result = await new PermissionTools(harness.Executor, new PermissionNameCache())
            .EffectivePermissionsAsync(3, permission: "i_client_talk_power", cancellationToken: Ct);

        Assert.Equal((4, "the channel the client is in"), (result.ChannelId, result.ChannelChoice));
        Assert.Equal("8", harness.Transport.SentCommands.Single(command => command.Name == "clientinfo").Parameters!["clid"]);
    }

    [Fact]
    public async Task The_profile_resource_carries_no_credentials()
    {
        await using var harness = new ToolHarness();

        var json = new ServerResources(harness.Executor, new PermissionNameCache()).Profiles();

        using var document = JsonDocument.Parse(json);
        Assert.Equal("test", document.RootElement.GetProperty("profiles")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task The_info_client_and_permission_resources_serialise_their_tools()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("serverinfo", ToolHarness.Records(new Dictionary<string, string> { ["virtualserver_name"] = "Test" }))
            .Returns("clientlist", ToolHarness.Records(
                new Dictionary<string, string> { ["clid"] = "8", ["client_nickname"] = "Alice", ["client_type"] = "0" },
                new Dictionary<string, string> { ["clid"] = "9", ["client_nickname"] = "serveradmin", ["client_type"] = "1" }))
            .Returns("permissionlist", ToolHarness.Records(new Dictionary<string, string> { ["permid"] = "226", ["permname"] = "i_client_talk_power", ["permdesc"] = "Talk power" }));

        var resources = new ServerResources(harness.Executor, new PermissionNameCache());

        using var info = JsonDocument.Parse(await resources.InfoAsync("test", 1, Ct));
        using var clients = JsonDocument.Parse(await resources.ClientsAsync("test", 1, Ct));
        using var permissions = JsonDocument.Parse(await resources.PermissionsAsync("test", Ct));

        Assert.Equal("Test", info.RootElement.GetProperty("fields").GetProperty("virtualserver_name").GetString());
        Assert.Equal(1, clients.RootElement.GetProperty("clients").GetArrayLength());
        Assert.Equal("i_client_talk_power", permissions.RootElement[0].GetProperty("name").GetString());
    }
}