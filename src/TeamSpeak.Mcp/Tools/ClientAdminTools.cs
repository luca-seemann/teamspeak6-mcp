using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that act on people: moving, kicking, poking, messaging and editing them.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class ClientAdminTools(QueryExecutor executor)
{
    /// <summary>The longest kick reason the server accepts.</summary>
    private const int MaxKickReasonLength = 40;

    /// <summary>Moves a connected client.</summary>
    [McpServerTool(Name = "ts_client_move", Title = "Move a client to a channel",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Moves a connected client into a channel. A password-protected channel needs its " +
                 "password. Takes the session id from ts_client_list. Needs Write.")]
    public async Task<ActionResult> MoveAsync(
        [Description("The client's session id, from ts_client_list.")] int clientId,
        [Description("The channel to move the client into.")] int channelId,
        [Description("The channel's password, if it has one.")] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string> { ["clid"] = Text(clientId), ["cid"] = Text(channelId) };
        if (!string.IsNullOrEmpty(channelPassword))
        {
            parameters["cpw"] = channelPassword;
        }

        var records = await executor.RunCommandAsync(
            "ts_client_move", profile, new QueryCommand("clientmove", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return ActionResult.From($"Moved client {clientId} to channel {channelId}.", records);
    }

    /// <summary>Pokes a connected client.</summary>
    [McpServerTool(Name = "ts_client_poke", Title = "Poke a client",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Pokes a connected client: a message that pops up in their TeamSpeak client and " +
                 "usually plays a sound. Use it sparingly. Needs Write.")]
    public async Task<ActionResult> PokeAsync(
        [Description("The client's session id, from ts_client_list.")] int clientId,
        [Description("The message to show.")] string message,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_client_poke",
            profile,
            new QueryCommand(
                "clientpoke",
                new Dictionary<string, string> { ["clid"] = Text(clientId), ["msg"] = RequireText(message, nameof(message)) },
                VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Poked client {clientId}.", records);
    }

    /// <summary>Kicks a connected client.</summary>
    [McpServerTool(Name = "ts_client_kick", Title = "Kick a client",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Kicks a connected client out of their channel, into the default channel, or off the " +
                 "server entirely. The optional reason, at most 40 characters, is shown to them. A server " +
                 "kick does not stop them reconnecting; use ts_ban_add for that. Refuses to kick this " +
                 "server's own query session. Needs Destructive.")]
    public async Task<ActionResult> KickAsync(
        [Description("The client's session id, from ts_client_list.")] int clientId,
        [Description("channel to kick into the default channel, or server to disconnect them.")] string from,
        [Description("A reason shown to the client, at most 40 characters.")] string? reason = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var choice = Choice(from, nameof(from), "channel", "server");
        var parameters = new Dictionary<string, string>
        {
            ["clid"] = Text(clientId),
            ["reasonid"] = choice == "channel" ? "4" : "5",
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var text = reason.Trim();
            if (text.Length > MaxKickReasonLength)
            {
                throw new McpException($"The kick reason may be at most {MaxKickReasonLength} characters; this one has {text.Length}.");
            }

            parameters["reasonmsg"] = text;
        }

        var records = await executor.RunExclusiveAsync(
            "ts_client_kick",
            SafetyLevel.Destructive,
            profile,
            requireSession: false,
            async session =>
            {
                await RefuseOwnSessionAsync(session, clientId, virtualServerId, "kick").ConfigureAwait(false);
                return await session.Send(new QueryCommand("clientkick", parameters, VirtualServerId: virtualServerId)).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Kicked client {clientId} from the {choice}.", records);
    }

    /// <summary>Changes a client's description or talker status.</summary>
    /// <remarks>
    /// Talker status only exists for a client that lacks the talk power its channel needs. Observed on a
    /// live server: granting it to a client with 0 talk power in a channel needing 100 worked, while for a
    /// client that could already speak the server refused <c>client_is_talker=1</c> with
    /// <c>1538 invalid parameter</c>.
    /// </remarks>
    [McpServerTool(Name = "ts_client_edit", Title = "Change a client's description or talker status",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Changes a client's description, which others see in their client info, for a connected " +
                 "client by clientId or for any known identity by databaseId. For a connected client in a " +
                 "moderated channel, one that needs more talk power than the client has, it can also grant " +
                 "or withdraw talker status, which lets the client speak there anyway; the server refuses " +
                 "that for a client that can already speak. A description cannot be cleared to empty, " +
                 "because TeamSpeak treats an empty value as missing. Needs Write.")]
    public async Task<ActionResult> EditAsync(
        [Description("A connected client's session id.")] int? clientId = null,
        [Description("A known identity's database id, whether or not it is online.")] int? databaseId = null,
        [Description("The new description. It cannot be empty.")] string? description = null,
        [Description("For a connected client in a moderated channel: grant (true) or withdraw (false) talker status.")] bool? isTalker = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        if ((clientId is null) == (databaseId is null))
        {
            throw new McpException("Pass exactly one of clientId or databaseId.");
        }

        if (databaseId is not null && isTalker is not null)
        {
            throw new McpException("Talker status belongs to a connected client in its current channel; pass clientId.");
        }

        var parameters = new Dictionary<string, string>();
        if (description is not null)
        {
            parameters["client_description"] = RequireText(description, nameof(description));
        }

        if (isTalker is { } talker)
        {
            parameters["client_is_talker"] = talker ? "1" : "0";
        }

        if (parameters.Count == 0)
        {
            throw new McpException("Pass description, isTalker, or both.");
        }

        QueryCommand command = clientId is { } clid
            ? new("clientedit", new Dictionary<string, string>(parameters) { ["clid"] = Text(clid) }, VirtualServerId: virtualServerId)
            : new("clientdbedit", new Dictionary<string, string>(parameters) { ["cldbid"] = Text(databaseId!.Value) }, VirtualServerId: virtualServerId);

        var records = await executor.RunCommandAsync("ts_client_edit", profile, command, cancellationToken).ConfigureAwait(false);

        return ActionResult.From(clientId is null ? $"Changed identity {databaseId}." : $"Changed client {clientId}.", records);
    }

    /// <summary>Deletes a known identity from the database.</summary>
    [McpServerTool(Name = "ts_clientdb_delete", Title = "Delete a known client identity",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes a client identity from the virtual server's database, with its group " +
                 "memberships, permissions and custom properties. If the person connects again they come " +
                 "back as a new, ungrouped identity. It does not ban them. Needs Destructive.")]
    public async Task<ActionResult> DeleteIdentityAsync(
        [Description("The identity's database id.")] int databaseId,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunCommandAsync(
            "ts_clientdb_delete",
            profile,
            new QueryCommand("clientdbdelete", new Dictionary<string, string> { ["cldbid"] = Text(databaseId) }, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return ActionResult.From($"Deleted identity {databaseId}.", records);
    }

    /// <summary>Sends a text message.</summary>
    [McpServerTool(Name = "ts_message_send", Title = "Send a text message",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sends a text chat message: privately to one connected client, into a channel's chat, " +
                 "into the whole virtual server's chat, or to everyone on every virtual server of the " +
                 "instance. A channel message moves this server's own query session into the channel and " +
                 "back as one uninterrupted step, because TeamSpeak delivers channel messages only to the " +
                 "channel the sender is in. Needs Write.")]
    public async Task<ActionResult> SendMessageAsync(
        [Description("client, channel, server, or instance.")] string target,
        [Description("The message text.")] string message,
        [Description("For client: the recipient's session id.")] int? clientId = null,
        [Description("For channel: the channel.")] int? channelId = null,
        [Description("For channel: the channel's password, if it has one.")] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var choice = Choice(target, nameof(target), "client", "channel", "server", "instance");
        var text = RequireText(message, nameof(message));

        switch (choice)
        {
            case "instance":
                await Run("gm", new() { ["msg"] = text }).ConfigureAwait(false);
                return ActionResult.From("Sent the message to every virtual server.", []);

            case "server":
                var sid = virtualServerId ?? executor.ResolveProfile(profile).DefaultVirtualServerId;
                await Run("sendtextmessage", new() { ["targetmode"] = "3", ["target"] = Text(sid), ["msg"] = text }).ConfigureAwait(false);
                return ActionResult.From($"Sent the message to virtual server {sid}.", []);

            case "client":
                var recipient = clientId ?? throw new McpException("A client message needs clientId.");
                await Run("sendtextmessage", new() { ["targetmode"] = "1", ["target"] = Text(recipient), ["msg"] = text }).ConfigureAwait(false);
                return ActionResult.From($"Sent the message to client {recipient}.", []);

            default:
                var channel = channelId ?? throw new McpException("A channel message needs channelId.");
                var movedBack = await SendToChannelAsync(channel, text, channelPassword, virtualServerId, profile, cancellationToken).ConfigureAwait(false);
                return ActionResult.From(
                    movedBack
                        ? $"Sent the message to channel {channel}."
                        : $"Sent the message to channel {channel}. This server's own session could not move back and has stayed in that channel.",
                    []);
        }

        Task<IReadOnlyList<QueryRecord>> Run(string command, Dictionary<string, string> parameters) =>
            executor.RunCommandAsync("ts_message_send", profile, new QueryCommand(command, parameters, VirtualServerId: virtualServerId), cancellationToken);
    }

    /// <summary>Sends an offline message, or deletes one from this server's inbox.</summary>
    [McpServerTool(Name = "ts_offline_message", Title = "Send or delete an offline message",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("send leaves an offline message for a client identity, which they see the next time they " +
                 "connect; it needs a real client's unique identity, not a query login. delete removes a " +
                 "message from this server's own inbox, as listed by ts_message_list. Needs Write.")]
    public async Task<ActionResult> OfflineMessageAsync(
        [Description("send or delete.")] string action,
        [Description("For send: the recipient's unique identity, from ts_client_resolve.")] string? uniqueId = null,
        [Description("For send: the subject.")] string? subject = null,
        [Description("For send: the message text.")] string? message = null,
        [Description("For delete: the message id.")] int? messageId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var send = Choice(action, nameof(action), "send", "delete") == "send";

        var command = send
            ? new QueryCommand(
                "messageadd",
                new Dictionary<string, string>
                {
                    ["cluid"] = RequireText(uniqueId, nameof(uniqueId)),
                    ["subject"] = RequireText(subject, nameof(subject)),
                    ["message"] = RequireText(message, nameof(message)),
                },
                VirtualServerId: virtualServerId)
            : new QueryCommand(
                "messagedel",
                new Dictionary<string, string> { ["msgid"] = Text(messageId ?? throw new McpException("delete needs messageId.")) },
                VirtualServerId: virtualServerId);

        var records = await executor.RunCommandAsync("ts_offline_message", profile, command, cancellationToken).ConfigureAwait(false);

        return ActionResult.From(send ? "Left the offline message." : $"Deleted message {messageId}.", records);
    }

    /// <summary>
    /// Refuses a kick or ban aimed at this server's own query session, within an exclusive sequence.
    /// </summary>
    /// <param name="session">The sequence.</param>
    /// <param name="clientId">The client about to be kicked or banned.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="verb">What is about to happen, for the message.</param>
    /// <returns>A task that completes when the client is known not to be this session.</returns>
    /// <remarks>
    /// Kicking the session would cut off every tool call on the profile, and banning it would lock
    /// this server out. The check has to share the sequence with the kick itself: a session id is
    /// only meaningful on the virtual server selected at that moment.
    /// </remarks>
    internal static async Task RefuseOwnSessionAsync(SessionSequence session, int clientId, int? virtualServerId, string verb)
    {
        // Selects the virtual server and fails cleanly when there is no such client.
        await session.Send(new QueryCommand(
            "clientinfo",
            new Dictionary<string, string> { ["clid"] = Text(clientId) },
            VirtualServerId: virtualServerId)).ConfigureAwait(false);

        // Without a lasting client of its own there is nothing of this server's to cut off.
        if (!session.HoldsSession)
        {
            return;
        }

        var me = await session.Send(new QueryCommand("whoami")).ConfigureAwait(false);
        if (me.Count > 0 && me[0].GetInt32("client_id") == clientId)
        {
            throw new McpException(
                $"Client {clientId} is this server's own query session on profile '{session.Profile.Name}'. " +
                $"To {verb} it would cut off every tool call on that profile, so nothing was sent.");
        }
    }

    /// <summary>
    /// Moves this server's own session into a channel, sends the message, and moves it back, as one
    /// uninterrupted sequence.
    /// </summary>
    /// <returns><see langword="true"/> unless the session could not move back.</returns>
    private Task<bool> SendToChannelAsync(
        int channelId,
        string text,
        string? channelPassword,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken) =>
        executor.RunExclusiveAsync(
            "ts_message_send",
            SafetyLevel.Write,
            profile,
            requireSession: true,
            async session =>
            {
                // Nothing else runs on the session until this returns, so the virtual server this
                // selects and the channel whoami reports stay true for every step below.
                await session.Send(new QueryCommand(
                    "channelinfo",
                    new Dictionary<string, string> { ["cid"] = Text(channelId) },
                    VirtualServerId: virtualServerId)).ConfigureAwait(false);

                var me = await session.Send(new QueryCommand("whoami")).ConfigureAwait(false);
                if (me.Count == 0)
                {
                    throw new McpException("The server did not say which client this session is; nothing was sent.");
                }

                // Over SSH the channelinfo above has selected the right virtual server. The WebQuery's
                // whoami has no virtual server in its path, so make sure the client it reports is on
                // the one being addressed before moving it anywhere.
                var expectedServer = virtualServerId ?? session.Profile.DefaultVirtualServerId;
                if (me[0].GetInt32("virtualserver_id") != expectedServer)
                {
                    throw new McpException(
                        $"This server's own client is on virtual server {me[0].GetInt32("virtualserver_id")}, not {expectedServer}, " +
                        "so it cannot carry a message into that channel. Nothing was sent.");
                }

                var ownClientId = me[0].GetInt32("client_id");
                var previousChannel = me[0].GetInt32("client_channel_id");
                var moved = previousChannel != channelId;

                if (moved)
                {
                    var move = new Dictionary<string, string> { ["clid"] = Text(ownClientId), ["cid"] = Text(channelId) };
                    if (!string.IsNullOrEmpty(channelPassword))
                    {
                        move["cpw"] = channelPassword;
                    }

                    await session.Send(new QueryCommand("clientmove", move, VirtualServerId: virtualServerId)).ConfigureAwait(false);
                }

                var movedBack = !moved;
                try
                {
                    await session.Send(new QueryCommand(
                        "sendtextmessage",
                        new Dictionary<string, string> { ["targetmode"] = "2", ["target"] = Text(channelId), ["msg"] = text },
                        VirtualServerId: virtualServerId)).ConfigureAwait(false);
                }
                finally
                {
                    // Even when sending failed, the session should not be left where it was moved.
                    if (moved && previousChannel > 0)
                    {
                        try
                        {
                            await session.Send(new QueryCommand(
                                "clientmove",
                                new Dictionary<string, string> { ["clid"] = Text(ownClientId), ["cid"] = Text(previousChannel) },
                                VirtualServerId: virtualServerId)).ConfigureAwait(false);
                            movedBack = true;
                        }
                        catch (McpException)
                        {
                            // Reported through the return value; the message itself was the point.
                        }
                    }
                }

                return movedBack;
            },
            cancellationToken);
}