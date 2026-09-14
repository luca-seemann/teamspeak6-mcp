using System.ComponentModel;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for virtual servers.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class VirtualServerTools(QueryExecutor executor)
{
    /// <summary>Lists the virtual servers of an instance.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The virtual servers.</returns>
    [McpServerTool(Name = "ts_vserver_list", Title = "List virtual servers",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the virtual servers on a TeamSpeak instance with their id, port, status, name " +
                 "and how many clients are online. The id is what other tools take as virtualServerId.")]
    public async Task<VirtualServerList> ListVirtualServersAsync(
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_vserver_list", SafetyLevel.ReadOnly, profile, new QueryCommand("serverlist"), cancellationToken)
            .ConfigureAwait(false);

        return new VirtualServerList(records
            .Select(record => new VirtualServerSummary(
                record.GetInt32("virtualserver_id"),
                record.GetInt32("virtualserver_port"),
                record.GetString("virtualserver_status"),
                record.GetString("virtualserver_name"),
                record.GetInt32("virtualserver_clientsonline"),
                record.GetInt32("virtualserver_queryclientsonline"),
                record.GetInt32("virtualserver_maxclients"),
                record.GetInt64("virtualserver_uptime"),
                record.GetBoolean("virtualserver_autostart")))
            .ToList());
    }

    /// <summary>Shows every property of a virtual server.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All of its fields.</returns>
    [McpServerTool(Name = "ts_vserver_info", Title = "Show a virtual server",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows every property of one virtual server: name, welcome message, slots, uptime, " +
                 "packet loss and ping totals, security level, default groups, and the rest of its " +
                 "configuration, by their ServerQuery field names.")]
    public async Task<RecordResult> VirtualServerInfoAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_vserver_info",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("serverinfo", VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new RecordResult(records.Count > 0 ? QueryExecutor.ToFields(records[0]) : new Dictionary<string, string>());
    }
}

/// <summary>The virtual servers of an instance.</summary>
/// <param name="VirtualServers">One entry per virtual server.</param>
public sealed record VirtualServerList(IReadOnlyList<VirtualServerSummary> VirtualServers);

/// <summary>A virtual server at a glance.</summary>
/// <param name="Id">The virtual server id.</param>
/// <param name="Port">The voice port clients connect to.</param>
/// <param name="Status">The status, for example <c>online</c>.</param>
/// <param name="Name">The server name.</param>
/// <param name="ClientsOnline">Connected clients, including query clients.</param>
/// <param name="QueryClientsOnline">Connected query clients.</param>
/// <param name="MaxClients">The slot limit.</param>
/// <param name="UptimeSeconds">Seconds since the virtual server started.</param>
/// <param name="Autostart">Whether it starts with the instance.</param>
public sealed record VirtualServerSummary(
    int Id,
    int Port,
    string Status,
    string Name,
    int ClientsOnline,
    int QueryClientsOnline,
    int MaxClients,
    long UptimeSeconds,
    bool Autostart);