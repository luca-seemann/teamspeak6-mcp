using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>Reads the current name of what a test deletes, as the confirmation needs it.</summary>
/// <remarks>Read right before deleting, since several tests rename what they created.</remarks>
internal static class LiveNames
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<string> ChannelAsync(QueryExecutor executor, int channelId) =>
        channelId == 0
            ? await VirtualServerAsync(executor)
            : (await new ChannelTools(executor).ChannelInfoAsync(channelId, 1, cancellationToken: Ct)).Fields["channel_name"];

    public static async Task<string> ServerGroupAsync(QueryExecutor executor, int groupId) =>
        (await new GroupTools(executor).ListServerGroupsAsync(1, cancellationToken: Ct)).Groups.Single(group => group.Id == groupId).Name;

    public static async Task<string> ChannelGroupAsync(QueryExecutor executor, int groupId) =>
        (await new GroupTools(executor).ListChannelGroupsAsync(1, cancellationToken: Ct)).Groups.Single(group => group.Id == groupId).Name;

    public static async Task<string> VirtualServerAsync(QueryExecutor executor) =>
        (await new VirtualServerTools(executor).VirtualServerInfoAsync(1, cancellationToken: Ct)).Fields["virtualserver_name"];
}