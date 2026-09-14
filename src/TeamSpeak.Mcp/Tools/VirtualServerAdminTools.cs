using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that change virtual servers and the instance.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class VirtualServerAdminTools(QueryExecutor executor)
{
    /// <summary>Creates a virtual server.</summary>
    [McpServerTool(Name = "ts_vserver_create", Title = "Create a virtual server",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Creates a new virtual server with a name and optional virtualserver_* properties such " +
                 "as virtualserver_port or virtualserver_maxclients, and returns its id, port and initial " +
                 "administrator privilege key. That key grants full control of the new server; give it only " +
                 "to its future owner. The TeamSpeak license may limit how many virtual servers can exist. " +
                 "Needs Destructive, because it hands out full control of the new server.")]
    public async Task<ActionResult> CreateAsync(
        [Description("The name of the new virtual server.")] string name,
        [Description(ToolDescriptions.VirtualServerProperties)] IReadOnlyDictionary<string, string>? properties = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = Properties(properties, "virtualserver_", required: false);
        parameters["virtualserver_name"] = RequireText(name, nameof(name));

        var records = await executor.RunCommandAsync("ts_vserver_create", profile, new QueryCommand("servercreate", parameters), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Created virtual server '{parameters["virtualserver_name"]}'.", records);
    }

    /// <summary>Changes a virtual server's properties.</summary>
    [McpServerTool(Name = "ts_vserver_edit", Title = "Change a virtual server",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Changes one or more properties of a virtual server at once, such as its name, welcome " +
                 "message, password, slot limit or host message, using the virtualserver_* names " +
                 "ts_vserver_info shows. Needs Write.")]
    public async Task<ActionResult> EditAsync(
        [Description(ToolDescriptions.VirtualServerProperties)] IReadOnlyDictionary<string, string> properties,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = Properties(properties, "virtualserver_", required: true);

        var records = await executor.RunCommandAsync(
            "ts_vserver_edit", profile, new QueryCommand("serveredit", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Changed {string.Join(", ", parameters.Keys)}.", records);
    }

    /// <summary>Starts or stops a virtual server.</summary>
    [McpServerTool(Name = "ts_vserver_power", Title = "Start or stop a virtual server",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Starts or stops a virtual server. Starting needs Write. Stopping disconnects everyone on " +
                 "it and needs Destructive; the optional reason is shown to them. The virtual server id is " +
                 "required here rather than defaulted, so the wrong server is not stopped by accident.")]
    public async Task<ActionResult> PowerAsync(
        [Description("start or stop.")] string action,
        [Description("The virtual server to start or stop.")] int virtualServerId,
        [Description("When stopping, a message shown to the clients being disconnected.")] string? reason = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var choice = Choice(action, nameof(action), "start", "stop");
        var parameters = new Dictionary<string, string> { ["sid"] = Text(virtualServerId) };

        if (choice == "stop" && !string.IsNullOrWhiteSpace(reason))
        {
            parameters["reasonmsg"] = reason.Trim();
        }

        var records = await executor.RunCommandAsync(
            "ts_vserver_power", profile, new QueryCommand(choice == "start" ? "serverstart" : "serverstop", parameters), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From(choice == "start" ? $"Started virtual server {virtualServerId}." : $"Stopped virtual server {virtualServerId}.", records);
    }

    /// <summary>Deletes a stopped virtual server.</summary>
    [McpServerTool(Name = "ts_vserver_delete", Title = "Delete a virtual server",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes a virtual server permanently, with all its channels, groups, permissions, bans " +
                 "and known identities. The server must be stopped first. As a safeguard, confirmName must " +
                 "repeat the virtual server's exact name. Needs Destructive.")]
    public async Task<ActionResult> DeleteAsync(
        [Description("The virtual server to delete.")] int virtualServerId,
        [Description(ToolDescriptions.ConfirmVirtualServer)] string confirmName,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        // Checked at the level of the deletion itself, so a lower profile learns nothing and sends nothing.
        var servers = await executor.RunAsync(
            "ts_vserver_delete", SafetyLevel.Destructive, profile, new QueryCommand("serverlist"), cancellationToken).ConfigureAwait(false);

        var server = servers.FirstOrDefault(candidate => candidate.GetInt32("virtualserver_id") == virtualServerId)
            ?? throw new McpException($"There is no virtual server {virtualServerId}.");

        RequireConfirmation(confirmName, server.GetString("virtualserver_name"), "virtual server");

        var records = await executor.RunCommandAsync(
            "ts_vserver_delete", profile, new QueryCommand("serverdelete", new Dictionary<string, string> { ["sid"] = Text(virtualServerId) }), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Deleted virtual server {virtualServerId} '{server.GetString("virtualserver_name")}'.", records);
    }

    /// <summary>Takes a snapshot of a virtual server.</summary>
    [McpServerTool(Name = "ts_vserver_snapshot_create", Title = "Snapshot a virtual server",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Takes a snapshot of a virtual server's configuration, channels, groups, permissions and " +
                 "known identities, returned as version, data and, when a password was given, salt. Keep " +
                 "all three to deploy it later with ts_vserver_snapshot_deploy. The snapshot contains the " +
                 "server's whole configuration, so it needs Write even though nothing changes.")]
    public async Task<ActionResult> SnapshotCreateAsync(
        [Description("A password to encrypt the snapshot with; the same one is needed to deploy it.")] string? password = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = string.IsNullOrWhiteSpace(password)
            ? null
            : new Dictionary<string, string> { ["password"] = password };

        var records = await executor.RunCommandAsync(
            "ts_vserver_snapshot_create", profile, new QueryCommand("serversnapshotcreate", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From("Created a snapshot.", records);
    }

    /// <summary>Deploys a snapshot over a virtual server.</summary>
    [McpServerTool(Name = "ts_vserver_snapshot_deploy", Title = "Deploy a snapshot over a virtual server",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replaces a virtual server's configuration, channels, groups and permissions with a " +
                 "snapshot from ts_vserver_snapshot_create, and returns how old channel ids map to new ones. " +
                 "TeamSpeak does not check permissions while deploying, so a snapshot can grant any " +
                 "privilege: deploy only snapshots whose origin you trust. As a safeguard, confirmName must " +
                 "repeat the target virtual server's exact name. Needs Destructive.")]
    public async Task<ActionResult> SnapshotDeployAsync(
        [Description("The snapshot's version field.")] string version,
        [Description("The snapshot's data field.")] string data,
        [Description(ToolDescriptions.ConfirmVirtualServer)] string confirmName,
        [Description("The snapshot's salt field, present when it was created with a password.")] string? salt = null,
        [Description("The password the snapshot was created with.")] string? password = null,
        [Description("Keep the files of channels that exist both before and after.")] bool keepFiles = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var info = await executor.RunAsync(
            "ts_vserver_snapshot_deploy", SafetyLevel.Destructive, profile, new QueryCommand("serverinfo", VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        RequireConfirmation(confirmName, info.Count > 0 ? info[0].GetString("virtualserver_name") : string.Empty, "virtual server");

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = RequireText(version, nameof(version)),
            ["data"] = RequireText(data, nameof(data)),
        };

        if (!string.IsNullOrWhiteSpace(salt))
        {
            parameters["salt"] = salt.Trim();
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            parameters["password"] = password;
        }

        var options = keepFiles ? new[] { "-mapping", "-keepfiles" } : ["-mapping"];

        var records = await executor.RunCommandAsync(
            "ts_vserver_snapshot_deploy", profile, new QueryCommand("serversnapshotdeploy", parameters, options, virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return new ActionResult(
            "Deployed the snapshot. Channel ids have changed; see records for the mapping from ocid to ncid.",
            new Dictionary<string, string>(),
            records.Select(QueryExecutor.ToFields).ToList());
    }

    /// <summary>Changes instance-wide settings.</summary>
    [McpServerTool(Name = "ts_instance_edit", Title = "Change instance settings",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Changes settings of the whole TeamSpeak instance, such as the file transfer port or the " +
                 "ServerQuery flood limits, using the serverinstance_* names ts_instance_info shows. A wrong " +
                 "value can lock every query client out, including this server, so it needs Destructive.")]
    public async Task<ActionResult> InstanceEditAsync(
        [Description(ToolDescriptions.InstanceProperties)] IReadOnlyDictionary<string, string> properties,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = Properties(properties, "serverinstance_", required: true);

        var records = await executor.RunCommandAsync("ts_instance_edit", profile, new QueryCommand("instanceedit", parameters), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Changed {string.Join(", ", parameters.Keys)}.", records);
    }

    /// <summary>Lists, adds or removes temporary server passwords.</summary>
    [McpServerTool(Name = "ts_temp_password", Title = "Manage temporary server passwords",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Manages temporary server passwords, which let someone join a password-protected server " +
                 "for a limited time, optionally straight into a channel. list shows them in clear text and " +
                 "needs Write; delete removes one by its password and needs Write; add creates one and needs " +
                 "Destructive, because it hands out access.")]
    public async Task<ActionResult> TempPasswordAsync(
        [Description("list, add or delete.")] string action,
        [Description("For add and delete: the password.")] string? password = null,
        [Description("For add: how many seconds it stays valid.")] int? durationSeconds = null,
        [Description("For add: what it is for.")] string? description = null,
        [Description("For add: a channel to put people who use it into. Omit for the default channel.")] int? channelId = null,
        [Description("For add: that channel's password, if it has one.")] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var choice = Choice(action, nameof(action), "list", "add", "delete");

        QueryCommand command = choice switch
        {
            "list" => new("servertemppasswordlist", VirtualServerId: virtualServerId),
            "delete" => new(
                "servertemppassworddel",
                new Dictionary<string, string> { ["pw"] = RequireText(password, nameof(password)) },
                VirtualServerId: virtualServerId),
            _ => new(
                "servertemppasswordadd",
                TempPasswordParameters(password, durationSeconds, description, channelId, channelPassword),
                VirtualServerId: virtualServerId),
        };

        var records = await executor.RunCommandAsync("ts_temp_password", profile, command, cancellationToken).ConfigureAwait(false);

        return choice == "list"
            ? new ActionResult($"{records.Count} temporary password(s).", new Dictionary<string, string>(), records.Select(QueryExecutor.ToFields).ToList())
            : ActionResult.From(choice == "add" ? "Added the temporary password." : "Deleted the temporary password.", records);
    }

    private static Dictionary<string, string> TempPasswordParameters(
        string? password,
        int? durationSeconds,
        string? description,
        int? channelId,
        string? channelPassword)
    {
        if (durationSeconds is not > 0)
        {
            throw new McpException("durationSeconds must be a positive number of seconds.");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pw"] = RequireText(password, nameof(password)),
            ["duration"] = Text(durationSeconds.Value),
            ["desc"] = string.IsNullOrWhiteSpace(description) ? "created by teamspeak6-mcp" : description.Trim(),
            ["tcid"] = Text(channelId ?? 0),
        };

        if (!string.IsNullOrWhiteSpace(channelPassword))
        {
            parameters["tcpw"] = channelPassword;
        }

        return parameters;
    }
}