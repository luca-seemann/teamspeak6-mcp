using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that change server groups, channel groups and their memberships.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class GroupAdminTools(QueryExecutor executor)
{
    /// <summary>Creates, copies or renames a server group.</summary>
    [McpServerTool(Name = "ts_servergroup_manage", Title = "Create, copy or rename a server group",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("create makes a new, empty server group. copy makes a new group with all the permissions " +
                 "of an existing one, the usual way to start a similar role. rename changes a group's name. " +
                 "Returns the new group's id for create and copy. Needs Write.")]
    public Task<ActionResult> ManageServerGroupAsync(
        [Description("create, copy, or rename.")] string action,
        [Description("The name of the new group, or the new name.")] string name,
        [Description("For copy: the group to copy. For rename: the group to rename.")] int? groupId = null,
        [Description("For create and copy: " + ToolDescriptions.GroupType)] string? type = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default) =>
        ManageAsync("ts_servergroup_manage", "server", "servergroupadd", "servergroupcopy", "servergrouprename", "sgid", "ssgid", "tsgid",
            action, name, groupId, type, virtualServerId, profile, cancellationToken);

    /// <summary>Creates, copies or renames a channel group.</summary>
    [McpServerTool(Name = "ts_channelgroup_manage", Title = "Create, copy or rename a channel group",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("create makes a new, empty channel group. copy makes a new group with all the permissions " +
                 "of an existing one. rename changes a group's name. Returns the new group's id for create " +
                 "and copy. Needs Write.")]
    public Task<ActionResult> ManageChannelGroupAsync(
        [Description("create, copy, or rename.")] string action,
        [Description("The name of the new group, or the new name.")] string name,
        [Description("For copy: the group to copy. For rename: the group to rename.")] int? groupId = null,
        [Description("For create and copy: " + ToolDescriptions.GroupType)] string? type = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default) =>
        ManageAsync("ts_channelgroup_manage", "channel", "channelgroupadd", "channelgroupcopy", "channelgrouprename", "cgid", "scgid", "tcgid",
            action, name, groupId, type, virtualServerId, profile, cancellationToken);

    /// <summary>Deletes a server group.</summary>
    [McpServerTool(Name = "ts_servergroup_delete", Title = "Delete a server group",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes a server group with its permissions. Without force it refuses while the group " +
                 "has members; with force the members simply lose it. Needs Destructive.")]
    public async Task<ActionResult> DeleteServerGroupAsync(
        [Description("The server group to delete.")] int groupId,
        [Description("Delete even though it has members.")] bool force = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_servergroup_delete",
            profile,
            new QueryCommand("servergroupdel", new Dictionary<string, string> { ["sgid"] = Text(groupId), ["force"] = force ? "1" : "0" }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Deleted server group {groupId}.", records);
    }

    /// <summary>Deletes a channel group.</summary>
    [McpServerTool(Name = "ts_channelgroup_delete", Title = "Delete a channel group",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes a channel group with its permissions. Without force it refuses while anyone holds " +
                 "it in a channel. Needs Destructive.")]
    public async Task<ActionResult> DeleteChannelGroupAsync(
        [Description("The channel group to delete.")] int groupId,
        [Description("Delete even though clients hold it.")] bool force = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_channelgroup_delete",
            profile,
            new QueryCommand("channelgroupdel", new Dictionary<string, string> { ["cgid"] = Text(groupId), ["force"] = force ? "1" : "0" }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Deleted channel group {groupId}.", records);
    }

    /// <summary>Adds a client identity to a server group or removes it.</summary>
    [McpServerTool(Name = "ts_servergroup_membership", Title = "Add to or remove from a server group",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Adds a client identity to a server group, or removes it, by database id, whether or not " +
                 "the person is online. Default groups and templates cannot be assigned. Needs Write.")]
    public async Task<ActionResult> ServerGroupMembershipAsync(
        [Description("add or remove.")] string action,
        [Description("The server group.")] int groupId,
        [Description("The client's database id; see ts_client_resolve.")] int databaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var add = Choice(action, nameof(action), "add", "remove") == "add";

        var records = await executor.RunCommandAsync(
            "ts_servergroup_membership",
            profile,
            new QueryCommand(
                add ? "servergroupaddclient" : "servergroupdelclient",
                new Dictionary<string, string> { ["sgid"] = Text(groupId), ["cldbid"] = Text(databaseId) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From(add ? $"Added identity {databaseId} to server group {groupId}." : $"Removed identity {databaseId} from server group {groupId}.", records);
    }

    /// <summary>Sets a client's channel group in a channel.</summary>
    [McpServerTool(Name = "ts_client_channelgroup_set", Title = "Set a client's channel group",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sets which channel group a client identity holds in one channel, such as making someone " +
                 "channel admin of their team's channel. Each identity holds exactly one channel group per " +
                 "channel, so this replaces the previous one; set the server's default channel group to " +
                 "take a role away. Needs Write.")]
    public async Task<ActionResult> SetChannelGroupAsync(
        [Description("The client's database id; see ts_client_resolve.")] int databaseId,
        [Description("The channel.")] int channelId,
        [Description("The channel group to hold there.")] int groupId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_client_channelgroup_set",
            profile,
            new QueryCommand(
                "setclientchannelgroup",
                new Dictionary<string, string> { ["cgid"] = Text(groupId), ["cid"] = Text(channelId), ["cldbid"] = Text(databaseId) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Identity {databaseId} now holds channel group {groupId} in channel {channelId}.", records);
    }

    private async Task<ActionResult> ManageAsync(
        string tool,
        string kind,
        string addCommand,
        string copyCommand,
        string renameCommand,
        string idKey,
        string sourceKey,
        string targetKey,
        string action,
        string name,
        int? groupId,
        string? type,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var choice = Choice(action, nameof(action), "create", "copy", "rename");
        var groupName = RequireText(name, nameof(name));
        var typeId = GroupTypeId(type);

        var (command, parameters) = choice switch
        {
            "create" => (addCommand, new Dictionary<string, string> { ["name"] = groupName, ["type"] = typeId }),
            "copy" => (copyCommand, new Dictionary<string, string>
            {
                [sourceKey] = Text(groupId ?? throw new McpException("copy needs groupId, the group to copy.")),
                // 0 creates a new group; overwriting an existing one is left to ts_query_raw on purpose.
                [targetKey] = "0",
                ["name"] = groupName,
                ["type"] = typeId,
            }),
            _ => (renameCommand, new Dictionary<string, string>
            {
                [idKey] = Text(groupId ?? throw new McpException("rename needs groupId, the group to rename.")),
                ["name"] = groupName,
            }),
        };

        var records = await executor.RunCommandAsync(tool, profile, new QueryCommand(command, parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From(
            choice switch
            {
                "create" => $"Created {kind} group '{groupName}'.",
                "copy" => $"Copied {kind} group {groupId} as '{groupName}'.",
                _ => $"Renamed {kind} group {groupId} to '{groupName}'.",
            },
            records);
    }

    private static string GroupTypeId(string? type) =>
        (type is null ? "regular" : Choice(type, nameof(type), "regular", "template", "query")) switch
        {
            "template" => "0",
            "query" => "2",
            _ => "1",
        };
}