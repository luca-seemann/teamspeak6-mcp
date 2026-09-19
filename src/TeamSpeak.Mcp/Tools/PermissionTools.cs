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
    /// <param name="search">Text to find in permission names.</param>
    /// <param name="limit">How many to return.</param>
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
                 "databaseId (a client's permissions in one channel). An admin group holds hundreds, so " +
                 "at most limit come back, by id; totalMatches says how many matched, and search narrows " +
                 "them by name. To see what a client ends up with from all of these combined, use " +
                 "ts_perm_effective.")]
    public async Task<AssignedPermissions> AssignedPermissionsAsync(
        [Description("A server group id.")] int? serverGroupId = null,
        [Description("A channel group id.")] int? channelGroupId = null,
        [Description("A channel id; with databaseId, the channel of a channel-client pair.")] int? channelId = null,
        [Description("A client database id; with channelId, the client of a channel-client pair.")] int? databaseId = null,
        [Description("Text to find in permission names, for example 'talk_power' or 'kick'.")] string? search = null,
        [Description("How many assignments to return, from 1 to 500.")] int limit = 100,
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

        var matches = records
            .Select(record => new PermissionValue(
                record.GetInt32("permid"),
                names!.NameOf(record.GetInt32("permid")),
                record.GetInt32("permvalue"),
                record.GetBoolean("permnegated"),
                record.GetBoolean("permskip")))
            .Where(permission => string.IsNullOrWhiteSpace(search) || permission.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(permission => permission.Id)
            .ToList();

        return new AssignedPermissions(target.Label, matches.Count, matches.Take(Math.Clamp(limit, 1, 500)).ToList());
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
                 "group names. Use it to answer 'who can kick people here?'." + ToolDescriptions.UserWrittenText)]
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

    /// <summary>Reports what this server's own query session is allowed to do.</summary>
    /// <param name="permissions">Permission names to check.</param>
    /// <param name="command">A ServerQuery command whose required permissions to check.</param>
    /// <param name="channelId">The channel to read the values in.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Who the session is, the groups it holds, and the value of each permission asked about.</returns>
    [McpServerTool(Name = "ts_perm_self", Title = "Show what this session may do",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows what this MCP server's own query session may do on a TeamSpeak server: which " +
                 "login it is, which server groups that login holds, and the value it has for the " +
                 "permissions asked about. Use it when a call came back as 'insufficient client " +
                 "permissions' (2568), or before trying something that might be refused. Name the " +
                 "permissions directly, or pass command to look up what a ServerQuery command needs, " +
                 "read from the server's own help; a permission the session does not hold at all comes " +
                 "back as granted false, and each entry carries a status in words. Looking up a command needs a " +
                 "profile that uses SSH, because " +
                 "the WebQuery serves no help. A guest profile has no account, so it holds no groups.")]
    public async Task<SessionPermissions> SelfPermissionsAsync(
        [Description("Permission names to check, for example 'b_virtualserver_stop'. See ts_perm_list.")]
        IReadOnlyList<string>? permissions = null,
        [Description("A ServerQuery command whose required permissions to check, for example 'serverstop'.")]
        string? command = null,
        [Description("Read the values as they are inside this channel, where a channel or channel group could change them.")]
        int? channelId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await executor.RunAsync(
            "ts_perm_self", SafetyLevel.ReadOnly, profile, new QueryCommand("whoami", VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        var session = identity.Count > 0 ? identity[0] : throw new McpException("The server returned no session.");
        var databaseId = session.GetInt32("client_database_id");

        var names = await RequestedPermissionsAsync(permissions, command, profile, cancellationToken).ConfigureAwait(false);
        var checks = new List<PermissionCheck>();

        foreach (var name in names)
        {
            checks.Add(await CheckAsync(name, channelId, virtualServerId, profile, cancellationToken).ConfigureAwait(false));
        }

        return new SessionPermissions(
            session.GetString("client_login_name"),
            databaseId,
            // What the values were read on, which is not what whoami reports: the session selects a
            // virtual server only when a command needs one, and whoami needs none.
            virtualServerId ?? executor.ResolveProfile(profile).DefaultVirtualServerId,
            channelId ?? session.GetInt32("client_channel_id"),
            // A guest has no account, so there is no membership to read: cldbid 0 is nobody.
            databaseId == 0 ? [] : await GroupsOfAsync(databaseId, virtualServerId, profile, cancellationToken).ConfigureAwait(false),
            checks);
    }

    /// <summary>Collects the permission names to check, from the caller and from a command's help.</summary>
    private async Task<IReadOnlyList<string>> RequestedPermissionsAsync(
        IReadOnlyList<string>? permissions,
        string? command,
        string? profile,
        CancellationToken cancellationToken)
    {
        var names = new List<string>(permissions?.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()) ?? []);

        if (!string.IsNullOrWhiteSpace(command))
        {
            var help = await new MetaTools(executor).CommandHelpAsync(command, profile, cancellationToken).ConfigureAwait(false);
            names.AddRange(PermissionsNamedIn(help.Text));
        }

        if (names.Count == 0)
        {
            throw new McpException(
                "Name at least one permission, or a command whose permissions to look up. " +
                "ts_perm_list finds permission names, ts_command_help shows what a command needs.");
        }

        // More than a handful is a catalog dump, which ts_perm_assigned does better and in one command.
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return distinct.Count <= 20
            ? distinct
            : throw new McpException(
                $"{distinct.Count} permissions is too many for one call; check at most 20. " +
                "ts_perm_assigned lists everything a group or client holds in one command.");
    }

    /// <summary>Reads the permission names out of the Permissions section of a command's help page.</summary>
    /// <remarks>
    /// The page lists them one per line between the <c>Permissions:</c> and <c>Description:</c>
    /// headings, indented; a command that needs none has the heading with nothing under it.
    /// </remarks>
    private static IEnumerable<string> PermissionsNamedIn(string help)
    {
        var lines = help.Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim().Equals("Permissions:", StringComparison.Ordinal));

        if (start < 0)
        {
            yield break;
        }

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.EndsWith(':') || line.Equals("Usage", StringComparison.Ordinal))
            {
                yield break;
            }

            if (line.Length > 0 && line.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                yield return line;
            }
        }
    }

    /// <summary>Asks the server for the session's own value of one permission.</summary>
    /// <remarks>
    /// The server validates the name itself, answering <c>2562</c> for one it does not know, so this
    /// needs no permission catalog, which matters because a session that may not read the catalog
    /// can still be asked about itself. Every outcome is reported rather than thrown: measured on
    /// 6.0.0-beta13, a guest session is refused <c>permget</c> entirely with <c>2568</c>, and "you may
    /// not even ask" is the answer to the question, not a reason to abandon the other permissions.
    /// </remarks>
    private async Task<PermissionCheck> CheckAsync(
        string permission,
        int? channelId,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var name = RequireText(permission, nameof(permission));

        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new McpException($"'{name}' is not a permission name. See ts_perm_list for the exact names.");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["permsid"] = name };

        if (channelId is { } channel)
        {
            parameters["cid"] = Text(channel);
        }

        var response = await executor.RunForStatusAsync(
            "ts_perm_self",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("permget", parameters, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        if (response.Error.Id == QueryErrorCode.InvalidPermissionId)
        {
            return new PermissionCheck(name, null, false, "the server has no permission by that name");
        }

        if (response.Error.Id == QueryErrorCode.InsufficientPermissions)
        {
            return new PermissionCheck(name, null, false, "this session may not read its own permissions");
        }

        if (!response.Error.IsSuccess && !response.Error.IsEmptyResult)
        {
            return new PermissionCheck(name, null, false, $"the server refused the question: {response.Error.Message}");
        }

        // A permission the session holds nowhere answers with an empty result set rather than a value.
        if (response.Error.IsEmptyResult || response.Records.Count == 0)
        {
            return new PermissionCheck(name, null, false, "this session holds it nowhere");
        }

        var value = response.Records[0].GetInt32("permvalue");
        return new PermissionCheck(name, value, value != 0, value != 0 ? "granted" : "held, but zero");
    }

    /// <summary>Reads the server groups a client database id belongs to.</summary>
    private async Task<IReadOnlyList<string>> GroupsOfAsync(
        int databaseId,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var records = await executor.RunSearchAsync(
            "ts_perm_self",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("servergroupsbyclientid", new Dictionary<string, string> { ["cldbid"] = Text(databaseId) }, VirtualServerId: virtualServerId),
            QueryErrorCode.EmptyResultSet,
            cancellationToken).ConfigureAwait(false);

        return records.Select(record => record.GetString("name")).Where(name => name.Length > 0).ToList();
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
/// <param name="TotalMatches">How many assignments matched; more than returned when the limit cut them off.</param>
/// <param name="Permissions">The first matching assignments, by id.</param>
public sealed record AssignedPermissions(string Target, int TotalMatches, IReadOnlyList<PermissionValue> Permissions);

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
/// <summary>What this server's own query session is and may do.</summary>
/// <param name="Login">The query login name, empty for a guest session.</param>
/// <param name="DatabaseId">The client database id behind the login, 0 for a guest.</param>
/// <param name="VirtualServerId">The virtual server the permissions were read on.</param>
/// <param name="ChannelId">The channel they were read in: the one asked for, or the one the session sits in, 0 for none.</param>
/// <param name="ServerGroups">The server groups the login holds; empty for a guest, which has no account.</param>
/// <param name="Permissions">One entry per permission asked about.</param>
public sealed record SessionPermissions(
    string Login,
    int DatabaseId,
    int VirtualServerId,
    int ChannelId,
    IReadOnlyList<string> ServerGroups,
    IReadOnlyList<PermissionCheck> Permissions);

/// <summary>The session's own value for one permission.</summary>
/// <param name="Name">The permission name.</param>
/// <param name="Value">The value the server reports, or <see langword="null"/> when the session holds it nowhere.</param>
/// <param name="Granted">Whether the value allows the action: any non-zero value.</param>
/// <param name="Status">Why it came out that way, in words, for the cases where no value was read.</param>
public sealed record PermissionCheck(string Name, int? Value, bool Granted, string Status);