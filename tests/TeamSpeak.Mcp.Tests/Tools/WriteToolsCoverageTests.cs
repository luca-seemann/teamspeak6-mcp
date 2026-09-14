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
        await Assert.ThrowsAsync<McpException>(() => new ClientAdminTools(write.Executor).DeleteIdentityAsync(4, cancellationToken: Ct));
        Assert.Empty(write.Transport.SentCommands);

        await using var destructive = new ToolHarness(SafetyLevel.Destructive);
        destructive.Transport.Returns("clientdbdelete", ToolHarness.Records());

        var result = await new ClientAdminTools(destructive.Executor).DeleteIdentityAsync(4, 2, cancellationToken: Ct);

        var sent = Assert.Single(destructive.Transport.SentCommands);
        Assert.Equal(("clientdbdelete", "4", 2), (sent.Name, sent.Parameters!["cldbid"], sent.VirtualServerId));
        Assert.Equal("Deleted identity 4.", result.Done);
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