using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that change permissions.</summary>
/// <param name="executor">The shared path to the server.</param>
/// <param name="permissionNames">The per-profile permission name cache.</param>
[McpServerToolType]
public sealed class PermissionAdminTools(QueryExecutor executor, PermissionNameCache permissionNames)
{
    /// <summary>Grants or revokes a permission on one target.</summary>
    [McpServerTool(Name = "ts_perm_set", Title = "Grant or revoke a permission",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Grants a permission with a value to one target, or revokes the assignment again. The " +
                 "target is a serverGroupId, a channelGroupId, a channelId alone (channel requirements such " +
                 "as needed join power), a databaseId alone (the identity itself), or channelId with " +
                 "databaseId (the identity in that channel). negated applies only to server groups; skip " +
                 "only to server groups and identities. Check the result with ts_perm_effective. Needs Write.")]
    public async Task<ActionResult> SetPermissionAsync(
        [Description("grant or revoke.")][AllowedValues("grant", "revoke")] string action,
        [Description("The permission name, for example 'i_client_talk_power'. See ts_perm_list.")] string permission,
        [Description("For grant: the value. Booleans use 1 and 0.")] int? value = null,
        [Description("For grant on a server group: use the lowest rather than highest value across groups.")] bool? negated = null,
        [Description("For grant on a server group or identity: keep channel group and channel values from overriding it.")] bool? skip = null,
        [Description("A server group id.")] int? serverGroupId = null,
        [Description("A channel group id.")] int? channelGroupId = null,
        [Description("A channel id; with databaseId, the channel of a channel-client pair.")] int? channelId = null,
        [Description("A client database id; with channelId, the client of a channel-client pair.")] int? databaseId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var grant = Choice(action, nameof(action), "grant", "revoke") == "grant";
        var target = PermissionTarget.From(serverGroupId, channelGroupId, channelId, databaseId);

        // The permission names are read first, so refuse before that read rather than after it.
        executor.Demand("ts_perm_set", SafetyLevel.Write, profile);

        if (negated is not null && (!grant || !target.SupportsNegate))
        {
            throw new McpException($"negated applies only when granting on a server group, not on {target.Label}.");
        }

        if (skip is not null && (!grant || !target.SupportsSkip))
        {
            throw new McpException($"skip applies only when granting on a server group or an identity, not on {target.Label}.");
        }

        var name = RequireText(permission, nameof(permission));
        var names = await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false);
        if (!names.TryFind(name, out var definition))
        {
            throw new McpException($"The server has no permission named '{name}'. Use ts_perm_list to find the exact name.");
        }

        var parameters = new Dictionary<string, string>(target.Parameters, StringComparer.Ordinal)
        {
            ["permid"] = Text(definition.Id),
        };

        if (grant)
        {
            parameters["permvalue"] = Text(value ?? throw new McpException("grant needs value."));

            // The server wants these flags present for the targets that take them.
            if (target.SupportsNegate)
            {
                parameters["permnegated"] = negated == true ? "1" : "0";
            }

            if (target.SupportsSkip)
            {
                parameters["permskip"] = skip == true ? "1" : "0";
            }
        }

        var records = await executor.RunCommandAsync(
            "ts_perm_set",
            profile,
            new QueryCommand(grant ? target.AddCommand : target.DeleteCommand, parameters, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From(
            grant ? $"Granted {definition.Name} = {value} to {target.Label}." : $"Revoked {definition.Name} from {target.Label}.",
            records);
    }

    /// <summary>Resets every permission on a virtual server.</summary>
    [McpServerTool(Name = "ts_perm_reset", Title = "Reset all permissions of a virtual server",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes every server group and channel group of a virtual server and restores " +
                 "TeamSpeak's default permissions, then returns a new administrator privilege key. Every " +
                 "role and custom permission is lost. TeamSpeak warns that if the reset fails part-way, the " +
                 "virtual server is deleted. As a safeguard, confirmName must repeat the virtual server's " +
                 "exact name. Needs Destructive.")]
    public async Task<ActionResult> ResetAsync(
        [Description(ToolDescriptions.ConfirmVirtualServer)] string confirmName,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var info = await executor.RunAsync(
            "ts_perm_reset", SafetyLevel.Destructive, profile, new QueryCommand("serverinfo", VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        RequireConfirmation(confirmName, info.Count > 0 ? info[0].GetString("virtualserver_name") : string.Empty, "virtual server");

        var records = await executor.RunCommandAsync("ts_perm_reset", profile, new QueryCommand("permreset", VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From("Reset all permissions. The token in details is the new administrator privilege key.", records);
    }
}