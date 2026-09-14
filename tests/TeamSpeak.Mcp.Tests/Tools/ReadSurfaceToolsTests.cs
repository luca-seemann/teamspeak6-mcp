using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The phase 5 read tools that are not about permissions.</summary>
public class ReadSurfaceToolsTests
{
    [Fact]
    public async Task Resolves_a_session_id_to_every_form_of_the_identity()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("clientgetuidfromclid", ToolHarness.Records(new Dictionary<string, string> { ["clid"] = "8", ["cluid"] = "abc=", ["nickname"] = "Alice" }))
            .Returns("clientgetnamefromuid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["cldbid"] = "3", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Records(
                new Dictionary<string, string> { ["cluid"] = "abc=", ["clid"] = "8", ["name"] = "Alice" },
                new Dictionary<string, string> { ["cluid"] = "abc=", ["clid"] = "9", ["name"] = "Alice" }));

        var identity = await new ClientDatabaseTools(harness.Executor).ResolveClientAsync(
            clientId: 8, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClientIdentity(3, "abc=", "Alice", [8, 9]), identity with { OnlineClientIds = identity.OnlineClientIds.ToList() }, new IdentityComparer());
    }

    [Fact]
    public async Task Resolves_an_offline_client_with_no_sessions()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("clientgetnamefromdbid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["cldbid"] = "3", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var identity = await new ClientDatabaseTools(harness.Executor).ResolveClientAsync(
            databaseId: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("abc=", identity.UniqueId);
        Assert.Empty(identity.OnlineClientIds);
    }

    [Fact]
    public async Task Refuses_to_resolve_from_more_than_one_id()
    {
        await using var harness = new ToolHarness();

        await Assert.ThrowsAsync<McpException>(() => new ClientDatabaseTools(harness.Executor).ResolveClientAsync(
            clientId: 8, databaseId: 3, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Pages_known_clients_with_the_total_from_the_first_record()
    {
        // As the live server answers clientdblist -count: count on the first record only.
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientdblist", ToolHarness.Records(
            new Dictionary<string, string>
            {
                ["count"] = "2",
                ["cldbid"] = "2",
                ["client_unique_identifier"] = "ServerQuery",
                ["client_nickname"] = "ServerQuery Guest",
                ["client_created"] = "1789334717",
                ["client_lastconnected"] = "1789334717",
                ["client_totalconnections"] = "0",
                ["client_description"] = "",
                ["client_lastip"] = "",
            },
            new Dictionary<string, string>
            {
                ["cldbid"] = "3",
                ["client_unique_identifier"] = "abc=",
                ["client_nickname"] = "Alice",
                ["client_created"] = "1789334805",
                ["client_lastconnected"] = "1789346058",
                ["client_totalconnections"] = "13",
                ["client_description"] = "",
                ["client_lastip"] = "172.20.0.1",
            }));

        var page = await new ClientDatabaseTools(harness.Executor).ListKnownClientsAsync(
            limit: 1000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Total);
        Assert.Equal("200", Assert.Single(harness.Transport.SentCommands).Parameters!["duration"]);
        Assert.Null(page.Clients[0].LastIp);
        Assert.Equal("172.20.0.1", page.Clients[1].LastIp);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789346058), page.Clients[1].LastConnected);
    }

    [Fact]
    public async Task Resolves_only_the_first_ten_search_matches()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("clientdbfind", ToolHarness.Records(Enumerable.Range(1, 12)
                .Select(id => new Dictionary<string, string> { ["cldbid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                .ToArray()))
            .Returns("clientgetnamefromdbid", ToolHarness.Records(new Dictionary<string, string> { ["cluid"] = "abc=", ["name"] = "Alice" }))
            .Returns("clientgetids", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        var result = await new ClientDatabaseTools(harness.Executor).FindKnownClientsAsync(
            "alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(12, result.TotalMatches);
        Assert.True(result.Truncated);
        Assert.Equal(10, result.Clients.Count);
        Assert.Equal("%alice%", harness.Transport.SentCommands[0].Parameters!["pattern"]);
    }

    [Fact]
    public async Task Reports_a_client_groups_by_name_in_both_kinds()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("servergroupsbyclientid", ToolHarness.Records(new Dictionary<string, string> { ["name"] = "Server Admin", ["sgid"] = "6", ["cldbid"] = "3" }))
            .Returns("channelgroupclientlist", ToolHarness.Records(new Dictionary<string, string> { ["cid"] = "4", ["cldbid"] = "3", ["cgid"] = "5" }))
            .Returns("channelgrouplist", ToolHarness.Records(new Dictionary<string, string> { ["cgid"] = "5", ["name"] = "Channel Admin", ["type"] = "1" }));

        var groups = await new GroupTools(harness.Executor).ClientGroupsAsync(3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Server Admin", Assert.Single(groups.ServerGroups).Name);
        Assert.Equal(new ChannelGroupMembership(4, 5, "Channel Admin"), Assert.Single(groups.ChannelGroups));
    }

    [Theory]
    [InlineData("0", "template")]
    [InlineData("1", "regular")]
    [InlineData("2", "query")]
    public async Task Names_the_kind_of_group(string type, string expected)
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("servergrouplist", ToolHarness.Records(
            new Dictionary<string, string> { ["sgid"] = "6", ["name"] = "Server Admin", ["type"] = type, ["savedb"] = "1", ["n_modifyp"] = "75" }));

        var group = Assert.Single((await new GroupTools(harness.Executor).ListServerGroupsAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Groups);

        Assert.Equal(expected, group.Type);
        Assert.Equal(75, group.NeededModifyPower);
    }

    [Fact]
    public async Task Works_out_when_a_ban_expires_and_leaves_a_permanent_one_open()
    {
        // Seconds, as the live server writes them, not the milliseconds the reference example suggests.
        await using var harness = new ToolHarness();
        harness.Transport.Returns("banlist", ToolHarness.Records(
            new Dictionary<string, string> { ["banid"] = "2", ["ip"] = "203.0.113.7", ["created"] = "1789373158", ["duration"] = "600", ["reason"] = "spam", ["invokername"] = "serveradmin" },
            new Dictionary<string, string> { ["banid"] = "3", ["uid"] = "abc=", ["created"] = "1789373158", ["duration"] = "0", ["reason"] = "" }));

        var bans = (await new ModerationTools(harness.Executor).ListBansAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Bans;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789373158 + 600), bans[0].Expires);
        Assert.Null(bans[1].Expires);
        Assert.Null(bans[1].Ip);
        Assert.Null(bans[1].Reason);
    }

    [Fact]
    public async Task Privilege_keys_need_the_write_level_because_they_are_live_credentials()
    {
        await using var readOnly = new ToolHarness();

        await Assert.ThrowsAsync<McpException>(() => new ModerationTools(readOnly.Executor).ListPrivilegeKeysAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(readOnly.Transport.SentCommands);

        await using var write = new ToolHarness(SafetyLevel.Write);
        write.Transport.Returns("privilegekeylist", ToolHarness.Records(
            new Dictionary<string, string> { ["token"] = "key", ["token_type"] = "1", ["token_id1"] = "5", ["token_id2"] = "4", ["token_created"] = "1789373160", ["token_description"] = "" }));

        var key = Assert.Single((await new ModerationTools(write.Executor).ListPrivilegeKeysAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Keys);

        Assert.Equal(("channel group", 5, 4), (key.GrantsA, key.GroupId, key.ChannelId!.Value));
        Assert.Null(key.Description);
    }

    [Fact]
    public async Task Reports_an_unlimited_api_key_as_never_expiring()
    {
        // The live server writes "unlimited" and repeats the creation time as the expiry.
        await using var harness = new ToolHarness();
        harness.Transport.Returns("apikeylist", ToolHarness.Records(
            new Dictionary<string, string> { ["id"] = "2", ["sid"] = "1", ["cldbid"] = "1", ["scope"] = "manage", ["time_left"] = "unlimited", ["created_at"] = "1789339353", ["expires_at"] = "1789339353" },
            new Dictionary<string, string> { ["id"] = "3", ["sid"] = "1", ["cldbid"] = "1", ["scope"] = "read", ["time_left"] = "85590", ["created_at"] = "1789372352", ["expires_at"] = "1789458752" }));

        var keys = (await new AccessTools(harness.Executor).ListApiKeysAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Keys;

        Assert.True(keys[0].NeverExpires);
        Assert.Null(keys[0].Expires);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789458752), keys[1].Expires);
        Assert.Equal("*", Assert.Single(harness.Transport.SentCommands).Parameters!["cldbid"]);
    }

    [Fact]
    public void Splits_a_log_line_into_its_columns()
    {
        var entry = LogTools.ParseLine("2026-09-14 08:05:53.383146|INFO    |VirtualServer |1  |query client connected 'serveradmin'(id:1)");

        Assert.Equal(
            new LogEntry("2026-09-14 08:05:53.383146", "INFO", "VirtualServer", "1", "query client connected 'serveradmin'(id:1)"),
            entry);
    }

    [Fact]
    public void Keeps_a_line_without_columns_whole()
    {
        var entry = LogTools.ParseLine("something unexpected");

        Assert.Equal("something unexpected", entry.Message);
    }

    [Fact]
    public async Task Reads_the_log_position_from_the_first_record()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("logview", ToolHarness.Records(
            new Dictionary<string, string> { ["last_pos"] = "4714", ["file_size"] = "5107", ["l"] = "2026-09-14 08:05:08.609844|INFO    |VirtualServerBase|1  |first" },
            new Dictionary<string, string> { ["l"] = "2026-09-14 08:05:53.383146|INFO    |VirtualServer |1  |second" }));

        var page = await new LogTools(harness.Executor).ViewLogAsync(
            lines: 500, instance: true, beforePosition: 9000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((4714L, 5107L), (page.Position, page.FileSize));
        Assert.Equal(["first", "second"], page.Entries.Select(entry => entry.Message));

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("100", "1", "9000"), (sent["lines"], sent["instance"], sent["begin_pos"]));
    }

    [Fact]
    public void A_healthy_server_has_no_findings_and_query_clients_do_not_count_as_people()
    {
        var report = VirtualServerTools.BuildHealthReport(ServerInfo(clientsOnline: 5, queryClients: 2, maxClients: 32, loss: "0.0000", ping: "12.0000"));

        Assert.Equal(3, report.ClientsOnline);
        Assert.Equal(2, report.QueryClientsOnline);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Flags_full_slots_packet_loss_and_high_ping_in_plain_words()
    {
        var report = VirtualServerTools.BuildHealthReport(ServerInfo(clientsOnline: 31, queryClients: 1, maxClients: 32, reserved: 2, loss: "0.0620", ping: "180.5000"));

        Assert.Equal(100, report.SlotUsagePercent);
        Assert.Equal(6.2, report.PacketLossPercent);
        Assert.Equal(3, report.Findings.Count);
        Assert.Contains(report.Findings, finding => finding.Contains("slots", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Contains("packet loss", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Contains("ping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_an_empty_search_pattern_before_sending()
    {
        await using var harness = new ToolHarness();

        await Assert.ThrowsAsync<McpException>(() => new ChannelTools(harness.Executor).FindChannelsAsync(
            " ", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    private static QueryRecord ServerInfo(int clientsOnline, int queryClients, int maxClients, string loss, string ping, int reserved = 0) =>
        new(new Dictionary<string, string>
        {
            ["virtualserver_name"] = "Test",
            ["virtualserver_status"] = "online",
            ["virtualserver_clientsonline"] = clientsOnline.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["virtualserver_queryclientsonline"] = queryClients.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["virtualserver_maxclients"] = maxClients.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["virtualserver_reserved_slots"] = reserved.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["virtualserver_total_packetloss_total"] = loss,
            ["virtualserver_total_packetloss_speech"] = loss,
            ["virtualserver_total_ping"] = ping,
        });

    private sealed class IdentityComparer : IEqualityComparer<ClientIdentity>
    {
        public bool Equals(ClientIdentity? x, ClientIdentity? y) =>
            x is not null && y is not null
            && (x.DatabaseId, x.UniqueId, x.Nickname) == (y.DatabaseId, y.UniqueId, y.Nickname)
            && x.OnlineClientIds.SequenceEqual(y.OnlineClientIds);

        public int GetHashCode(ClientIdentity obj) => obj.DatabaseId;
    }
}