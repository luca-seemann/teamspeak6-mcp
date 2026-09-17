using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The three tools a coverage check found without any test of their own.</summary>
public class WriteToolsCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_an_identity_needs_destructive_and_sends_its_database_id()
    {
        await using var write = new ToolHarness(SafetyLevel.Write);
        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(write.Executor).DeleteIdentityAsync(4, "Alice", cancellationToken: Ct));
        Assert.Empty(write.Transport.SentCommands);

        await using var destructive = new ToolHarness(SafetyLevel.Destructive);
        destructive.Transport
            .Returns("clientdbinfo", ToolHarness.Records(new Dictionary<string, string> { ["client_nickname"] = "Alice" }))
            .Returns("clientdbdelete", ToolHarness.Records());

        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(destructive.Executor).DeleteIdentityAsync(4, "Bob", 2, cancellationToken: Ct));
        Assert.DoesNotContain(destructive.Transport.SentCommands, command => command.Name == "clientdbdelete");

        var result = await new ClientAdminTools(destructive.Executor).DeleteIdentityAsync(4, "Alice", 2, cancellationToken: Ct);

        var sent = destructive.Transport.SentCommands.Single(command => command.Name == "clientdbdelete");
        Assert.Equal(("4", 2), (sent.Parameters!["cldbid"], sent.VirtualServerId));
        Assert.Equal("Deleted identity 4 'Alice'.", result.Done);
    }

    [Fact]
    public async Task Deleting_a_complaint_names_both_the_target_and_who_filed_it()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("complaindel", ToolHarness.Records());

        await new ModerationAdminTools(harness.Executor).DeleteComplaintAsync(3, 1, cancellationToken: Ct);

        var sent = Assert.Single(harness.Transport.SentCommands).Parameters!;
        Assert.Equal(("3", "1"), (sent["tcldbid"], sent["fcldbid"]));
    }

    [Fact]
    public async Task Lists_channel_groups_by_their_own_id_field()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("channelgrouplist", ToolHarness.Records(
            new Dictionary<string, string> { ["cgid"] = "5", ["name"] = "Channel Admin", ["type"] = "1", ["n_member_addp"] = "50" }));

        var group = Assert.Single((await new GroupTools(harness.Executor).ListChannelGroupsAsync(cancellationToken: Ct)).Groups);

        Assert.Equal((5, "Channel Admin", "regular", 50), (group.Id, group.Name, group.Type, group.NeededMemberAddPower));
    }
}