using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>Irreversible deletions name what they delete, through the dedicated tools and the raw tool alike.</summary>
public class ConfirmationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value);

    private static IEnumerable<string> Changes(ToolHarness harness) =>
        harness.Transport.SentCommands.Select(command => command.Name).Where(name => name is not ("channelinfo" or "servergrouplist" or "channelgrouplist" or "clientdbinfo" or "queryloginlist" or "apikeylist" or "whoami" or "serverinfo" or "serverlist" or "ftgetfileinfo"));

    [Fact]
    public async Task A_channel_is_deleted_only_under_its_current_name()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("channelinfo", ToolHarness.Records(Fields(("channel_name", "💬 Talk"))))
            .Returns("channeldelete", ToolHarness.Records());
        var tools = new ChannelAdminTools(harness.Executor);

        var wrong = await Assert.ThrowsAsync<McpException>(() => tools.DeleteAsync(7, "Talk", cancellationToken: Ct));
        Assert.Contains("'💬 Talk'", wrong.Message, StringComparison.Ordinal);
        Assert.Empty(Changes(harness));

        var done = await tools.DeleteAsync(7, "💬 Talk", cancellationToken: Ct);

        Assert.Equal(["channeldelete"], Changes(harness));
        Assert.Equal("Deleted channel 7 '💬 Talk'.", done.Done);
    }

    [Fact]
    public async Task A_profile_that_may_not_delete_reads_no_name_either()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);

        await Assert.ThrowsAsync<McpException>(() => new GroupAdminTools(harness.Executor).DeleteServerGroupAsync(9, "Veteran", cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Groups_are_confirmed_by_the_name_listed_for_their_id()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("servergrouplist", ToolHarness.Records(Fields(("sgid", "8"), ("name", "Guest")), Fields(("sgid", "9"), ("name", "Veteran"))))
            .Returns("channelgrouplist", ToolHarness.Records(Fields(("cgid", "5"), ("name", "Channel Admin"))))
            .Returns("servergroupdel", ToolHarness.Records())
            .Returns("channelgroupdel", ToolHarness.Records());
        var tools = new GroupAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.DeleteServerGroupAsync(9, "Guest", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => tools.DeleteServerGroupAsync(42, "Guest", cancellationToken: Ct));
        await tools.DeleteServerGroupAsync(9, "Veteran", cancellationToken: Ct);
        await tools.DeleteChannelGroupAsync(5, "Channel Admin", cancellationToken: Ct);

        Assert.Equal(["servergroupdel", "channelgroupdel"], Changes(harness));
    }

    [Fact]
    public async Task A_query_login_is_confirmed_by_its_login_name()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("queryloginlist", ToolHarness.Records(Fields(("cldbid", "12"), ("client_login_name", "musicbot"), ("sid", "1"))))
            .Returns("querylogindel", ToolHarness.Records());
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageQueryLoginAsync("delete", 12, cancellationToken: Ct));
        await tools.ManageQueryLoginAsync("delete", 12, confirmName: "musicbot", cancellationToken: Ct);

        Assert.Equal(["querylogindel"], Changes(harness));
    }

    [Fact]
    public async Task An_api_key_is_confirmed_by_its_owner_and_this_logins_own_keys_by_the_login_name()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("apikeylist", ToolHarness.Records(
                Fields(("id", "3"), ("cldbid", "1"), ("sid", "1"), ("scope", "manage")),
                Fields(("id", "4"), ("cldbid", "12"), ("sid", "1"), ("scope", "read"))))
            .Returns("whoami", ToolHarness.Records(Fields(("client_database_id", "1"), ("client_login_name", "serveradmin"))))
            .Returns("clientdbinfo", ToolHarness.Records(Fields(("client_nickname", "Alice"))))
            .Returns("apikeydel", ToolHarness.Records());
        var tools = new ModerationAdminTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => tools.ManageApiKeyAsync("delete", keyId: 3, confirmName: "Alice", cancellationToken: Ct));
        await tools.ManageApiKeyAsync("delete", keyId: 3, confirmName: "serveradmin", cancellationToken: Ct);
        await tools.ManageApiKeyAsync("delete", keyId: 4, confirmName: "Alice", cancellationToken: Ct);

        Assert.Equal(["apikeydel", "apikeydel"], Changes(harness));
    }

    [Fact]
    public async Task Overwriting_asks_for_the_file_name_only_when_a_file_is_there_to_replace()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("ftgetfileinfo", command => command.Parameters!["name"] == "/rules.txt"
            ? ToolHarness.Records(Fields(("size", "10")))
            : ToolHarness.Error(QueryErrorCode.FileNotFound, "file not found"));
        var tools = new FileAdminTools(harness.Executor, new FileTransferOptions());

        var refused = await Assert.ThrowsAsync<McpException>(() => tools.UploadAsync(1, "/rules.txt", content: "new", overwrite: true, cancellationToken: Ct));
        Assert.Contains("'rules.txt'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Changes(harness));

        // Past the confirmation, the upload goes on to ftinitupload, which this fake refuses.
        var past = await Assert.ThrowsAsync<McpException>(() => tools.UploadAsync(1, "/fresh.txt", content: "new", overwrite: true, cancellationToken: Ct));
        Assert.DoesNotContain("confirmName", past.Message, StringComparison.Ordinal);
        Assert.Contains("ftinitupload", Changes(harness));
    }

    [Fact]
    public async Task The_raw_tool_asks_for_the_same_names()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("channelinfo", ToolHarness.Records(Fields(("channel_name", "AFK"))))
            .Returns("serverlist", ToolHarness.Records(Fields(("virtualserver_id", "2"), ("virtualserver_name", "Staging"))))
            .Returns("channeldelete", ToolHarness.Records())
            .Returns("serverdelete", ToolHarness.Records());
        var meta = new MetaTools(harness.Executor);

        await Assert.ThrowsAsync<McpException>(() => meta.QueryRawAsync("channeldelete", new Dictionary<string, string> { ["cid"] = "96", ["force"] = "1" }, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => meta.QueryRawAsync("CHANNELDELETE", new Dictionary<string, string> { ["cid"] = "96" }, confirmName: "Lobby", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => meta.QueryRawAsync("channeldelete", confirmName: "AFK", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => meta.QueryRawAsync("serverdelete", new Dictionary<string, string> { ["sid"] = "2" }, cancellationToken: Ct));
        Assert.Empty(Changes(harness));

        await meta.QueryRawAsync("channeldelete", new Dictionary<string, string> { ["cid"] = "96" }, confirmName: "AFK", cancellationToken: Ct);
        await meta.QueryRawAsync("serverdelete", new Dictionary<string, string> { ["sid"] = "2" }, confirmName: "Staging", cancellationToken: Ct);

        Assert.Equal(["channeldelete", "serverdelete"], Changes(harness));
    }
}