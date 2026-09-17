using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that change bans, complaints, privilege keys, custom properties, keys, logins and the log.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ModerationAdminTools(QueryExecutor executor)
{
    /// <summary>Bans a client or adds a ban rule.</summary>
    [McpServerTool(Name = "ts_ban_add", Title = "Ban someone",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Bans someone. Pass clientId to ban a connected client, which disconnects them and creates " +
                 "separate rules for their identity, their myTeamSpeak id and their IP address as the server " +
                 "sees it. Behind NAT or Docker port publishing many people can share that address, and the " +
                 "IP rule then locks out everyone connecting through it until it is lifted with " +
                 "ts_ban_delete. Or add a rule matching an ip, a nickname pattern (name), a " +
                 "uniqueId or a myTeamSpeakId, which also works for people who are offline. durationSeconds " +
                 "0 or omitted bans permanently. Returns the new ban ids; ts_ban_delete lifts a ban. Needs " +
                 "Destructive.")]
    public async Task<ActionResult> AddBanAsync(
        [Description("A connected client's session id. Cannot be combined with the rule fields.")] int? clientId = null,
        [Description("An IP address, or a regular expression matching addresses.")] string? ip = null,
        [Description("A regular expression matching nicknames.")] string? name = null,
        [Description("A unique identity.")] string? uniqueId = null,
        [Description("A myTeamSpeak id.")] string? myTeamSpeakId = null,
        [Description("How long the ban lasts, in seconds. 0 or omitted means permanently.")] int durationSeconds = 0,
        [Description("The reason, shown to the banned client.")] string? reason = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var rule = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfGiven(rule, "ip", ip);
        AddIfGiven(rule, "name", name);
        AddIfGiven(rule, "uid", uniqueId);
        AddIfGiven(rule, "mytsid", myTeamSpeakId);

        if ((clientId is null) == (rule.Count == 0))
        {
            throw new McpException("Pass either clientId, or at least one of ip, name, uniqueId and myTeamSpeakId, but not both.");
        }

        var parameters = clientId is { } clid ? new Dictionary<string, string> { ["clid"] = Text(clid) } : rule;

        if (durationSeconds < 0)
        {
            throw new McpException("durationSeconds cannot be negative; use 0 for a permanent ban.");
        }

        if (durationSeconds > 0)
        {
            parameters["time"] = Text(durationSeconds);
        }

        AddIfGiven(parameters, "banreason", reason);

        var records = clientId is { } target
            ? await executor.RunExclusiveAsync(
                "ts_ban_add",
                SafetyLevel.Destructive,
                profile,
                requireSession: false,
                async session =>
                {
                    await ClientAdminTools.RefuseOwnSessionAsync(session, target, virtualServerId, "ban").ConfigureAwait(false);
                    return await session.Send(new QueryCommand("banclient", parameters, VirtualServerId: virtualServerId)).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false)
            : await executor.RunCommandAsync(
                "ts_ban_add", profile, new QueryCommand("banadd", parameters, VirtualServerId: virtualServerId), cancellationToken)
                .ConfigureAwait(false);

        var ids = string.Join(", ", records.Select(record => record.GetString("banid")).Where(id => id.Length > 0));
        return ActionResult.From($"Added ban {ids}{(durationSeconds > 0 ? $" for {durationSeconds} seconds" : ", permanently")}.", records);
    }

    /// <summary>Lifts a ban.</summary>
    [McpServerTool(Name = "ts_ban_delete", Title = "Lift a ban",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lifts one ban rule by its id from ts_ban_list. Banning a client creates several rules, one " +
                 "each for the IP address and the identity; lift all of them to let the person back. Needs " +
                 "Write.")]
    public async Task<ActionResult> DeleteBanAsync(
        [Description("The ban id, from ts_ban_list.")] int banId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_ban_delete", profile, new QueryCommand("bandel", new Dictionary<string, string> { ["banid"] = Text(banId) }, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Lifted ban {banId}.", records);
    }

    /// <summary>Deletes a complaint.</summary>
    [McpServerTool(Name = "ts_complaint_delete", Title = "Delete a complaint",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes one complaint, identified by who it is about and who filed it, as ts_complaint_list " +
                 "shows. Needs Write.")]
    public async Task<ActionResult> DeleteComplaintAsync(
        [Description("The database id of the client the complaint is about.")] int targetDatabaseId,
        [Description("The database id of the client who filed it.")] int fromDatabaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_complaint_delete",
            profile,
            new QueryCommand(
                "complaindel",
                new Dictionary<string, string> { ["tcldbid"] = Text(targetDatabaseId), ["fcldbid"] = Text(fromDatabaseId) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Deleted the complaint by {fromDatabaseId} about {targetDatabaseId}.", records);
    }

    /// <summary>Creates or deletes a privilege key.</summary>
    [McpServerTool(Name = "ts_token_manage", Title = "Create or delete a privilege key",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("add creates a privilege key: a one-time code that puts whoever redeems it into a server " +
                 "group, or into a channel group in one channel. Hand it only to the intended person. add " +
                 "needs Destructive, because it hands out access. delete invalidates an unused key and needs " +
                 "Write.")]
    public async Task<ActionResult> ManageTokenAsync(
        [Description("add or delete.")][AllowedValues("add", "delete")] string action,
        [Description("For add: the server group the key grants.")] int? serverGroupId = null,
        [Description("For add: the channel group the key grants, together with channelId.")] int? channelGroupId = null,
        [Description("For add with channelGroupId: the channel.")] int? channelId = null,
        [Description("For add: what the key is for.")] string? description = null,
        [Description("For delete: the key.")] string? token = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var add = Choice(action, nameof(action), "add", "delete") == "add";

        Dictionary<string, string> parameters;
        if (add)
        {
            parameters = (serverGroupId, channelGroupId, channelId) switch
            {
                ({ } sgid, null, null) => new() { ["tokentype"] = "0", ["tokenid1"] = Text(sgid), ["tokenid2"] = "0" },
                (null, { } cgid, { } cid) => new() { ["tokentype"] = "1", ["tokenid1"] = Text(cgid), ["tokenid2"] = Text(cid) },
                _ => throw new McpException("add needs serverGroupId, or channelGroupId together with channelId."),
            };

            AddIfGiven(parameters, "tokendescription", description);
        }
        else
        {
            parameters = new() { ["token"] = RequireText(token, nameof(token)) };
        }

        var records = await executor.RunCommandAsync(
            "ts_token_manage",
            profile,
            new QueryCommand(add ? "privilegekeyadd" : "privilegekeydelete", parameters, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From(add ? "Created a privilege key; it is in details as token." : "Deleted the privilege key.", records);
    }

    /// <summary>Sets or deletes a custom property.</summary>
    [McpServerTool(Name = "ts_custom_property", Title = "Set or delete a custom property",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sets a custom property on a client identity, such as a linked forum account, or deletes " +
                 "one. Integrations use these to tie TeamSpeak identities to other systems; ts_custom_search " +
                 "finds them again. Needs Write.")]
    public async Task<ActionResult> CustomPropertyAsync(
        [Description("set or delete.")][AllowedValues("set", "delete")] string action,
        [Description("The client's database id.")] int databaseId,
        [Description("The property identifier, for example 'forum_account'.")] string ident,
        [Description("For set: the value.")] string? value = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var set = Choice(action, nameof(action), "set", "delete") == "set";
        var parameters = new Dictionary<string, string> { ["cldbid"] = Text(databaseId), ["ident"] = RequireText(ident, nameof(ident)) };

        if (set)
        {
            parameters["value"] = RequireText(value, nameof(value));
        }

        var records = await executor.RunCommandAsync(
            "ts_custom_property", profile, new QueryCommand(set ? "customset" : "customdelete", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From(set ? $"Set {parameters["ident"]} on identity {databaseId}." : $"Deleted {parameters["ident"]} from identity {databaseId}.", records);
    }

    /// <summary>Writes an entry into the log.</summary>
    [McpServerTool(Name = "ts_log_add", Title = "Write to the server log",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Writes an entry into a virtual server's log, for example to record why an administrative " +
                 "change was made. Needs Write.")]
    public async Task<ActionResult> AddLogAsync(
        [Description("The entry text.")] string message,
        [Description("info (the default), warning, error, or debug.")][AllowedValues("info", "warning", "error", "debug")] string level = "info",
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var levelId = Choice(level, nameof(level), "info", "warning", "error", "debug") switch
        {
            "error" => "1",
            "warning" => "2",
            "debug" => "3",
            _ => "4",
        };

        var records = await executor.RunCommandAsync(
            "ts_log_add",
            profile,
            new QueryCommand("logadd", new Dictionary<string, string> { ["loglevel"] = levelId, ["logmsg"] = RequireText(message, nameof(message)) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From("Wrote the log entry.", records);
    }

    /// <summary>Creates or deletes a WebQuery API key.</summary>
    [McpServerTool(Name = "ts_apikey_manage", Title = "Create or delete a WebQuery API key",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("add creates a WebQuery API key with a scope (read, write or manage) and a lifetime in days, " +
                 "for this query login or another identity; the key itself is returned once and never shown " +
                 "again. delete removes a key by the id ts_apikey_list shows; deleting the key a profile of " +
                 "this server uses cuts that profile off, and confirmName must repeat the owner's name: the " +
                 "identity's nickname, or the login name for a key of this query login. Both need Destructive.")]
    public async Task<ActionResult> ManageApiKeyAsync(
        [Description("add or delete.")][AllowedValues("add", "delete")] string action,
        [Description("For add: read, write, or manage.")][AllowedValues("read", "write", "manage")] string? scope = null,
        [Description("For add: lifetime in days. 0 means the key never expires. Omitted, the server's default of 14 days.")] int? lifetimeDays = null,
        [Description("For add: the identity to own the key. Omit for this query login.")] int? databaseId = null,
        [Description("For delete: the key id.")] int? keyId = null,
        [Description("For delete: the key owner's nickname, or this query login's name for its own keys.")] string? confirmName = null,
        [Description("The virtual server the key belongs to. " + ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var add = Choice(action, nameof(action), "add", "delete") == "add";

        Dictionary<string, string> parameters;
        if (add)
        {
            parameters = new() { ["scope"] = Choice(scope, nameof(scope), "read", "write", "manage") };

            if (lifetimeDays is { } days)
            {
                parameters["lifetime"] = Text(days < 0 ? throw new McpException("lifetimeDays cannot be negative.") : days);
            }

            if (databaseId is { } owner)
            {
                parameters["cldbid"] = Text(owner);
            }
        }
        else
        {
            parameters = new() { ["id"] = Text(keyId ?? throw new McpException("delete needs keyId.")) };
        }

        var command = new QueryCommand(add ? "apikeyadd" : "apikeydel", parameters, VirtualServerId: virtualServerId);
        await new DeletionTargets(executor).ConfirmAsync("ts_apikey_manage", profile, command, confirmName, cancellationToken).ConfigureAwait(false);

        var records = await executor.RunCommandAsync("ts_apikey_manage", profile, command, cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From(add ? "Created the API key; details holds it, and it will not be shown again." : $"Deleted API key {keyId}.", records);
    }

    /// <summary>Creates or deletes a query login.</summary>
    [McpServerTool(Name = "ts_querylogin_manage", Title = "Create or delete a query login",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("add enables ServerQuery login for an existing client identity on this virtual server, " +
                 "under a login name, and returns its generated password once. delete removes an " +
                 "identity's query login, and confirmName must repeat its login name. Query logins act with " +
                 "that identity's permissions, so both need Destructive.")]
    public async Task<ActionResult> ManageQueryLoginAsync(
        [Description("add or delete.")][AllowedValues("add", "delete")] string action,
        [Description("The identity's database id.")] int databaseId,
        [Description("For add: the login name.")] string? loginName = null,
        [Description("For delete: the login name to remove, exactly as ts_querylogin_list shows it.")] string? confirmName = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var add = Choice(action, nameof(action), "add", "delete") == "add";

        var parameters = new Dictionary<string, string> { ["cldbid"] = Text(databaseId) };
        if (add)
        {
            parameters["client_login_name"] = RequireText(loginName, nameof(loginName));
        }

        var command = new QueryCommand(add ? "queryloginadd" : "querylogindel", parameters, VirtualServerId: virtualServerId);
        await new DeletionTargets(executor).ConfirmAsync("ts_querylogin_manage", profile, command, confirmName, cancellationToken).ConfigureAwait(false);

        var records = await executor.RunCommandAsync("ts_querylogin_manage", profile, command, cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From(add ? "Created the query login; details holds its password, shown only this once." : $"Deleted the query login of identity {databaseId}.", records);
    }

    private static void AddIfGiven(Dictionary<string, string> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value.Trim();
        }
    }
}