using System.ComponentModel;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for bans, complaints and privilege keys.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ModerationTools(QueryExecutor executor)
{
    /// <summary>Lists the active bans.</summary>
    /// <param name="offset">How many to skip.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bans.</returns>
    [McpServerTool(Name = "ts_ban_list", Title = "List bans",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the active ban rules of a virtual server: what each matches (IP address, " +
                 "name pattern, unique identity or myTeamSpeak id), the reason, who created it and " +
                 "when, when it expires, and how often it has been enforced.")]
    public async Task<BanList> ListBansAsync(
        [Description("How many bans to skip.")] int offset = 0,
        [Description("How many bans to return, from 1 to 200.")] int limit = 100,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_ban_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "banlist",
                new Dictionary<string, string> { ["start"] = Text(Math.Max(0, offset)), ["duration"] = Text(Math.Clamp(limit, 1, 200)) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new BanList(records.Select(ToBan).ToList());
    }

    /// <summary>Lists complaints.</summary>
    /// <param name="databaseId">Only complaints about this client.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The complaints.</returns>
    [McpServerTool(Name = "ts_complaint_list", Title = "List complaints",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the complaints users filed against other users on a virtual server: who " +
                 "complained about whom, the message, and when. Pass databaseId to see only " +
                 "complaints about one client.")]
    public async Task<ComplaintList> ListComplaintsAsync(
        [Description("Only complaints about this client database id.")] int? databaseId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_complaint_list",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand(
                "complainlist",
                databaseId is { } target ? new Dictionary<string, string> { ["tcldbid"] = Text(target) } : null,
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new ComplaintList(records
            .Select(record => new Complaint(
                record.GetInt32("tcldbid"),
                record.GetString("tname"),
                record.GetInt32("fcldbid"),
                record.GetString("fname"),
                record.GetString("message"),
                record.GetUnixTime("timestamp")))
            .ToList());
    }

    /// <summary>Lists the unused privilege keys.</summary>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The keys.</returns>
    [McpServerTool(Name = "ts_token_list", Title = "List privilege keys",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the unused privilege keys of a virtual server: the key itself, the server " +
                 "group or channel group it grants, when it was created and its description. The keys " +
                 "are live credentials, so this needs the Write safety level even though it changes " +
                 "nothing.")]
    public async Task<PrivilegeKeyList> ListPrivilegeKeysAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_token_list",
            SafetyLevel.Write,
            profile,
            new QueryCommand("privilegekeylist", VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return new PrivilegeKeyList(records
            .Select(record =>
            {
                var channelGroup = record.GetInt32("token_type") == 1;
                return new PrivilegeKey(
                    record.GetString("token"),
                    channelGroup ? "channel group" : "server group",
                    record.GetInt32("token_id1"),
                    channelGroup ? record.GetInt32("token_id2") : null,
                    record.GetUnixTime("token_created"),
                    Optional(record, "token_description"));
            })
            .ToList());
    }

    private static Ban ToBan(QueryRecord record)
    {
        var created = record.GetUnixTime("created");
        var duration = record.GetInt64("duration");

        return new Ban(
            record.GetInt32("banid"),
            Optional(record, "ip"),
            Optional(record, "name"),
            Optional(record, "uid"),
            Optional(record, "mytsid"),
            Optional(record, "lastnickname"),
            Optional(record, "reason"),
            created,
            duration,
            duration > 0 && created is { } start ? start.AddSeconds(duration) : null,
            record.GetString("invokername"),
            record.GetInt32("invokercldbid"),
            record.GetString("invokeruid"),
            record.GetInt32("enforcements"));
    }
}

/// <summary>The active bans.</summary>
/// <param name="Bans">One entry per ban rule.</param>
public sealed record BanList(IReadOnlyList<Ban> Bans);

/// <summary>A ban rule.</summary>
/// <param name="Id">The ban id.</param>
/// <param name="Ip">The IP address or pattern it matches, if any.</param>
/// <param name="Name">The nickname pattern it matches, if any.</param>
/// <param name="UniqueId">The unique identity it matches, if any.</param>
/// <param name="MyTeamSpeakId">The myTeamSpeak id it matches, if any.</param>
/// <param name="LastNickname">The banned client's last nickname, if known.</param>
/// <param name="Reason">The reason given.</param>
/// <param name="Created">When the ban was created.</param>
/// <param name="DurationSeconds">How long it lasts, or 0 for permanently.</param>
/// <param name="Expires">When it expires; absent for a permanent ban.</param>
/// <param name="InvokerName">Who created it.</param>
/// <param name="InvokerDatabaseId">The creator's database id.</param>
/// <param name="InvokerUniqueId">The creator's unique identity.</param>
/// <param name="Enforcements">How many connection attempts it has blocked.</param>
public sealed record Ban(
    int Id,
    string? Ip,
    string? Name,
    string? UniqueId,
    string? MyTeamSpeakId,
    string? LastNickname,
    string? Reason,
    DateTimeOffset? Created,
    long DurationSeconds,
    DateTimeOffset? Expires,
    string InvokerName,
    int InvokerDatabaseId,
    string InvokerUniqueId,
    int Enforcements);

/// <summary>Complaints on a virtual server.</summary>
/// <param name="Complaints">One entry per complaint.</param>
public sealed record ComplaintList(IReadOnlyList<Complaint> Complaints);

/// <summary>A complaint one client filed about another.</summary>
/// <param name="TargetDatabaseId">The client complained about.</param>
/// <param name="TargetName">Their nickname.</param>
/// <param name="FromDatabaseId">The client who complained.</param>
/// <param name="FromName">Their nickname.</param>
/// <param name="Message">The complaint.</param>
/// <param name="Timestamp">When it was filed.</param>
public sealed record Complaint(
    int TargetDatabaseId,
    string TargetName,
    int FromDatabaseId,
    string FromName,
    string Message,
    DateTimeOffset? Timestamp);

/// <summary>Unused privilege keys.</summary>
/// <param name="Keys">One entry per key.</param>
public sealed record PrivilegeKeyList(IReadOnlyList<PrivilegeKey> Keys);

/// <summary>A privilege key.</summary>
/// <param name="Token">The key a user redeems. A live credential.</param>
/// <param name="GrantsA"><c>server group</c> or <c>channel group</c>.</param>
/// <param name="GroupId">The group it grants.</param>
/// <param name="ChannelId">The channel, for a channel group key.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Description">Its description, if any.</param>
public sealed record PrivilegeKey(
    string Token,
    string GrantsA,
    int GroupId,
    int? ChannelId,
    DateTimeOffset? Created,
    string? Description);