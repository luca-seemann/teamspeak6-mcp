using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for reading permissions.</summary>
/// <param name="executor">The shared path to the server.</param>
/// <param name="permissionNames">The per-profile permission name cache.</param>
[McpServerToolType]
public sealed partial class PermissionTools(QueryExecutor executor, PermissionNameCache permissionNames)
{
    /// <summary>Searches the permissions a server knows.</summary>
    /// <param name="search">Text to find in names and descriptions.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching permissions.</returns>
    [McpServerTool(Name = "ts_perm_list", Title = "Search permissions",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Searches the permissions a TeamSpeak server knows by name or description, for " +
                 "example 'talk' or 'kick', returning id, name and description. Use it to find the " +
                 "exact name other permission tools need. Without search it returns the first " +
                 "permissions by id; totalMatches says how many matched.")]
    public async Task<PermissionCatalog> ListPermissionsAsync(
        [Description("Text to find in permission names and descriptions, for example 'talk_power'.")]
        string? search = null,
        [Description("How many permissions to return, from 1 to 200.")] int limit = 50,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var names = await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false);

        var matches = string.IsNullOrWhiteSpace(search)
            ? names.All
            : names.All
                .Where(permission =>
                    permission.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                    || permission.Description.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        return new PermissionCatalog(matches.Count, matches.Take(Math.Clamp(limit, 1, 200)).ToList());
    }

