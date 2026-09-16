using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for the identities a virtual server knows, whether or not they are online.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ClientDatabaseTools(QueryExecutor executor)
{
    /// <summary>How many search matches are resolved to full identities, at two commands each.</summary>
    private const int MaxResolvedMatches = 10;

    /// <summary>Pages through known client identities.</summary>
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One page of identities.</returns>
    [McpServerTool(Name = "ts_clientdb_list", Title = "List known client identities",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Pages through every client identity a virtual server has ever seen, online or not: " +
                 "database id, unique identity, last nickname, when it was created and last connected, " +
                 "how often it connected, and its last IP address. Page with offset and limit; total " +
                 "says how many identities exist." + ToolDescriptions.UserWrittenText)]
    public async Task<KnownClientPage> ListKnownClientsAsync(
        [Description("How many identities to skip.")] int offset = 0,
        [Description("How many identities to return, from 1 to 200.")] int limit = 50,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        var records = await executor.RunAsync(
            "ts_clientdb_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "clientdblist",
                new Dictionary<string, string> { ["start"] = Text(offset), ["duration"] = Text(limit) },
                ["-count"],
                virtualServerId),
            cancellationToken).ConfigureAwait(false);

        // -count puts the total on the first record only, and a page past the end has no records.
        int? total = records.Count > 0 ? records[0].GetInt32("count", -1) : null;

        return new KnownClientPage(
            total is -1 ? null : total,
            offset,
            records
                .Select(record => new KnownClient(
                    record.GetInt32("cldbid"),
                    record.GetString("client_unique_identifier"),
                    record.GetString("client_nickname"),
                    record.GetUnixTime("client_created"),
                    record.GetUnixTime("client_lastconnected"),
                    record.GetInt32("client_totalconnections"),
                    Optional(record, "client_description"),
                    Optional(record, "client_lastip")))
                .ToList());
    }

    /// <summary>Shows everything stored about a known identity.</summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All of its fields.</returns>
    [McpServerTool(Name = "ts_clientdb_info", Title = "Show a known client identity",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows everything a virtual server stores about one client identity, online or not: " +
                 "unique identity, nicknames, creation and last connection, connection count, traffic " +
                 "totals, avatar and description, by their ServerQuery field names." + ToolDescriptions.UserWrittenText)]
    public async Task<RecordResult> KnownClientInfoAsync(
        [Description("The client's database id.")] int databaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_clientdb_info",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("clientdbinfo", new Dictionary<string, string> { ["cldbid"] = Text(databaseId) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var fields = FirstFields(records);
        fields.TryAdd("cldbid", Text(databaseId));
        return new RecordResult(fields);
    }

    /// <summary>Searches known identities by nickname or unique identity.</summary>
    /// <param name="pattern">The text to find.</param>
    /// <param name="byUniqueId">Whether to search unique identities instead of nicknames.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching identities.</returns>
    [McpServerTool(Name = "ts_clientdb_find", Title = "Find known client identities",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Finds client identities a virtual server knows, online or not, by last nickname or " +
                 "by unique identity, and returns each with its database id, unique identity, nickname " +
                 "and any online session ids. Plain text matches anywhere in the nickname; % works as a " +
                 "wildcard. At most 10 matches are resolved in full; totalMatches says how many there were." + ToolDescriptions.UserWrittenText)]
    public async Task<ClientIdentityMatches> FindKnownClientsAsync(
        [Description("Text to find, for example 'alice'. Use % as a wildcard; plain text matches anywhere.")]
        string pattern,
        [Description("Search unique identities instead of nicknames.")] bool byUniqueId = false,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var text = RequireText(pattern, nameof(pattern));

        var records = await Run(
            "clientdbfind",
            new Dictionary<string, string> { ["pattern"] = byUniqueId ? text : SqlPattern(text) },
            byUniqueId ? ["-uid"] : null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        var ids = records.Select(record => record.GetInt32("cldbid")).Where(id => id > 0).Distinct().ToList();

        var identities = new List<ClientIdentity>();
        foreach (var id in ids.Take(MaxResolvedMatches))
        {
            identities.Add(await ResolveDatabaseIdAsync(id, virtualServerId, profile, cancellationToken).ConfigureAwait(false));
        }

        return new ClientIdentityMatches(ids.Count, ids.Count > MaxResolvedMatches, identities);
    }

    /// <summary>Connects the three ways TeamSpeak identifies a client.</summary>
    /// <param name="clientId">An online session id.</param>
    /// <param name="databaseId">A database id.</param>
    /// <param name="uniqueId">A unique identity.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identity.</returns>
    [McpServerTool(Name = "ts_client_resolve", Title = "Resolve a client identity",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Resolves any one of the ids TeamSpeak uses for a client into all of them: the " +
                 "permanent database id (what permissions and groups use), the unique identity, the " +
                 "last nickname, and the session ids of any current connections (what moving, " +
                 "kicking and messaging use). Pass exactly one of clientId, databaseId or uniqueId. To " +
                 "search by nickname, use ts_clientdb_find." + ToolDescriptions.UserWrittenText)]
    public async Task<ClientIdentity> ResolveClientAsync(
        [Description("A session id of a connected client, as in ts_client_list.")] int? clientId = null,
        [Description("A permanent database id.")] int? databaseId = null,
        [Description("A unique identity, the 44-character value ts_client_list shows as uniqueId.")] string? uniqueId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var given = (clientId is null ? 0 : 1) + (databaseId is null ? 0 : 1) + (string.IsNullOrWhiteSpace(uniqueId) ? 0 : 1);
        if (given != 1)
        {
            throw new McpException("Pass exactly one of clientId, databaseId or uniqueId.");
        }

        if (databaseId is { } dbid)
        {
            return await ResolveDatabaseIdAsync(dbid, virtualServerId, profile, cancellationToken).ConfigureAwait(false);
        }

        var uid = uniqueId?.Trim();
        if (clientId is { } clid)
        {
            var session = await Run(
                "clientgetuidfromclid",
                new Dictionary<string, string> { ["clid"] = Text(clid) },
                null,
                virtualServerId,
                profile,
                cancellationToken).ConfigureAwait(false);

            uid = session.Count > 0 ? Optional(session[0], "cluid") : null;
            if (uid is null)
            {
                throw new McpException($"No connected client has session id {clid}. Session ids change on every reconnect; see ts_client_list.");
            }
        }

        var named = await Run(
            "clientgetnamefromuid",
            new Dictionary<string, string> { ["cluid"] = uid! },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        if (named.Count == 0)
        {
            throw new McpException($"No client with unique identity '{uid}' is known to this virtual server.");
        }

        return await WithSessionsAsync(named[0].GetInt32("cldbid"), uid!, named[0].GetString("name"), virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists a known identity's custom properties.</summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The properties.</returns>
    [McpServerTool(Name = "ts_custom_info", Title = "Show a client's custom properties",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the custom properties stored for one client identity, such as a linked forum " +
                 "account, as identifier and value pairs. Integrations set these; most clients have none." + ToolDescriptions.UserWrittenText)]
    public async Task<CustomProperties> CustomInfoAsync(
        [Description("The client's database id.")] int databaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await Run(
            "custominfo",
            new Dictionary<string, string> { ["cldbid"] = Text(databaseId) },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (Optional(record, "ident") is { } ident)
            {
                properties[ident] = record.GetString("value");
            }
        }

        return new CustomProperties(databaseId, properties);
    }

    /// <summary>Finds identities by a custom property.</summary>
    /// <param name="ident">The property identifier.</param>
    /// <param name="pattern">The value to find.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matches.</returns>
    [McpServerTool(Name = "ts_custom_search", Title = "Find clients by custom property",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Finds the client identities whose custom property has a given value, for example " +
                 "which client is linked to a forum account. Plain text matches anywhere in the value; " +
                 "% works as a wildcard." + ToolDescriptions.UserWrittenText)]
    public async Task<CustomPropertyMatches> CustomSearchAsync(
        [Description("The property identifier, for example 'forum_account'.")] string ident,
        [Description("The value to find. Use % as a wildcard; plain text matches anywhere.")] string pattern,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await Run(
            "customsearch",
            new Dictionary<string, string>
            {
                ["ident"] = RequireText(ident, nameof(ident)),
                ["pattern"] = SqlPattern(RequireText(pattern, nameof(pattern))),
            },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        return new CustomPropertyMatches(records
            .Select(record => new CustomPropertyMatch(record.GetInt32("cldbid"), record.GetString("ident"), record.GetString("value")))
            .ToList());
    }

    private async Task<ClientIdentity> ResolveDatabaseIdAsync(
        int databaseId,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var named = await Run(
            "clientgetnamefromdbid",
            new Dictionary<string, string> { ["cldbid"] = Text(databaseId) },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        if (named.Count == 0 || Optional(named[0], "cluid") is not { } uid)
        {
            throw new McpException($"No client with database id {databaseId} is known to this virtual server.");
        }

        return await WithSessionsAsync(databaseId, uid, named[0].GetString("name"), virtualServerId, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ClientIdentity> WithSessionsAsync(
        int databaseId,
        string uniqueId,
        string nickname,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        // An offline client comes back as an empty result, which the executor turns into no records.
        var sessions = await Run(
            "clientgetids",
            new Dictionary<string, string> { ["cluid"] = uniqueId },
            null,
            virtualServerId,
            profile,
            cancellationToken).ConfigureAwait(false);

        return new ClientIdentity(
            databaseId,
            uniqueId,
            nickname,
            sessions.Select(session => session.GetInt32("clid")).Where(id => id > 0).ToList());
    }

    private Task<IReadOnlyList<QueryRecord>> Run(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? options,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken) =>
        executor.RunAsync(
            command,
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(command, parameters, options, virtualServerId),
            cancellationToken);
}

/// <summary>One page of known client identities.</summary>
/// <param name="Total">How many identities exist, when the server reported it.</param>
/// <param name="Offset">How many were skipped.</param>
/// <param name="Clients">The identities on this page.</param>
public sealed record KnownClientPage(int? Total, int Offset, IReadOnlyList<KnownClient> Clients);

/// <summary>A client identity the server knows.</summary>
/// <param name="DatabaseId">The permanent database id.</param>
/// <param name="UniqueId">The unique identity.</param>
/// <param name="Nickname">The last nickname.</param>
/// <param name="Created">When the identity first connected.</param>
/// <param name="LastConnected">When it last connected.</param>
/// <param name="TotalConnections">How many times it connected.</param>
/// <param name="Description">The description an admin set, if any.</param>
/// <param name="LastIp">The address it last connected from, when the query login may see it.</param>
public sealed record KnownClient(
    int DatabaseId,
    string UniqueId,
    string Nickname,
    DateTimeOffset? Created,
    DateTimeOffset? LastConnected,
    int TotalConnections,
    string? Description,
    string? LastIp);

/// <summary>A client's identity in all the forms TeamSpeak uses.</summary>
/// <param name="DatabaseId">The permanent database id, used by permissions and groups.</param>
/// <param name="UniqueId">The unique identity.</param>
/// <param name="Nickname">The last known nickname.</param>
/// <param name="OnlineClientIds">Session ids of current connections; empty when offline.</param>
public sealed record ClientIdentity(int DatabaseId, string UniqueId, string Nickname, IReadOnlyList<int> OnlineClientIds);

/// <summary>The outcome of an identity search.</summary>
/// <param name="TotalMatches">How many identities matched.</param>
/// <param name="Truncated">Whether only the first matches were resolved.</param>
/// <param name="Clients">The resolved matches.</param>
public sealed record ClientIdentityMatches(int TotalMatches, bool Truncated, IReadOnlyList<ClientIdentity> Clients);

/// <summary>A client's custom properties.</summary>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="Properties">Values by identifier.</param>
public sealed record CustomProperties(int DatabaseId, IReadOnlyDictionary<string, string> Properties);

/// <summary>Clients found by a custom property.</summary>
/// <param name="Matches">One entry per match.</param>
public sealed record CustomPropertyMatches(IReadOnlyList<CustomPropertyMatch> Matches);

/// <summary>A custom property on a client.</summary>
/// <param name="DatabaseId">The client's database id.</param>
/// <param name="Ident">The property identifier.</param>
/// <param name="Value">The value.</param>
public sealed record CustomPropertyMatch(int DatabaseId, string Ident, string Value);