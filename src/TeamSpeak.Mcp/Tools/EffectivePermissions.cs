using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>The "why can or can't this client do X?" tool.</summary>
public sealed partial class PermissionTools
{
    /// <summary>How many permissions one call explains in full.</summary>
    private const int MaxExplained = 50;

    /// <summary>The resolution order, stated with every answer so the model can reason from it.</summary>
    private const string Rules =
        "Layers are applied in order, each later one replacing the value so far: server groups " +
        "(the highest value among them, or the lowest among those with the negate flag), then the " +
        "client's own permission, then its channel group in the channel, then its permission in that " +
        "channel. A skip flag on a server group or on the client's own permission keeps the two " +
        "channel layers out, and a client holding b_client_skip_channelgroup_permissions ignores its " +
        "channel group. Channel permissions are requirements set on the channel, not grants. " +
        "This is TeamSpeak's documented order; it was not independently verified against every " +
        "TeamSpeak 6 client behaviour.";

    /// <summary>Explains a client's effective permissions in a channel.</summary>
    /// <param name="databaseId">The client.</param>
    /// <param name="permission">An exact permission name.</param>
    /// <param name="search">Text to find in permission names.</param>
    /// <param name="channelId">The channel.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resolved permissions, each with every contribution.</returns>
    [McpServerTool(Name = "ts_perm_effective", Title = "Explain a client's effective permissions",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Answers 'why can or can't this client do X?'. For one client identity in one channel, " +
                 "shows the value the client ends up with for a permission and every assignment that fed " +
                 "into it: each server group, the client itself, its channel group in that channel and " +
                 "any client-in-channel permission, marking which one decided and whether a skip flag or " +
                 "b_client_skip_channelgroup_permissions kept channel values out. Pass permission for an exact name, or search for text in " +
                 "names such as 'kick'. Without channelId it uses the channel the client is in, or the " +
                 "default channel when the client is offline. Channel permissions such as the power " +
                 "needed to join are listed as requirements, not grants.")]
    public async Task<EffectivePermissions> EffectivePermissionsAsync(
        [Description("The client's database id; see ts_client_resolve.")] int databaseId,
        [Description("An exact permission name, for example 'i_client_talk_power'.")] string? permission = null,
        [Description("Text to find in permission names instead, for example 'kick'.")] string? search = null,
        [Description("The channel to evaluate in. Defaults to the client's current channel, or the default channel when offline.")]
        int? channelId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(permission) == string.IsNullOrWhiteSpace(search))
        {
            throw new McpException("Pass either permission, an exact name, or search, text to find in names, but not both.");
        }