    /// <summary>Lists the permissions assigned directly to one target.</summary>
    /// <param name="serverGroupId">A server group.</param>
    /// <param name="channelGroupId">A channel group.</param>
    /// <param name="channelId">A channel, or with databaseId the channel of a channel-client pair.</param>
    /// <param name="databaseId">A client, or with channelId the client of a channel-client pair.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assigned permissions.</returns>
    [McpServerTool(Name = "ts_perm_assigned", Title = "List assigned permissions",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the permissions assigned directly to one target, with name, value and the " +
                 "negate and skip flags. Pass exactly one target: serverGroupId, channelGroupId, " +
                 "channelId alone (channel permissions, mostly the powers needed to join or talk), " +
                 "databaseId alone (permissions on a client identity), or channelId together with " +
                 "databaseId (a client's permissions in one channel). To see what a client ends up " +
                 "with from all of these combined, use ts_perm_effective.")]
    public async Task<AssignedPermissions> AssignedPermissionsAsync(
        [Description("A server group id.")] int? serverGroupId = null,
        [Description("A channel group id.")] int? channelGroupId = null,
        [Description("A channel id; with databaseId, the channel of a channel-client pair.")] int? channelId = null,
        [Description("A client database id; with channelId, the client of a channel-client pair.")] int? databaseId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var target = PermissionTarget.From(serverGroupId, channelGroupId, channelId, databaseId);

        var records = await executor.RunAsync(
            "ts_perm_assigned",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(target.ListCommand, target.Parameters, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var names = records.Count == 0
            ? null
            : await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false);

        return new AssignedPermissions(
            target.Label,
            records
                .Select(record => new PermissionValue(
                    record.GetInt32("permid"),
                    names!.NameOf(record.GetInt32("permid")),
                    record.GetInt32("permvalue"),
                    record.GetBoolean("permnegated"),
                    record.GetBoolean("permskip")))
                .ToList());
    }

    /// <summary>Finds everything that holds a permission.</summary>
    /// <param name="permission">The permission name.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where the permission is assigned.</returns>
    [McpServerTool(Name = "ts_perm_find", Title = "Find where a permission is assigned",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Finds every place one permission is assigned on a virtual server: which server " +
                 "groups, channel groups, channels, clients and client-in-channel pairs carry it, with " +
                 "group names. Use it to answer 'who can kick people here?'.")]
    public async Task<PermissionHolders> FindPermissionAsync(
        [Description("The permission name, for example 'i_client_kick_from_server_power'. See ts_perm_list.")]
        string permission,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await RequirePermissionAsync(permission, profile, cancellationToken).ConfigureAwait(false);

        var records = await executor.RunAsync(
            "ts_perm_find",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("permfind", new Dictionary<string, string> { ["permid"] = Text(definition.Id) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var sources = records.Select(PermissionSource.From).ToList();
        var groupNames = await GroupNamesAsync(sources, virtualServerId, profile, cancellationToken).ConfigureAwait(false);

        return new PermissionHolders(definition.Name, sources.Select(source => source.Describe(groupNames)).ToList());
    }

    /// <summary>Resolves a permission name, refusing one the server does not know.</summary>
    private async Task<PermissionDefinition> RequirePermissionAsync(string permission, string? profile, CancellationToken cancellationToken)
    {
        var name = RequireText(permission, nameof(permission));
        var names = await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false);

        return names.TryFind(name, out var definition)
            ? definition
            : throw new McpException($"The server has no permission named '{name}'. Use ts_perm_list to find the exact name.");
    }

    /// <summary>Fetches group names, but only for the kinds of group the sources mention.</summary>
    private async Task<GroupNames> GroupNamesAsync(
        IReadOnlyCollection<PermissionSource> sources,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var groups = new GroupTools(executor);

        var serverGroups = sources.Any(source => source.Kind == PermissionSourceKind.ServerGroup)
            ? (await groups.ReadGroupsAsync("servergrouplist", "sgid", virtualServerId, profile, cancellationToken).ConfigureAwait(false))
                .ToDictionary(group => group.Id, group => group.Name)
            : [];

        var channelGroups = sources.Any(source => source.Kind == PermissionSourceKind.ChannelGroup)
            ? (await groups.ReadGroupsAsync("channelgrouplist", "cgid", virtualServerId, profile, cancellationToken).ConfigureAwait(false))
                .ToDictionary(group => group.Id, group => group.Name)
            : [];

        return new GroupNames(serverGroups, channelGroups);
    }
}

/// <summary>Permissions found by a search.</summary>
/// <param name="TotalMatches">How many permissions matched.</param>
/// <param name="Permissions">The first matches, by id.</param>
public sealed record PermissionCatalog(int TotalMatches, IReadOnlyList<PermissionDefinition> Permissions);

/// <summary>The permissions assigned directly to one target.</summary>
/// <param name="Target">What they are assigned to, for example <c>server group 6</c>.</param>
/// <param name="Permissions">The assignments.</param>
public sealed record AssignedPermissions(string Target, IReadOnlyList<PermissionValue> Permissions);

/// <summary>A permission assignment.</summary>
/// <param name="Id">The permission id.</param>
/// <param name="Name">The permission name.</param>
/// <param name="Value">The value granted.</param>
/// <param name="Negated">Whether the lowest rather than the highest server group value applies.</param>
/// <param name="Skip">Whether channel group and channel-client values are ignored for this permission.</param>
public sealed record PermissionValue(int Id, string Name, int Value, bool Negated, bool Skip);

/// <summary>Everything that holds one permission.</summary>
/// <param name="Permission">The permission name.</param>
/// <param name="Holders">Where it is assigned.</param>
public sealed record PermissionHolders(string Permission, IReadOnlyList<PermissionHolder> Holders);

/// <summary>One place a permission is assigned.</summary>
/// <param name="Kind"><c>server group</c>, <c>client</c>, <c>channel</c>, <c>channel group</c> or <c>channel client</c>.</param>
/// <param name="ServerGroupId">The server group, for that kind.</param>
/// <param name="ChannelGroupId">The channel group, for that kind.</param>
/// <param name="ChannelId">The channel, where one applies; 0 for a channel group assigned in no particular channel.</param>
/// <param name="DatabaseId">The client, where one applies.</param>
/// <param name="GroupName">The group's name, for group kinds.</param>
public sealed record PermissionHolder(
    string Kind,
    int? ServerGroupId,
    int? ChannelGroupId,
    int? ChannelId,
    int? DatabaseId,
    string? GroupName);

/// <summary>Group names by id, for describing permission sources.</summary>
internal sealed record GroupNames(IReadOnlyDictionary<int, string> ServerGroups, IReadOnlyDictionary<int, string> ChannelGroups);

/// <summary>What kind of thing a permission row comes from.</summary>
internal enum PermissionSourceKind
{
    ServerGroup = 0,
    Client = 1,
    Channel = 2,
    ChannelGroup = 3,
    ChannelClient = 4,
}

/// <summary>
/// Decodes the <c>t id1 id2</c> triple that <c>permoverview</c> and <c>permfind</c> use to say
/// where a permission comes from.
/// </summary>
/// <param name="Kind">The kind of source.</param>
/// <param name="Id1">The first id.</param>
/// <param name="Id2">The second id.</param>
internal sealed record PermissionSource(PermissionSourceKind Kind, int Id1, int Id2)
{
    public static PermissionSource From(QueryRecord record) =>
        new((PermissionSourceKind)record.GetInt32("t", -1), record.GetInt32("id1"), record.GetInt32("id2"));

    public int? ServerGroupId => Kind == PermissionSourceKind.ServerGroup ? Id1 : null;

    public int? ChannelGroupId => Kind == PermissionSourceKind.ChannelGroup ? Id2 : null;

    public int? ChannelId => Kind switch
    {
        PermissionSourceKind.Channel or PermissionSourceKind.ChannelGroup or PermissionSourceKind.ChannelClient => Id1,
        _ => null,
    };

    public int? DatabaseId => Kind switch
    {
        PermissionSourceKind.Client => Id1,
        PermissionSourceKind.ChannelClient => Id2,
        _ => null,
    };

    public string KindName => Kind switch
    {
        PermissionSourceKind.ServerGroup => "server group",
        PermissionSourceKind.Client => "client",
        PermissionSourceKind.Channel => "channel",
        PermissionSourceKind.ChannelGroup => "channel group",
        PermissionSourceKind.ChannelClient => "channel client",
        _ => $"unknown ({(int)Kind})",
    };

    public string? GroupName(GroupNames names) => Kind switch
    {
        PermissionSourceKind.ServerGroup => names.ServerGroups.GetValueOrDefault(Id1, $"#{Id1}"),
        PermissionSourceKind.ChannelGroup => names.ChannelGroups.GetValueOrDefault(Id2, $"#{Id2}"),
        _ => null,
    };

    public PermissionHolder Describe(GroupNames names) =>
        new(KindName, ServerGroupId, ChannelGroupId, ChannelId, DatabaseId, GroupName(names));
}