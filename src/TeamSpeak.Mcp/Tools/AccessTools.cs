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
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The keys, without their secret values.</returns>
    [McpServerTool(Name = "ts_apikey_list", Title = "List WebQuery API keys",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the WebQuery API keys of every query login: id, owner, virtual server, scope " +
                 "(read, write or manage), and when each expires. The secret key values are never " +
                 "shown; the server does not reveal them after creation. At most limit come back, from " +
                 "offset; total says how many keys there are.")]
    public async Task<ApiKeyList> ListApiKeysAsync(
        [Description("How many keys to skip.")] int offset = 0,
        [Description("How many keys to return, from 1 to 500.")] int limit = 100,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_apikey_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("apikeylist", new Dictionary<string, string> { ["cldbid"] = "*" }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        var keys = records
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
            .ToList();

        var (page, total, skipped) = Page(keys, offset, limit, 500);
        return new ApiKeyList(total, skipped, page);
    }

    /// <summary>Lists query logins.</summary>
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The logins.</returns>
    [McpServerTool(Name = "ts_querylogin_list", Title = "List query logins",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the ServerQuery logins created for a virtual server, such as those of bots " +
                 "and integrations, with the client database id each belongs to. The built-in " +
                 "serveradmin login is not listed. At most limit come back, from offset; total says how " +
                 "many logins there are.")]
    public async Task<QueryLoginList> ListQueryLoginsAsync(
        [Description("How many logins to skip.")] int offset = 0,
        [Description("How many logins to return, from 1 to 500.")] int limit = 100,
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

        var logins = records
            .Select(record => new QueryLogin(record.GetString("client_login_name"), record.GetInt32("cldbid"), record.GetInt32("sid")))
            .ToList();

        var (page, total, skipped) = Page(logins, offset, limit, 500);
        return new QueryLoginList(total, skipped, page);
    }

    /// <summary>Lists the query login's offline messages.</summary>
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The messages, without their bodies.</returns>
    [McpServerTool(Name = "ts_message_list", Title = "List offline messages",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the offline messages in this MCP server's own query login inbox: sender, " +
                 "subject, when it arrived and whether it was read. Use ts_message_get for a body. At most " +
                 "limit come back, from offset; total says how many messages there are." + ToolDescriptions.UserWrittenText)]
    public async Task<OfflineMessageList> ListMessagesAsync(
        [Description("How many messages to skip.")] int offset = 0,
        [Description("How many messages to return, from 1 to 500.")] int limit = 100,
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

        var messages = records
            .Select(record => new OfflineMessageSummary(
                record.GetInt32("msgid"),
                record.GetString("cluid"),
                record.GetString("subject"),
                record.GetUnixTime("timestamp"),
                record.GetBoolean("flag_read")))
            .ToList();

        var (page, total, skipped) = Page(messages, offset, limit, 500);
        return new OfflineMessageList(total, skipped, page);
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
                 "its body. Reading it does not mark it as read." + ToolDescriptions.UserWrittenText)]
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
/// <param name="Total">How many keys there are.</param>
/// <param name="Offset">How many were skipped.</param>
/// <param name="Keys">This page, one entry per key.</param>
public sealed record ApiKeyList(int Total, int Offset, IReadOnlyList<ApiKey> Keys);

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
/// <param name="Total">How many logins there are.</param>
/// <param name="Offset">How many were skipped.</param>
/// <param name="Logins">This page, one entry per login.</param>
public sealed record QueryLoginList(int Total, int Offset, IReadOnlyList<QueryLogin> Logins);

/// <summary>A ServerQuery login.</summary>
/// <param name="LoginName">The login name.</param>
/// <param name="DatabaseId">The client database id it belongs to.</param>
/// <param name="VirtualServerId">The virtual server it is bound to, or 0 for the instance.</param>
public sealed record QueryLogin(string LoginName, int DatabaseId, int VirtualServerId);

/// <summary>Offline messages.</summary>
/// <param name="Total">How many messages there are.</param>
/// <param name="Offset">How many were skipped.</param>
/// <param name="Messages">This page, one entry per message.</param>
public sealed record OfflineMessageList(int Total, int Offset, IReadOnlyList<OfflineMessageSummary> Messages);

/// <summary>An offline message without its body.</summary>
/// <param name="Id">The message id.</param>
/// <param name="SenderUniqueId">The sender's unique identity.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Received">When it arrived.</param>
/// <param name="IsRead">Whether it was marked read.</param>
public sealed record OfflineMessageSummary(int Id, string SenderUniqueId, string Subject, DateTimeOffset? Received, bool IsRead);