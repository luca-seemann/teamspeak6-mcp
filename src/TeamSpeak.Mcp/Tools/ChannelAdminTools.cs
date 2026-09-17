using System.ComponentModel;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that change channels.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ChannelAdminTools(QueryExecutor executor)
{
    /// <summary>Creates a channel.</summary>
    [McpServerTool(Name = "ts_channel_create", Title = "Create a channel",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Creates a channel, at the top level or under a parent, and returns its id. Properties set " +
                 "anything else at creation, such as channel_topic, channel_password, channel_maxclients, or " +
                 "channel_flag_permanent=1; without a permanence flag TeamSpeak creates a temporary channel " +
                 "that disappears when it empties. Needs Write.")]
    public async Task<ActionResult> CreateAsync(
        [Description("The channel name.")] string name,
        [Description("The parent channel. Omit for the top level.")] int? parentId = null,
        [Description(ToolDescriptions.ChannelProperties)] IReadOnlyDictionary<string, string>? properties = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = Properties(properties, "channel_", required: false);
        parameters["channel_name"] = RequireText(name, nameof(name));

        if (parentId is { } parent)
        {
            parameters["cpid"] = Text(parent);
        }

        var records = await executor.RunCommandAsync(
            "ts_channel_create", profile, new QueryCommand("channelcreate", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Created channel '{parameters["channel_name"]}'.", records);
    }

    /// <summary>Changes a channel's properties.</summary>
    [McpServerTool(Name = "ts_channel_edit", Title = "Change a channel",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Changes one or more properties of a channel at once, such as its name, topic, " +
                 "description, password, slot limit, needed talk power or permanence, using the channel_* " +
                 "names ts_channel_info shows. Needs Write.")]
    public async Task<ActionResult> EditAsync(
        [Description("The channel to change.")] int channelId,
        [Description(ToolDescriptions.ChannelProperties)] IReadOnlyDictionary<string, string> properties,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = Properties(properties, "channel_", required: true);
        parameters["cid"] = Text(channelId);

        var records = await executor.RunCommandAsync(
            "ts_channel_edit", profile, new QueryCommand("channeledit", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Changed {string.Join(", ", parameters.Keys.Where(key => key != "cid"))} of channel {channelId}.", records);
    }

    /// <summary>Moves a channel.</summary>
    [McpServerTool(Name = "ts_channel_move", Title = "Move a channel",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Moves a channel, with its sub-channels, under a new parent, or to the top level with " +
                 "parentId 0, optionally placing it directly below a given sibling. Needs Write.")]
    public async Task<ActionResult> MoveAsync(
        [Description("The channel to move.")] int channelId,
        [Description("The new parent channel, or 0 for the top level.")] int parentId,
        [Description("The sibling to place it directly below. Omit or 0 to place it first under the parent.")] int? belowChannelId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        // The current parent is read first, so refuse before that read rather than after it.
        executor.Demand("ts_channel_move", SafetyLevel.Write, profile);

        var order = Text(belowChannelId ?? 0);
        var parent = parentId == 0 ? "the top level" : $"channel {parentId}";

        var info = await executor.RunAsync(
            "ts_channel_move",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("channelinfo", new Dictionary<string, string> { ["cid"] = Text(channelId) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        // channelmove refuses a channel's own parent with 770 "already member of channel", even when the
        // position should change, so a move within the same parent only sets the order.
        if (info.Count > 0 && info[0].GetInt32("pid") == parentId)
        {
            var edited = await executor.RunCommandAsync(
                "ts_channel_move",
                profile,
                new QueryCommand(
                    "channeledit",
                    new Dictionary<string, string> { ["cid"] = Text(channelId), ["channel_order"] = order },
                    VirtualServerId: virtualServerId),
                cancellationToken).ConfigureAwait(false);

            var place = belowChannelId is > 0 ? $"below channel {belowChannelId}" : "first";
            return ActionResult.From($"Channel {channelId} was already under {parent}; placed it {place}.", edited);
        }

        var records = await executor.RunCommandAsync(
            "ts_channel_move",
            profile,
            new QueryCommand(
                "channelmove",
                new Dictionary<string, string> { ["cid"] = Text(channelId), ["cpid"] = Text(parentId), ["order"] = order },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Moved channel {channelId} under {parent}.", records);
    }

    /// <summary>Deletes a channel.</summary>
    [McpServerTool(Name = "ts_channel_delete", Title = "Delete a channel",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes a channel with all its sub-channels and files. Without force it refuses while " +
                 "anyone is inside; with force the people inside are moved to the default channel. As a " +
                 "safeguard, confirmName must repeat the channel's current name, as ts_channel_info shows it. " +
                 "Needs Destructive.")]
    public async Task<ActionResult> DeleteAsync(
        [Description("The channel to delete.")] int channelId,
        [Description("The channel's current name, exactly as the server shows it.")] string confirmName,
        [Description("Delete even if clients are inside, moving them to the default channel.")] bool force = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var command = new QueryCommand(
            "channeldelete",
            new Dictionary<string, string> { ["cid"] = Text(channelId), ["force"] = force ? "1" : "0" },
            VirtualServerId: virtualServerId);
        var target = await new DeletionTargets(executor).ConfirmAsync("ts_channel_delete", profile, command, confirmName, cancellationToken).ConfigureAwait(false);

        var records = await executor.RunCommandAsync("ts_channel_delete", profile, command, cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Deleted channel {channelId} '{target!.Name}'.", records);
    }
}