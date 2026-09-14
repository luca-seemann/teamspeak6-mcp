using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Exercises both transports against a live TeamSpeak 6 server.
/// </summary>
/// <remarks>
/// Skipped unless <c>TSMCP_TEST_HOST</c> is set. Set <c>TSMCP_TEST_PASSWORD</c> for the SSH tests
/// and <c>TSMCP_TEST_APIKEY</c> for the WebQuery tests; each group skips itself without its
/// credential. Every test shares one SSH session via <see cref="LiveServerFixture"/>.
/// </remarks>
public sealed class TransportIntegrationTests(LiveServerFixture server) : IClassFixture<LiveServerFixture>
{
    [RequiresTeamSpeakServerFact]
    public async Task Ssh_transport_completes_a_command_round_trip()
    {
        var response = await server.Ssh.SendAsync(
            new QueryCommand("version"),
            TestContext.Current.CancellationToken);

        Assert.True(response.Error.IsSuccess);
        var record = Assert.Single(response.Records);
        Assert.StartsWith("6.", record.GetRequired("version"), StringComparison.Ordinal);
        Assert.True(server.Ssh.SupportsEvents);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Ssh_transport_survives_a_burst_that_would_otherwise_trip_flood_protection()
    {
        // Twenty commands back to back is well past the roughly five that get an unpaced client
        // blocked. The FloodGuard is the only reason this passes.
        for (var i = 0; i < 20; i++)
        {
            var response = await server.Ssh.SendAsync(
                new QueryCommand("version"),
                TestContext.Current.CancellationToken);

            Assert.True(response.Error.IsSuccess, $"Command {i + 1} of 20 failed: {response.Error.Message}");
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task Ssh_transport_selects_a_virtual_server_and_reads_its_channels()
    {
        var channels = await server.Ssh.SendAsync(
            new QueryCommand("channellist", VirtualServerId: 1),
            TestContext.Current.CancellationToken);

        Assert.True(channels.Error.IsSuccess);
        Assert.NotEmpty(channels.Records);
        Assert.All(channels.Records, record => Assert.NotEmpty(record.GetRequired("cid")));
    }

    [RequiresTeamSpeakServerFact]
    public async Task Ssh_transport_reports_a_server_side_failure_without_throwing()
    {
        // A refused command is data, not an exception.
        var response = await server.Ssh.SendAsync(
            new QueryCommand("clientinfo"),
            TestContext.Current.CancellationToken);

        Assert.False(response.Error.IsSuccess);
        Assert.Equal(QueryErrorCode.ParameterNotFound, response.Error.Id);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Ssh_transport_unescapes_a_value_the_server_escaped()
    {
        var info = await server.Ssh.SendAsync(
            new QueryCommand("serverinfo", VirtualServerId: 1),
            TestContext.Current.CancellationToken);

        // The server sends this as TeamSpeak\s6\sServer; a caller must never see the escapes.
        var name = Assert.Single(info.Records).GetRequired("virtualserver_name");
        Assert.DoesNotContain(@"\s", name, StringComparison.Ordinal);
    }

    [RequiresWebQueryFact]
    public async Task Web_query_transport_completes_a_command_round_trip()
    {
        await using var http = new HttpQueryTransport(LiveServerFixture.Profile());

        var response = await http.SendAsync(
            new QueryCommand("version"),
            TestContext.Current.CancellationToken);

        Assert.True(response.Error.IsSuccess);
        Assert.StartsWith("6.", Assert.Single(response.Records).GetRequired("version"), StringComparison.Ordinal);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_scoped_command_reaches_the_virtual_server_it_names()
    {
        var info = await server.Ssh.SendAsync(
            new QueryCommand("serverinfo", VirtualServerId: 1),
            TestContext.Current.CancellationToken);

        Assert.True(info.Error.IsSuccess);
        Assert.Equal("1", Assert.Single(info.Records).GetRequired("virtualserver_id"));
    }

    [RequiresTeamSpeakServerFact]
    public async Task Concurrent_scoped_and_instance_wide_commands_all_succeed_over_one_session()
    {
        // Tool calls share the session. Selection and command have to stay paired even when
        // callers interleave, and the slot and the flood guard must not deadlock each other.
        var ct = TestContext.Current.CancellationToken;

        var responses = await Task.WhenAll(
            server.Ssh.SendAsync(new QueryCommand("serverinfo", VirtualServerId: 1), ct),
            server.Ssh.SendAsync(new QueryCommand("serverlist"), ct),
            server.Ssh.SendAsync(new QueryCommand("channellist", VirtualServerId: 1), ct),
            server.Ssh.SendAsync(new QueryCommand("version"), ct));

        Assert.All(responses, response => Assert.True(response.Error.IsSuccess, response.Error.Message));
        Assert.Equal("1", Assert.Single(responses[0].Records).GetRequired("virtualserver_id"));
    }

    [RequiresWebQueryFact]
    public async Task Both_transports_return_the_same_channel_list()
    {
        // The parity the whole design rests on, checked against a live server rather than fixtures.
        // SSH sends "use" underneath while the WebQuery puts the id in the URL; callers never know.
        await using var http = new HttpQueryTransport(LiveServerFixture.Profile());

        var viaSsh = await server.Ssh.SendAsync(
            new QueryCommand("channellist", VirtualServerId: 1),
            TestContext.Current.CancellationToken);
        var viaHttp = await http.SendAsync(
            new QueryCommand("channellist", VirtualServerId: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            viaSsh.Records.Select(r => (r.GetRequired("cid"), r.GetRequired("channel_name"))),
            viaHttp.Records.Select(r => (r.GetRequired("cid"), r.GetRequired("channel_name"))));
    }
}