        var names = await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false);
        var definition = string.IsNullOrWhiteSpace(permission)
            ? null
            : await RequirePermissionAsync(permission, profile, cancellationToken).ConfigureAwait(false);

        var (channel, channelChoice) = await ChannelForAsync(databaseId, channelId, virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);

        var records = await executor.RunAsync(
            "ts_perm_effective",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "permoverview",
                new Dictionary<string, string>
                {
                    ["cldbid"] = Text(databaseId),
                    ["cid"] = Text(channel),
                    ["permid"] = definition is null ? "0" : Text(definition.Id),
                },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var rows = records.Select(PermissionRow.From).ToList();
        var skipsChannelGroups = await SkipsChannelGroupsAsync(databaseId, channel, definition, rows, names, virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            var text = search!.Trim();
            rows = rows.Where(row => names.NameOf(row.PermissionId).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var groups = await GroupNamesAsync(rows.Select(row => row.Source).ToList(), virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);

        var resolved = rows
            .GroupBy(row => row.PermissionId)
            .OrderBy(group => names.NameOf(group.Key), StringComparer.Ordinal)
            .Select(group => Resolve(group.Key, group.ToList(), names, groups, skipsChannelGroups))
            .ToList();

        // An exact permission nothing assigns is still an answer: the client simply does not have it.
        if (definition is not null && resolved.Count == 0)
        {
            resolved.Add(new PermissionResolution(definition.Id, definition.Name, null, "nothing assigns this permission to the client", false, []));
        }

        return new EffectivePermissions(
            databaseId,
            channel,
            channelChoice,
            resolved.Count,
            resolved.Take(MaxExplained).ToList(),
            Rules);
    }

    /// <summary>The permission that makes a client ignore its channel group's values.</summary>
    private const string SkipChannelGroupPermission = "b_client_skip_channelgroup_permissions";

    /// <summary>
    /// Whether the client holds <see cref="SkipChannelGroupPermission"/>, from its server groups and its
    /// own permission.
    /// </summary>
    /// <remarks>
    /// Server Admin has it by default. Measured live: an admin with 75 talk power from Server Admin and
    /// a channel group granting 62 keeps 75.
    /// </remarks>
    private async Task<bool> SkipsChannelGroupsAsync(
        int databaseId,
        int channel,
        PermissionDefinition? asked,
        List<PermissionRow> rows,
        PermissionNames names,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        if (!names.TryFind(SkipChannelGroupPermission, out var skipPermission))
        {
            return false;
        }

        // An overview of every permission, or of this one, already holds the rows; otherwise ask for them.
        var skipRows = asked is null || asked.Id == skipPermission.Id
            ? rows
            : (await executor.RunAsync(
                "ts_perm_effective",
                SafetyLevel.ReadOnly,
                profile,
                new QueryCommand(
                    "permoverview",
                    new Dictionary<string, string>
                    {
                        ["cldbid"] = Text(databaseId),
                        ["cid"] = Text(channel),
                        ["permid"] = Text(skipPermission.Id),
                    },
                    VirtualServerId: virtualServerId),
                cancellationToken).ConfigureAwait(false)).Select(PermissionRow.From).ToList();

        var held = HeldValue(skipRows.Where(row => row.PermissionId == skipPermission.Id).ToList());
        return held is > 0;
    }

    /// <summary>The value from server groups and the client's own permission, before any channel layer.</summary>
    private static int? HeldValue(List<PermissionRow> rows)
    {
        int? value = null;

        var serverGroups = rows.Where(row => row.Source.Kind == PermissionSourceKind.ServerGroup).ToList();
        if (serverGroups.Count > 0)
        {
            var negated = serverGroups.Where(row => row.Negated).ToList();
            value = (negated.Count > 0 ? negated.MinBy(row => row.Value) : serverGroups.MaxBy(row => row.Value))!.Value;
        }

        return rows.FirstOrDefault(row => row.Source.Kind == PermissionSourceKind.Client)?.Value ?? value;
    }

    private static PermissionResolution Resolve(int id, List<PermissionRow> rows, PermissionNames names, GroupNames groups, bool skipsChannelGroups)
    {
        PermissionRow? decided = null;

        var serverGroups = rows.Where(row => row.Source.Kind == PermissionSourceKind.ServerGroup).ToList();
        if (serverGroups.Count > 0)
        {
            var negated = serverGroups.Where(row => row.Negated).ToList();
            decided = negated.Count > 0 ? negated.MinBy(row => row.Value) : serverGroups.MaxBy(row => row.Value);
        }

        var skip = serverGroups.Any(row => row.Skip);

        if (rows.FirstOrDefault(row => row.Source.Kind == PermissionSourceKind.Client) is { } client)
        {
            decided = client;
            skip |= client.Skip;
        }

        if (!skip)
        {
            if (!skipsChannelGroups)
            {
                decided = rows.FirstOrDefault(row => row.Source.Kind == PermissionSourceKind.ChannelGroup) ?? decided;
            }

            decided = rows.FirstOrDefault(row => row.Source.Kind == PermissionSourceKind.ChannelClient) ?? decided;
        }

        var contributions = rows
            .OrderBy(row => (int)row.Source.Kind)
            .Select(row => new PermissionContribution(
                row.Source.KindName,
                DescribeSource(row.Source, groups),
                row.Value,
                row.Negated,
                row.Skip,
                ReferenceEquals(row, decided),
                row.Source.Kind switch
                {
                    PermissionSourceKind.Channel =>
                        "Set on the channel itself: a requirement others must meet, not something the client holds.",
                    PermissionSourceKind.ChannelGroup or PermissionSourceKind.ChannelClient when skip =>
                        "Ignored: a skip flag on a server group or on the client's own permission keeps channel values out.",
                    PermissionSourceKind.ChannelGroup when skipsChannelGroups =>
                        $"Ignored: the client holds {SkipChannelGroupPermission}, so channel group values do not count.",
                    _ => null,
                }))
            .ToList();

        return new PermissionResolution(
            id,
            names.NameOf(id),
            decided?.Value,
            decided is null ? "nothing grants this permission to the client" : DescribeSource(decided.Source, groups),
            skip,
            contributions);
    }

    private static string DescribeSource(PermissionSource source, GroupNames groups) => source.Kind switch
    {
        PermissionSourceKind.ServerGroup => $"server group '{source.GroupName(groups)}' ({source.Id1})",
        PermissionSourceKind.Client => "the client's own permission",
        PermissionSourceKind.Channel => $"channel {source.Id1}",
        PermissionSourceKind.ChannelGroup => $"channel group '{source.GroupName(groups)}' ({source.Id2}) in channel {source.Id1}",
        PermissionSourceKind.ChannelClient => $"the client's permission in channel {source.Id1}",
        _ => source.KindName,
    };

    /// <summary>Picks the channel to evaluate in when the caller did not name one.</summary>
    private async Task<(int ChannelId, string Choice)> ChannelForAsync(
        int databaseId,
        int? channelId,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        if (channelId is { } given)
        {
            return (given, "the channel that was asked about");
        }

        async Task<IReadOnlyList<QueryRecord>> Run(string command, IReadOnlyDictionary<string, string>? parameters, IReadOnlyList<string>? options = null) =>
            await executor.RunAsync(
                "ts_perm_effective",
                SafetyLevel.ReadOnly,
                profile,
                new QueryCommand(command, parameters, options, virtualServerId),
                cancellationToken).ConfigureAwait(false);

        var named = await Run("clientgetnamefromdbid", new Dictionary<string, string> { ["cldbid"] = Text(databaseId) }).ConfigureAwait(false);
        if (named.Count == 0 || Optional(named[0], "cluid") is not { } uniqueId)
        {
            throw new McpException($"No client with database id {databaseId} is known to this virtual server.");
        }

        var sessions = await Run("clientgetids", new Dictionary<string, string> { ["cluid"] = uniqueId }).ConfigureAwait(false);
        if (sessions.Count > 0)
        {
            var info = await Run("clientinfo", new Dictionary<string, string> { ["clid"] = Text(sessions[0].GetInt32("clid")) }).ConfigureAwait(false);
            if (info.Count > 0 && info[0].GetInt32("cid") > 0)
            {
                return (info[0].GetInt32("cid"), "the channel the client is in");
            }
        }

        var channels = await Run("channellist", null, ["-flags"]).ConfigureAwait(false);
        var fallback = channels.FirstOrDefault(channel => channel.GetBoolean("channel_flag_default"));

        return fallback is null
            ? throw new McpException("The client is offline and the virtual server has no default channel; pass channelId.")
            : (fallback.GetInt32("cid"), "the default channel, because the client is offline");
    }
}

/// <summary>One row of <c>permoverview</c>: where a value comes from, and the value.</summary>
/// <param name="Source">Where the value is assigned.</param>
/// <param name="PermissionId">The permission.</param>
/// <param name="Value">The value.</param>
/// <param name="Negated">The negate flag.</param>
/// <param name="Skip">The skip flag.</param>
internal sealed record PermissionRow(PermissionSource Source, int PermissionId, int Value, bool Negated, bool Skip)
{
    public static PermissionRow From(QueryRecord record) =>
        new(PermissionSource.From(record), record.GetInt32("p"), record.GetInt32("v"), record.GetBoolean("n"), record.GetBoolean("s"));
}

/// <summary>A client's effective permissions in a channel.</summary>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="ChannelId">The channel they were evaluated in.</param>
/// <param name="ChannelChoice">Why that channel was used.</param>
/// <param name="TotalMatches">How many permissions matched.</param>
/// <param name="Permissions">The first matches, each explained.</param>
/// <param name="Rules">How the values were resolved.</param>
public sealed record EffectivePermissions(
    int DatabaseId,
    int ChannelId,
    string ChannelChoice,
    int TotalMatches,
    IReadOnlyList<PermissionResolution> Permissions,
    string Rules);

/// <summary>One permission, resolved.</summary>
/// <param name="Id">The permission id.</param>
/// <param name="Name">The permission name.</param>
/// <param name="Value">The value the client ends up with; absent when nothing grants it.</param>
/// <param name="DecidedBy">Which assignment decided the value.</param>
/// <param name="SkipApplied">Whether a skip flag kept the channel layers out.</param>
/// <param name="Contributions">Every assignment that was considered.</param>
public sealed record PermissionResolution(
    int Id,
    string Name,
    int? Value,
    string DecidedBy,
    bool SkipApplied,
    IReadOnlyList<PermissionContribution> Contributions);

/// <summary>An assignment that fed into an effective permission.</summary>
/// <param name="Kind"><c>server group</c>, <c>client</c>, <c>channel</c>, <c>channel group</c> or <c>channel client</c>.</param>
/// <param name="Source">The assignment in words, with names and ids.</param>
/// <param name="Value">Its value.</param>
/// <param name="Negated">The negate flag.</param>
/// <param name="Skip">The skip flag.</param>
/// <param name="Decided">Whether this assignment decided the effective value.</param>
/// <param name="Note">Why it did not count, where that needs saying.</param>
public sealed record PermissionContribution(
    string Kind,
    string Source,
    int Value,
    bool Negated,
    bool Skip,
    bool Decided,
    string? Note);