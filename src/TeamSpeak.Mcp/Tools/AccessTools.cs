using System.ComponentModel;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for query access: API keys, query logins, and the query login's offline messages.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class AccessTools(QueryExecutor executor)
{
    /// <summary>Lists WebQuery API keys.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The keys, without their secret values.</returns>
    [McpServerTool(Name = "ts_apikey_list", Title = "List WebQuery API keys",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the WebQuery API keys of every query login: id, owner, virtual server, scope " +
                 "(read, write or manage), and when each expires. The secret key values are never " +
                 "shown; the server does not reveal them after creation.")]
    public async Task<ApiKeyList> ListApiKeysAsync(
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_apikey_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("apikeylist", new Dictionary<string, string> { ["cldbid"] = "*" }),
            cancellationToken).ConfigureAwait(false);

        return new ApiKeyList(records
            .Select(record =>
            {
                // "unlimited" is written instead of a number for a key that never expires, and such
                // a key reports its creation time as its expiry.
                var unlimited = record.GetString("time_left") == "unlimited";
                return new ApiKey(
                    record.GetInt32("id"),
                    record.GetInt32("cldbid"),
                    record.GetInt32("sid"),
                    record.GetString("scope"),
                    record.GetUnixTime("created_at"),
                    unlimited ? null : record.GetUnixTime("expires_at"),
                    unlimited);
            })
            .ToList());
    }

    /// <summary>Lists query logins.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The logins.</returns>
    [McpServerTool(Name = "ts_querylogin_list", Title = "List query logins",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the ServerQuery logins created for a virtual server, such as those of bots " +
                 "and integrations, with the client database id each belongs to. The built-in " +
                 "serveradmin login is not listed.")]
    public async Task<QueryLoginList> ListQueryLoginsAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_querylogin_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("queryloginlist", VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new QueryLoginList(records
            .Select(record => new QueryLogin(record.GetString("client_login_name"), record.GetInt32("cldbid"), record.GetInt32("sid")))
            .ToList());
    }

    /// <summary>Lists the query login's offline messages.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The messages, without their bodies.</returns>
    [McpServerTool(Name = "ts_message_list", Title = "List offline messages",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the offline messages in this MCP server's own query login inbox: sender, " +
                 "subject, when it arrived and whether it was read. Use ts_message_get for a body.")]
    public async Task<OfflineMessageList> ListMessagesAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_message_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("messagelist", VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new OfflineMessageList(records
            .Select(record => new OfflineMessageSummary(
                record.GetInt32("msgid"),
                record.GetString("cluid"),
                record.GetString("subject"),
                record.GetUnixTime("timestamp"),
                record.GetBoolean("flag_read")))
            .ToList());
    }

    /// <summary>Reads an offline message.</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The message.</returns>
    [McpServerTool(Name = "ts_message_get", Title = "Read an offline message",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Reads one offline message from this MCP server's own query login inbox, including " +
                 "its body. Reading it does not mark it as read.")]
    public async Task<RecordResult> GetMessageAsync(
        [Description("The message id, as listed by ts_message_list.")] int messageId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_message_get",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("messageget", new Dictionary<string, string> { ["msgid"] = Text(messageId) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new RecordResult(FirstFields(records));
    }
}

/// <summary>WebQuery API keys.</summary>
/// <param name="Keys">One entry per key.</param>
public sealed record ApiKeyList(IReadOnlyList<ApiKey> Keys);

/// <summary>A WebQuery API key, without its secret.</summary>
/// <param name="Id">The key id.</param>
/// <param name="OwnerDatabaseId">The query login's client database id.</param>
/// <param name="VirtualServerId">The virtual server it was created on.</param>
/// <param name="Scope"><c>read</c>, <c>write</c> or <c>manage</c>.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Expires">When it expires; absent for a key that never does.</param>
/// <param name="NeverExpires">Whether the key has no expiry.</param>
public sealed record ApiKey(
    int Id,
    int OwnerDatabaseId,
    int VirtualServerId,
    string Scope,
    DateTimeOffset? Created,
    DateTimeOffset? Expires,
    bool NeverExpires);

/// <summary>Query logins.</summary>
/// <param name="Logins">One entry per login.</param>
public sealed record QueryLoginList(IReadOnlyList<QueryLogin> Logins);

/// <summary>A ServerQuery login.</summary>
/// <param name="LoginName">The login name.</param>
/// <param name="DatabaseId">The client database id it belongs to.</param>
/// <param name="VirtualServerId">The virtual server it is bound to, or 0 for the instance.</param>
public sealed record QueryLogin(string LoginName, int DatabaseId, int VirtualServerId);

/// <summary>Offline messages.</summary>
/// <param name="Messages">One entry per message.</param>
public sealed record OfflineMessageList(IReadOnlyList<OfflineMessageSummary> Messages);

/// <summary>An offline message without its body.</summary>
/// <param name="Id">The message id.</param>
/// <param name="SenderUniqueId">The sender's unique identity.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Received">When it arrived.</param>
/// <param name="IsRead">Whether it was marked read.</param>
public sealed record OfflineMessageSummary(int Id, string SenderUniqueId, string Subject, DateTimeOffset? Received, bool IsRead);