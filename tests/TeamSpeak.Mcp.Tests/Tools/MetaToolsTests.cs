using System.Text.Json;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tests.Tools;

public class MetaToolsTests
{
    [Theory]
    [InlineData("use")]
    [InlineData("quit")]
    [InlineData("logout")]
    [InlineData("login")]
    [InlineData("servernotifyregister")]
    public async Task Raw_query_refuses_session_commands_whatever_the_safety_level(string command)
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);

        await Assert.ThrowsAsync<McpException>(() => new MetaTools(harness.Executor).QueryRawAsync(
            command,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Raw_query_refuses_a_change_on_a_read_only_profile_before_sending_anything()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly);

        var ex = await Assert.ThrowsAsync<McpException>(() => new MetaTools(harness.Executor).QueryRawAsync(
            "channeledit",
            new Dictionary<string, string> { ["cid"] = "1", ["channel_name"] = "x" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
        Assert.Contains("Write", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'channeledit'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_query_treats_an_unclassified_command_as_destructive()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);

        var ex = await Assert.ThrowsAsync<McpException>(() => new MetaTools(harness.Executor).QueryRawAsync(
            "somefuturecommand",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Destructive", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Raw_query_passes_parameters_options_and_virtual_server_through()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("clientdbfind", ToolHarness.Records(new Dictionary<string, string> { ["cldbid"] = "3" }));

        var result = await new MetaTools(harness.Executor).QueryRawAsync(
            "clientdbfind",
            new Dictionary<string, string> { ["pattern"] = "Alice" },
            ["-uid"],
            virtualServerId: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        var sent = Assert.Single(harness.Transport.SentCommands);
        Assert.Equal("Alice", sent.Parameters!["pattern"]);
        Assert.Equal(["-uid"], sent.Options);
        Assert.Equal(2, sent.VirtualServerId);
        Assert.Equal("ReadOnly", result.SafetyLevel);
        Assert.Equal("3", Assert.Single(result.Records)["cldbid"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("clientdbfind pattern=Alice")]
    public async Task Raw_query_insists_on_a_bare_command_name(string command)
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);

        await Assert.ThrowsAsync<McpException>(() => new MetaTools(harness.Executor).QueryRawAsync(
            command,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Explains_an_api_key_scope_refusal()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns(
            "serverinfo",
            ToolHarness.Error(QueryErrorCode.OutOfScope, "out of scope", "command not in api key scope"));

        var ex = await Assert.ThrowsAsync<McpException>(() => new VirtualServerTools(harness.Executor).VirtualServerInfoAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("5120", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scope=manage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_that_an_empty_value_counts_as_a_missing_parameter()
    {
        var message = QueryExecutor.DescribeRefusal(
            "test",
            "channelfind",
            new QueryError(QueryErrorCode.MissingRequiredParameter, "missing required parameter"));

        Assert.Contains("1542", message, StringComparison.Ordinal);
        Assert.Contains("empty", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_that_a_value_was_too_long()
    {
        var message = QueryExecutor.DescribeRefusal(
            "test",
            "channelgroupadd",
            new QueryError(QueryErrorCode.InvalidParameterSize, "invalid parameter size"));

        Assert.Contains("1541", message, StringComparison.Ordinal);
        Assert.Contains("too long", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_connection_failure_as_a_tool_error_naming_the_profile()
    {
        await using var connections = new QueryConnectionManager(
            new ProfileRegistry([new QueryProfile { Name = "prod", Host = "ts.example.com", Password = "secret" }]),
            (_, _, _) => Task.FromException<IQueryTransport>(new TimeoutException("connection timed out")));

        var tools = new MetaTools(new QueryExecutor(connections, new SafetyPolicy()));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.WhoAmIAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("'prod'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("connection timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_profiles_without_contacting_a_server_or_revealing_credentials()
    {
        await using var connections = new QueryConnectionManager(
            new ProfileRegistry(
            [
                new QueryProfile { Name = "prod", Host = "prod.example.com", Password = "hunter2" },
                new QueryProfile
                {
                    Name = "web", Host = "web.example.com", WebQueryUrl = new Uri("http://web.example.com:10080"),
                    ApiKey = "secret-key", DefaultVirtualServerId = 2,
                },
            ]),
            (_, _, _) => throw new InvalidOperationException("must not connect"));

        var policy = new SafetyPolicy(SafetyLevel.ReadOnly, new Dictionary<string, SafetyLevel> { ["web"] = SafetyLevel.Write });
        var result = new MetaTools(new QueryExecutor(connections, policy)).ListProfiles();

        Assert.Equal(["prod", "web"], result.Profiles.Select(profile => profile.Name));
        Assert.Equal("ssh", result.Profiles[0].Interface);
        Assert.True(result.Profiles[0].EventsAvailable);
        Assert.Equal("webquery", result.Profiles[1].Interface);
        Assert.Equal("Write", result.Profiles[1].Safety);
        Assert.Equal(2, result.Profiles[1].DefaultVirtualServerId);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instance_info_combines_four_commands_in_one_call()
    {
        await using var harness = new ToolHarness();
        harness.Transport
            .Returns("version", ToolHarness.Records(new Dictionary<string, string> { ["version"] = "6.0.0-beta12.1" }))
            .Returns("hostinfo", ToolHarness.Records(new Dictionary<string, string> { ["instance_uptime"] = "223" }))
            .Returns("instanceinfo", ToolHarness.Records(new Dictionary<string, string> { ["serverinstance_filetransfer_port"] = "30033" }))
            .Returns("bindinglist", ToolHarness.Records(
                new Dictionary<string, string> { ["ip"] = "0.0.0.0" },
                new Dictionary<string, string> { ["ip"] = "::" }));

        var result = await new MetaTools(harness.Executor).InstanceInfoAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("6.0.0-beta12.1", result.Version["version"]);
        Assert.Equal("223", result.Host["instance_uptime"]);
        Assert.Equal("30033", result.Instance["serverinstance_filetransfer_port"]);
        Assert.Equal(["0.0.0.0", "::"], result.BoundAddresses);
        Assert.Equal(
            ["version", "hostinfo", "instanceinfo", "bindinglist"],
            harness.Transport.SentCommands.Select(command => command.Name));
    }
}