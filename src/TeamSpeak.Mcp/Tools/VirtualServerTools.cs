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

    /// <summary>Summarises a virtual server's health.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    [McpServerTool(Name = "ts_health_report", Title = "Check a virtual server's health",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks how a virtual server is doing: status, uptime, people and query clients " +
                 "online against the slot limit, channel count, packet loss, average ping and " +
                 "bandwidth, plus findings in plain words for anything that deserves attention, such " +
                 "as slots running out, packet loss or high ping." + ToolDescriptions.UserWrittenText)]
    public async Task<HealthReport> HealthReportAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_health_report",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("serverinfo", VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return BuildHealthReport(records.Count > 0 ? records[0] : new QueryRecord(new Dictionary<string, string>()));
    }

    /// <summary>Derives a health report from <c>serverinfo</c> fields.</summary>
    /// <param name="info">The <c>serverinfo</c> record.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// The server writes packet loss as a ratio between 0 and 1 and counts query clients among the
    /// clients online; both are converted to what a person would expect to read.
    /// </remarks>
    public static HealthReport BuildHealthReport(QueryRecord info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var status = info.GetString("virtualserver_status");
        var queryClients = info.GetInt32("virtualserver_queryclientsonline");
        var people = Math.Max(0, info.GetInt32("virtualserver_clientsonline") - queryClients);
        var maxClients = info.GetInt32("virtualserver_maxclients");
        var reserved = info.GetInt32("virtualserver_reserved_slots");
        var openSlots = Math.Max(0, maxClients - reserved);
        var slotUsage = openSlots > 0 ? Math.Round(100.0 * people / openSlots, 1) : 0;
        var packetLoss = Math.Round(info.GetDouble("virtualserver_total_packetloss_total") * 100, 2);
        var speechLoss = Math.Round(info.GetDouble("virtualserver_total_packetloss_speech") * 100, 2);
        var ping = Math.Round(info.GetDouble("virtualserver_total_ping"), 1);

        var findings = new List<string>();

        if (status is not ("online" or ""))
        {
            findings.Add($"The virtual server is {status}, not online.");
        }

        if (openSlots > 0 && slotUsage >= 90)
        {
            findings.Add($"{people} of {openSlots} open slots are in use ({slotUsage}%); new clients may soon be turned away.");
        }

        if (packetLoss >= 5)
        {
            findings.Add($"Average packet loss is {packetLoss}%, which clients hear as choppy voice.");
        }
        else if (packetLoss >= 1)
        {
            findings.Add($"Average packet loss is {packetLoss}%, noticeable for some clients.");
        }

        if (ping >= 150)
        {
            findings.Add($"Average ping is {ping} ms, high enough to cause noticeable voice delay.");
        }

        return new HealthReport(
            info.GetString("virtualserver_name"),
            status,
            info.GetInt64("virtualserver_uptime"),
            people,
            queryClients,
            maxClients,
            reserved,
            slotUsage,
            info.GetInt32("virtualserver_channelsonline"),
            packetLoss,
            speechLoss,
            ping,
            info.GetInt64("connection_bandwidth_sent_last_minute_total"),
            info.GetInt64("connection_bandwidth_received_last_minute_total"),
            findings);
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

/// <summary>A virtual server's health, from one <c>serverinfo</c>.</summary>
/// <param name="Name">The server name.</param>
/// <param name="Status">The status, for example <c>online</c>.</param>
/// <param name="UptimeSeconds">Seconds since the virtual server started.</param>
/// <param name="ClientsOnline">Connected people, not counting query clients.</param>
/// <param name="QueryClientsOnline">Connected query clients.</param>
/// <param name="MaxClients">The slot limit.</param>
/// <param name="ReservedSlots">Slots held back for clients with the right to use them.</param>
/// <param name="SlotUsagePercent">People online as a share of the slots open to everyone.</param>
/// <param name="ChannelsOnline">How many channels exist.</param>
/// <param name="PacketLossPercent">Average packet loss across all traffic.</param>
/// <param name="SpeechPacketLossPercent">Average packet loss of voice traffic.</param>
/// <param name="AveragePingMilliseconds">Average ping of connected clients.</param>
/// <param name="BytesSentLastMinute">Bandwidth used sending over the last minute, in bytes per second.</param>
/// <param name="BytesReceivedLastMinute">Bandwidth used receiving over the last minute, in bytes per second.</param>
/// <param name="Findings">Anything that deserves attention, in plain words; empty when all is well.</param>
public sealed record HealthReport(
    string Name,
    string Status,
    long UptimeSeconds,
    int ClientsOnline,
    int QueryClientsOnline,
    int MaxClients,
    int ReservedSlots,
    double SlotUsagePercent,
    int ChannelsOnline,
    double PacketLossPercent,
    double SpeechPacketLossPercent,
    double AveragePingMilliseconds,
    long BytesSentLastMinute,
    long BytesReceivedLastMinute,
    IReadOnlyList<string> Findings);

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