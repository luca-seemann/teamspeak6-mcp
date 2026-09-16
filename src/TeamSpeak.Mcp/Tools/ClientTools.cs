using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for clients that are currently online.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ClientTools(QueryExecutor executor)
{
    /// <summary>Lists the clients online on a virtual server.</summary>
    /// <param name="includeQueryClients">Whether to include query clients.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The clients.</returns>
    [McpServerTool(Name = "ts_client_list", Title = "List online clients",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the clients currently connected to a virtual server: nickname, the channel " +
                 "they are in, their session client id and permanent database id, unique identity, " +
                 "server groups, channel group and away status. Query clients, including this MCP " +
                 "server's own session, are left out unless includeQueryClients is true." + ToolDescriptions.UserWrittenText)]
    public async Task<ClientList> ListClientsAsync(
        [Description("Include ServerQuery clients such as bots and this MCP server's own session.")]
        bool includeQueryClients = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_client_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("clientlist", Options: ["-uid", "-away", "-groups"], VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new ClientList(records
            .Select(ToClient)
            .Where(client => includeQueryClients || !client.IsQueryClient)
            .ToList());
    }

    /// <summary>Shows every property of an online client.</summary>
    /// <param name="clientId">The client's session id.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All of its fields.</returns>
    [McpServerTool(Name = "ts_client_info", Title = "Show an online client",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows every property of one connected client: version, platform, country, " +
                 "connection time, idle time, groups, talk power, mute state and the rest, by their " +
                 "ServerQuery field names. Takes the session client id from ts_client_list, which " +
                 "changes on every reconnect, not the permanent database id." + ToolDescriptions.UserWrittenText)]
    public async Task<RecordResult> ClientInfoAsync(
        [Description("The client's session id (clientId in ts_client_list), not its database id.")]
        int clientId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_client_info",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "clientinfo",
                new Dictionary<string, string> { ["clid"] = clientId.ToString(CultureInfo.InvariantCulture) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var fields = records.Count > 0
            ? new Dictionary<string, string>(QueryExecutor.ToFields(records[0]))
            : [];

        // clientinfo does not echo the id it was asked about.
        fields.TryAdd("clid", clientId.ToString(CultureInfo.InvariantCulture));
        return new RecordResult(fields);
    }

    /// <summary>Finds connected clients by nickname.</summary>
    /// <param name="pattern">Text to find in nicknames.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching clients.</returns>
    [McpServerTool(Name = "ts_client_find", Title = "Find online clients by nickname",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Finds the currently connected clients whose nickname contains some text and returns " +
                 "their session ids and nicknames, or an empty list when no one matches. To find someone " +
                 "who is offline, use ts_clientdb_find." + ToolDescriptions.UserWrittenText)]
    public async Task<OnlineClientMatches> FindClientsAsync(
        [Description("Text to find in nicknames, for example 'alice'.")] string pattern,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunSearchAsync(
            "ts_client_find",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "clientfind",
                new Dictionary<string, string> { ["pattern"] = ToolArguments.RequireText(pattern, nameof(pattern)) },
                VirtualServerId: virtualServerId),
            QueryErrorCode.InvalidClientId,
            cancellationToken).ConfigureAwait(false);

        return new OnlineClientMatches(records
            .Select(record => new OnlineClientMatch(record.GetInt32("clid"), record.GetString("client_nickname")))
            .ToList());
    }

    private static OnlineClient ToClient(QueryRecord record) =>
        new(
            record.GetInt32("clid"),
            record.GetInt32("client_database_id"),
            record.GetInt32("cid"),
            record.GetString("client_nickname"),
            record.GetString("client_unique_identifier"),
            IsQueryClient: record.GetInt32("client_type") == 1,
            IsAway: record.GetBoolean("client_away"),
            record.GetString("client_away_message") is { Length: > 0 } message ? message : null,
            record.GetString("client_servergroups")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => int.TryParse(id, CultureInfo.InvariantCulture, out var value) ? value : -1)
                .Where(id => id >= 0)
                .ToList(),
            record.GetInt32("client_channel_group_id"));
}

/// <summary>Connected clients found by nickname.</summary>
/// <param name="Clients">The matches.</param>
public sealed record OnlineClientMatches(IReadOnlyList<OnlineClientMatch> Clients);

/// <summary>A connected client found by nickname.</summary>
/// <param name="ClientId">The session id.</param>
/// <param name="Nickname">The nickname.</param>
public sealed record OnlineClientMatch(int ClientId, string Nickname);

/// <summary>The clients online on a virtual server.</summary>
/// <param name="Clients">One entry per connected client.</param>
public sealed record ClientList(IReadOnlyList<OnlineClient> Clients);

/// <summary>A connected client.</summary>
/// <param name="ClientId">The session id, which changes on every reconnect.</param>
/// <param name="DatabaseId">The permanent database id.</param>
/// <param name="ChannelId">The channel the client is in.</param>
/// <param name="Nickname">The nickname.</param>
/// <param name="UniqueId">The permanent unique identity.</param>
/// <param name="IsQueryClient">Whether this is a ServerQuery client rather than a person.</param>
/// <param name="IsAway">Whether the client is marked away.</param>
/// <param name="AwayMessage">The away message, when one is set.</param>
/// <param name="ServerGroupIds">The server groups the client belongs to.</param>
/// <param name="ChannelGroupId">The client's channel group in its current channel.</param>
public sealed record OnlineClient(
    int ClientId,
    int DatabaseId,
    int ChannelId,
    string Nickname,
    string UniqueId,
    bool IsQueryClient,
    bool IsAway,
    string? AwayMessage,
    IReadOnlyList<int> ServerGroupIds,
    int ChannelGroupId);