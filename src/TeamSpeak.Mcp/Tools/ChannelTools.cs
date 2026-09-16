using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for channels.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ChannelTools(QueryExecutor executor)
{
    /// <summary>Lists the channels of a virtual server.</summary>
    /// <param name="tree">Whether to nest channels under their parents.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The channels in display order.</returns>
    [McpServerTool(Name = "ts_channel_list", Title = "List channels",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the channels of a virtual server in the order TeamSpeak clients display them, " +
                 "with topic, client counts, slot limit and whether each is the default, " +
                 "password-protected, permanent, semi-permanent or temporary. Set tree to true to nest " +
                 "sub-channels under their parents instead of a flat list with parent ids." + ToolDescriptions.UserWrittenText)]
    public async Task<ChannelList> ListChannelsAsync(
        [Description("Nest sub-channels under their parents. Defaults to a flat list in display order.")]
        bool tree = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_channel_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("channellist", Options: ["-topic", "-flags", "-limits"], VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new ChannelList(ChannelTree.Build(records, tree));
    }

    /// <summary>Shows every property of a channel.</summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All of its fields.</returns>
    [McpServerTool(Name = "ts_channel_info", Title = "Show a channel",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows every property of one channel: name, topic, description, codec, limits, " +
                 "needed talk power, flags and the rest of its configuration, by their ServerQuery " +
                 "field names." + ToolDescriptions.UserWrittenText)]
    public async Task<RecordResult> ChannelInfoAsync(
        [Description("The channel id, as listed by ts_channel_list.")] int channelId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_channel_info",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "channelinfo",
                new Dictionary<string, string> { ["cid"] = channelId.ToString(CultureInfo.InvariantCulture) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var fields = records.Count > 0
            ? new Dictionary<string, string>(QueryExecutor.ToFields(records[0]))
            : [];

        // channelinfo does not echo the id it was asked about.
        fields.TryAdd("cid", channelId.ToString(CultureInfo.InvariantCulture));
        return new RecordResult(fields);
    }

    /// <summary>Finds channels by name.</summary>
    /// <param name="pattern">Text to find in channel names.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching channels.</returns>
    [McpServerTool(Name = "ts_channel_find", Title = "Find channels by name",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Finds the channels whose name contains some text, ignoring case, and returns their " +
                 "ids and full names, or an empty list when no name matches." + ToolDescriptions.UserWrittenText)]
    public async Task<ChannelMatches> FindChannelsAsync(
        [Description("Text to find in channel names, for example 'afk'.")] string pattern,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunSearchAsync(
            "ts_channel_find",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "channelfind",
                new Dictionary<string, string> { ["pattern"] = ToolArguments.RequireText(pattern, nameof(pattern)) },
                VirtualServerId: virtualServerId),
            QueryErrorCode.InvalidChannelId,
            cancellationToken).ConfigureAwait(false);

        return new ChannelMatches(records
            .Select(record => new ChannelMatch(record.GetInt32("cid"), record.GetString("channel_name")))
            .ToList());
    }
}

/// <summary>Channels found by name.</summary>
/// <param name="Channels">The matches.</param>
public sealed record ChannelMatches(IReadOnlyList<ChannelMatch> Channels);

/// <summary>A channel found by name.</summary>
/// <param name="Id">The channel id.</param>
/// <param name="Name">The full channel name.</param>
public sealed record ChannelMatch(int Id, string Name);

/// <summary>The channels of a virtual server.</summary>
/// <param name="Channels">The channels in display order, nested when a tree was requested.</param>
public sealed record ChannelList(IReadOnlyList<ChannelNode> Channels);

/// <summary>A channel.</summary>
/// <param name="Id">The channel id.</param>
/// <param name="ParentId">The parent channel id, or 0 at the top level.</param>
/// <param name="Name">The channel name.</param>
/// <param name="Topic">The topic, when one is set.</param>
/// <param name="TotalClients">Clients in this channel.</param>
/// <param name="MaxClients">The slot limit, or -1 for unlimited.</param>
/// <param name="IsDefault">Whether new clients join this channel.</param>
/// <param name="HasPassword">Whether joining needs a password.</param>
/// <param name="Lifetime"><c>permanent</c>, <c>semi-permanent</c> or <c>temporary</c>.</param>
/// <param name="Children">Sub-channels in display order; present only in a tree.</param>
public sealed record ChannelNode(
    int Id,
    int ParentId,
    string Name,
    string? Topic,
    int TotalClients,
    int MaxClients,
    bool IsDefault,
    bool HasPassword,
    string Lifetime,
    IReadOnlyList<ChannelNode>? Children = null);

/// <summary>
/// Orders channels the way TeamSpeak clients display them.
/// </summary>
/// <remarks>
/// <c>channellist</c> returns channels in no useful order. Each channel's <c>channel_order</c> names
/// the sibling displayed directly above it, with 0 for the first, so the display order is a linked
/// list per parent that has to be walked.
/// </remarks>
public static class ChannelTree
{
    /// <summary>Builds the display order from <c>channellist</c> records.</summary>
    /// <param name="records">The records.</param>
    /// <param name="nested">Nest children under parents instead of flattening depth-first.</param>
    /// <returns>The channels.</returns>
    public static IReadOnlyList<ChannelNode> Build(IEnumerable<QueryRecord> records, bool nested)
    {
        ArgumentNullException.ThrowIfNull(records);

        var all = records.ToList();
        var ids = all.Select(record => record.GetInt32("cid")).ToHashSet();

        // A parent missing from the list, which should not happen, is treated as the top level
        // rather than losing its children.
        var byParent = all
            .GroupBy(record => ids.Contains(record.GetInt32("pid")) ? record.GetInt32("pid") : 0)
            .ToDictionary(group => group.Key, group => SortSiblings(group));

        var visited = new HashSet<int>();
        var result = Visit(0);

        // Anything unreachable, such as a parent cycle, is still shown rather than silently dropped.
        foreach (var record in all.Where(record => !visited.Contains(record.GetInt32("cid"))))
        {
            result.Add(ToNode(record));
        }

        return result;

        List<ChannelNode> Visit(int parentId)
        {
            var nodes = new List<ChannelNode>();
            if (!byParent.TryGetValue(parentId, out var siblings))
            {
                return nodes;
            }

            foreach (var record in siblings)
            {
                var id = record.GetInt32("cid");
                if (!visited.Add(id))
                {
                    continue;
                }

                var node = ToNode(record);
                var children = Visit(id);

                if (nested)
                {
                    nodes.Add(node with { Children = children.Count > 0 ? children : null });
                }
                else
                {
                    nodes.Add(node);
                    nodes.AddRange(children);
                }
            }

            return nodes;
        }
    }

    private static List<QueryRecord> SortSiblings(IEnumerable<QueryRecord> siblings)
    {
        var remaining = siblings.ToList();
        var sorted = new List<QueryRecord>(remaining.Count);
        var above = 0;

        while (remaining.Count > 0)
        {
            var next = remaining.Find(record => record.GetInt32("channel_order") == above);
            if (next is null)
            {
                // A broken chain: keep what is left in a stable order instead of losing it.
                sorted.AddRange(remaining.OrderBy(record => record.GetInt32("cid")));
                break;
            }

            sorted.Add(next);
            remaining.Remove(next);
            above = next.GetInt32("cid");
        }

        return sorted;
    }

    private static ChannelNode ToNode(QueryRecord record) =>
        new(
            record.GetInt32("cid"),
            record.GetInt32("pid"),
            record.GetString("channel_name"),
            record.GetString("channel_topic") is { Length: > 0 } topic ? topic : null,
            record.GetInt32("total_clients"),
            record.GetInt32("channel_maxclients", -1),
            record.GetBoolean("channel_flag_default"),
            record.GetBoolean("channel_flag_password"),
            record.GetBoolean("channel_flag_permanent") ? "permanent"
            : record.GetBoolean("channel_flag_semi_permanent") ? "semi-permanent"
            : "temporary");
}