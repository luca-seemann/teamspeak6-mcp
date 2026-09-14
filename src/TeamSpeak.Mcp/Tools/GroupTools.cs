using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for server groups and channel groups.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class GroupTools(QueryExecutor executor)
{
    /// <summary>Lists the server groups.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The groups.</returns>
    [McpServerTool(Name = "ts_servergroup_list", Title = "List server groups",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the server groups of a virtual server with their id, name, kind (regular, " +
                 "template or query), icon, sort order and the powers needed to modify the group or " +
                 "add and remove its members.")]
    public async Task<GroupList> ListServerGroupsAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default) =>
        new(await ReadGroupsAsync("servergrouplist", "sgid", virtualServerId, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>Lists the channel groups.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The groups.</returns>
    [McpServerTool(Name = "ts_channelgroup_list", Title = "List channel groups",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the channel groups of a virtual server with their id, name, kind (regular, " +
                 "template or query), icon, sort order and the powers needed to modify the group or " +
                 "add and remove its members.")]
    public async Task<GroupList> ListChannelGroupsAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default) =>
        new(await ReadGroupsAsync("channelgrouplist", "cgid", virtualServerId, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>Lists the members of a server group.</summary>
    /// <param name="groupId">The server group.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The members.</returns>
    [McpServerTool(Name = "ts_servergroup_members", Title = "List server group members",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the client identities in one server group, online or not, with database id, " +
                 "last nickname and unique identity.")]
    public async Task<GroupMembers> ServerGroupMembersAsync(
        [Description("The server group id, as listed by ts_servergroup_list.")] int groupId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await Run(
            "servergroupclientlist",
            new Dictionary<string, string> { ["sgid"] = Text(groupId) },
            ["-names"],
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        return new GroupMembers(
            groupId,
            records
                .Select(record => new GroupMember(
                    record.GetInt32("cldbid"),
                    record.GetString("client_nickname"),
                    record.GetString("client_unique_identifier")))
                .Where(member => member.DatabaseId > 0)
                .ToList());
    }

    /// <summary>Lists channel group assignments.</summary>
    /// <param name="channelId">Only this channel.</param>
    /// <param name="databaseId">Only this client.</param>
    /// <param name="groupId">Only this channel group.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assignments.</returns>
    [McpServerTool(Name = "ts_channelgroup_members", Title = "List channel group assignments",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists which client holds which channel group in which channel. Filter by any " +
                 "combination of channelId, databaseId and groupId. Clients who only have the default " +
                 "channel group are not stored as assignments and do not appear.")]
    public async Task<ChannelGroupAssignments> ChannelGroupMembersAsync(
        [Description("Only assignments in this channel.")] int? channelId = null,
        [Description("Only assignments of this client database id.")] int? databaseId = null,
        [Description("Only assignments of this channel group.")] int? groupId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>();
        if (channelId is { } cid)
        {
            parameters["cid"] = Text(cid);
        }

        if (databaseId is { } cldbid)
        {
            parameters["cldbid"] = Text(cldbid);
        }

        if (groupId is { } cgid)
        {
            parameters["cgid"] = Text(cgid);
        }

        var records = await Run("channelgroupclientlist", parameters, null, virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);

        return new ChannelGroupAssignments(records
            .Select(record => new ChannelGroupAssignment(record.GetInt32("cid"), record.GetInt32("cldbid"), record.GetInt32("cgid")))
            .ToList());
    }

    /// <summary>Lists every group a client identity belongs to.</summary>
    /// <param name="databaseId">The client.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its server groups and channel groups.</returns>
    [McpServerTool(Name = "ts_client_groups", Title = "Show a client's groups",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows every group one client identity belongs to, online or not: its server groups " +
                 "by id and name, and the channel groups it holds in particular channels.")]
    public async Task<ClientGroupMemberships> ClientGroupsAsync(
        [Description("The client's database id.")] int databaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var serverGroups = await Run(
            "servergroupsbyclientid",
            new Dictionary<string, string> { ["cldbid"] = Text(databaseId) },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        var assignments = await Run(
            "channelgroupclientlist",
            new Dictionary<string, string> { ["cldbid"] = Text(databaseId) },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        var channelGroupNames = assignments.Count == 0
            ? new Dictionary<int, string>()
            : (await ReadGroupsAsync("channelgrouplist", "cgid", virtualServerId, profile, cancellationToken).ConfigureAwait(false))
                .ToDictionary(group => group.Id, group => group.Name);

        return new ClientGroupMemberships(
            databaseId,
            serverGroups
                .Select(record => new GroupReference(record.GetInt32("sgid"), record.GetString("name")))
                .ToList(),
            assignments
                .Select(record => new ChannelGroupMembership(
                    record.GetInt32("cid"),
                    record.GetInt32("cgid"),
                    channelGroupNames.GetValueOrDefault(record.GetInt32("cgid"), $"#{record.GetInt32("cgid")}")))
                .ToList());
    }

    /// <summary>Reads a server or channel group list.</summary>
    internal async Task<IReadOnlyList<GroupSummary>> ReadGroupsAsync(
        string command,
        string idField,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var records = await Run(command, null, null, virtualServerId, profile, cancellationToken).ConfigureAwait(false);

        return records
            .Select(record => new GroupSummary(
                record.GetInt32(idField),
                record.GetString("name"),
                record.GetInt32("type") switch
                {
                    0 => "template",
                    1 => "regular",
                    2 => "query",
                    var other => $"unknown ({other})",
                },
                record.GetInt32("iconid"),
                record.GetBoolean("savedb"),
                record.GetInt32("sortid"),
                record.GetInt32("n_modifyp"),
                record.GetInt32("n_member_addp"),
                record.GetInt32("n_member_removep")))
            .ToList();
    }

    private Task<IReadOnlyList<QueryRecord>> Run(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? options,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken) =>
        executor.RunAsync(
            command,
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(command, parameters, options, virtualServerId),
            cancellationToken);
}

/// <summary>Server or channel groups.</summary>
/// <param name="Groups">The groups.</param>
public sealed record GroupList(IReadOnlyList<GroupSummary> Groups);

/// <summary>A server or channel group.</summary>
/// <param name="Id">The group id.</param>
/// <param name="Name">The group name.</param>
/// <param name="Type"><c>regular</c>, <c>template</c> or <c>query</c>.</param>
/// <param name="IconId">The icon, or 0 for none.</param>
/// <param name="SavedInDatabase">Whether membership survives a disconnect.</param>
/// <param name="SortId">The display sort order.</param>
/// <param name="NeededModifyPower">The power needed to modify the group.</param>
/// <param name="NeededMemberAddPower">The power needed to add members.</param>
/// <param name="NeededMemberRemovePower">The power needed to remove members.</param>
public sealed record GroupSummary(
    int Id,
    string Name,
    string Type,
    int IconId,
    bool SavedInDatabase,
    int SortId,
    int NeededModifyPower,
    int NeededMemberAddPower,
    int NeededMemberRemovePower);

/// <summary>The members of a server group.</summary>
/// <param name="GroupId">The server group.</param>
/// <param name="Members">Its members.</param>
public sealed record GroupMembers(int GroupId, IReadOnlyList<GroupMember> Members);

/// <summary>A member of a group.</summary>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="Nickname">The last nickname.</param>
/// <param name="UniqueId">The unique identity.</param>
public sealed record GroupMember(int DatabaseId, string Nickname, string UniqueId);

/// <summary>Channel group assignments.</summary>
/// <param name="Assignments">One entry per client, channel and group.</param>
public sealed record ChannelGroupAssignments(IReadOnlyList<ChannelGroupAssignment> Assignments);

/// <summary>A client holding a channel group in a channel.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="ChannelGroupId">The channel group.</param>
public sealed record ChannelGroupAssignment(int ChannelId, int DatabaseId, int ChannelGroupId);

/// <summary>Every group a client belongs to.</summary>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="ServerGroups">Its server groups.</param>
/// <param name="ChannelGroups">The channel groups it holds, by channel.</param>
public sealed record ClientGroupMemberships(
    int DatabaseId,
    IReadOnlyList<GroupReference> ServerGroups,
    IReadOnlyList<ChannelGroupMembership> ChannelGroups);

/// <summary>A group by id and name.</summary>
/// <param name="Id">The group id.</param>
/// <param name="Name">The group name.</param>
public sealed record GroupReference(int Id, string Name);

/// <summary>A channel group held in a channel.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="ChannelGroupId">The channel group.</param>
/// <param name="ChannelGroupName">The channel group's name.</param>
public sealed record ChannelGroupMembership(int ChannelId, int ChannelGroupId, string ChannelGroupName);