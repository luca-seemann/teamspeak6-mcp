using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// The one path every tool takes to the server: resolve the profile, check safety, send, and turn
/// failures into messages a model can act on.
/// </summary>
/// <remarks>
/// Keeping this in one place means no tool can forget the safety check, open its own connection,
/// or hand a raw exception to the client.
/// </remarks>
public sealed class QueryExecutor
{
    /// <summary>Initialises an executor.</summary>
    /// <param name="connections">The shared per-profile connections.</param>
    /// <param name="safety">The safety policy to enforce.</param>
    public QueryExecutor(QueryConnectionManager connections, SafetyPolicy safety)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(safety);

        Connections = connections;
        Safety = safety;
    }

    /// <summary>Gets the shared per-profile connections.</summary>
    public QueryConnectionManager Connections { get; }

    /// <summary>Gets the safety policy.</summary>
    public SafetyPolicy Safety { get; }

    /// <summary>Resolves a profile, reporting a bad name as a tool error.</summary>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="McpException">Thrown when the profile cannot be resolved.</exception>
    public QueryProfile ResolveProfile(string? profile)
    {
        try
        {
            return Connections.Profiles.Resolve(profile);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>Checks safety, sends one command and returns its records.</summary>
    /// <param name="action">What is being attempted, used in refusal messages, usually the tool name.</param>
    /// <param name="required">The safety level the command needs.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records; empty when the server reports an empty result set.</returns>
    /// <exception cref="McpException">
    /// Thrown when the call is not allowed, cannot reach the server, or the server refuses it.
    /// </exception>
    public async Task<IReadOnlyList<QueryRecord>> RunAsync(
        string action,
        SafetyLevel required,
        string? profile,
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        var response = await RunForResponseAsync(action, required, profile, command, cancellationToken).ConfigureAwait(false);
        return response.Error.IsEmptyResult ? [] : response.Records;
    }

    /// <summary>
    /// Checks safety, sends one command and returns the whole response, for commands whose answer is
    /// prose rather than records.
    /// </summary>
    /// <param name="action">What is being attempted, used in refusal messages, usually the tool name.</param>
    /// <param name="required">The safety level the command needs.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The response, including its <see cref="QueryResponse.Text"/>.</returns>
    /// <exception cref="McpException">
    /// Thrown when the call is not allowed, cannot reach the server, or the server refuses it.
    /// </exception>
    public async Task<QueryResponse> RunForResponseAsync(
        string action,
        SafetyLevel required,
        string? profile,
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        var (resolved, response) = await SendUncheckedAsync(action, required, profile, command, cancellationToken).ConfigureAwait(false);

        return response.Error.IsSuccess || response.Error.IsEmptyResult
            ? response
            : throw new McpException(DescribeRefusal(resolved.Name, command.Name, response.Error, resolved.IsGuest));
    }

    /// <summary>
    /// Checks safety, sends a search and returns its records, reading the status the server sends when
    /// nothing matches as no records.
    /// </summary>
    /// <param name="action">What is being attempted, used in refusal messages, usually the tool name.</param>
    /// <param name="required">The safety level the command needs.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="command">The search to send.</param>
    /// <param name="noMatchCode">The status this search answers with when nothing matches.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records; empty when nothing matched.</returns>
    /// <exception cref="McpException">
    /// Thrown when the call is not allowed, cannot reach the server, or the server refuses it for any
    /// other reason.
    /// </exception>
    /// <remarks>
    /// Measured on 6.0.0-beta12.1 over both interfaces: <c>channelfind</c> answers
    /// <see cref="QueryErrorCode.InvalidChannelId"/> and <c>clientfind</c>
    /// <see cref="QueryErrorCode.InvalidClientId"/> when nothing matches. Elsewhere those codes mean a
    /// wrong id, so they are read as "nothing found" only for the search that names them.
    /// </remarks>
    public async Task<IReadOnlyList<QueryRecord>> RunSearchAsync(
        string action,
        SafetyLevel required,
        string? profile,
        QueryCommand command,
        int noMatchCode,
        CancellationToken cancellationToken)
    {
        var (resolved, response) = await SendUncheckedAsync(action, required, profile, command, cancellationToken).ConfigureAwait(false);

        if (response.Error.Id == noMatchCode || response.Error.IsEmptyResult)
        {
            return [];
        }

        return response.Error.IsSuccess
            ? response.Records
            : throw new McpException(DescribeRefusal(resolved.Name, command.Name, response.Error, resolved.IsGuest));
    }

    /// <summary>
    /// Checks safety, sends one command and returns whatever the server answered, refusal included.
    /// </summary>
    /// <param name="action">What is being attempted, usually the tool name.</param>
    /// <param name="required">The safety level the command needs.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The response, successful or not.</returns>
    /// <exception cref="McpException">
    /// Thrown when the call is not allowed or the server cannot be reached. A refusal by the server
    /// is returned rather than thrown.
    /// </exception>
    /// <remarks>
    /// For the few tools whose answer is the status itself, such as asking permission by permission
    /// what this session may do, where "you may not even ask" is a result worth reporting rather than
    /// an error to abort on. Everywhere else a refusal should become an <see cref="McpException"/>
    /// through <see cref="RunAsync"/>.
    /// </remarks>
    public async Task<QueryResponse> RunForStatusAsync(
        string action,
        SafetyLevel required,
        string? profile,
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        var (_, response) = await SendUncheckedAsync(action, required, profile, command, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>Checks safety and sends one command, leaving the server's status to the caller.</summary>
    private async Task<(QueryProfile Profile, QueryResponse Response)> SendUncheckedAsync(
        string action,
        SafetyLevel required,
        string? profile,
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(command);

        var resolved = ResolveProfile(profile);

        // Never less than the command itself needs, so a level set too low in a tool cannot under-protect.
        var catalogLevel = CommandCatalog.RequiredLevel(command);
        Safety.Demand(resolved, catalogLevel > required ? catalogLevel : required, action);

        var transport = await TransportForAsync(resolved, cancellationToken).ConfigureAwait(false);

        await RefuseKnownCrashesAsync(command, transport.SendAsync, cancellationToken).ConfigureAwait(false);

        try
        {
            return (resolved, await transport.SendAsync(command, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException(
                $"'{command.Name}' did not complete on profile '{resolved.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs a sequence of commands that must not be interleaved with other calls on the same session.
    /// </summary>
    /// <typeparam name="T">What the sequence produces.</typeparam>
    /// <param name="action">What is being attempted, usually the tool name.</param>
    /// <param name="required">The highest level any command of the sequence needs; checked before anything is sent.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="requireSession">Refuse profiles whose interface holds no session, for sequences that act as this server's own client.</param>
    /// <param name="work">The sequence.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What <paramref name="work"/> returns.</returns>
    /// <exception cref="McpException">
    /// Thrown when the call is not allowed, cannot reach the server, needs a session the profile lacks,
    /// or the server refuses one of the commands.
    /// </exception>
    /// <remarks>
    /// Each command in the sequence is still checked against the command catalog, so a sequence can
    /// never send more than its profile allows even if <paramref name="required"/> was set too low.
    /// </remarks>
    public async Task<T> RunExclusiveAsync<T>(
        string action,
        SafetyLevel required,
        string? profile,
        bool requireSession,
        Func<SessionSequence, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(work);

        var resolved = ResolveProfile(profile);
        Safety.Demand(resolved, required, action);

        var transport = await TransportForAsync(resolved, cancellationToken).ConfigureAwait(false);

        if (requireSession && !transport.HoldsSession)
        {
            throw new McpException(
                $"{action} has to act as this server's own query client, and the interface profile " +
                $"'{resolved.Name}' uses keeps no such client between commands. Nothing was sent.");
        }

        try
        {
            return await transport.RunExclusiveAsync(
                send => work(new SessionSequence(resolved, transport.HoldsSession, async command =>
                {
                    Safety.Demand(resolved, CommandCatalog.RequiredLevel(command), action);
                    await RefuseKnownCrashesAsync(command, send, cancellationToken).ConfigureAwait(false);
                    var response = await send(command, cancellationToken).ConfigureAwait(false);
                    return RecordsOf(resolved, command.Name, response);
                })),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
        {
            throw new McpException($"{action} did not complete on profile '{resolved.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Refuses a command known to wreck the server, first asking the server whatever the refusal
    /// depends on: which version it runs, what it is still busy with.
    /// </summary>
    /// <param name="command">The command about to be sent.</param>
    /// <param name="send">Sends on the connection the command itself would use.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <exception cref="McpException">Thrown when the command is refused. It has not been sent.</exception>
    /// <remarks>
    /// The extra round trip is paid only by the handful of commands that can wreck a server, all of
    /// them rare and heavyweight, so nothing here is worth caching and going stale over. A fact that
    /// cannot be read leaves the command refused.
    /// </remarks>
    private static async Task RefuseKnownCrashesAsync(
        QueryCommand command,
        QuerySender send,
        CancellationToken cancellationToken)
    {
        if (!KnownCrashes.NeedsServerVersion(command))
        {
            KnownCrashes.Refuse(command);
            return;
        }

        var response = await send(new QueryCommand("version"), cancellationToken).ConfigureAwait(false);

        KnownCrashes.Refuse(
            command,
            response.Error.IsSuccess && response.Records.Count > 0
                ? ServerVersion.TryParse(response.Records[0].GetString("version"))
                : null);
    }

    private async Task<IQueryTransport> TransportForAsync(QueryProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            return await Connections.GetTransportAsync(profile.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException(
                $"Could not connect to profile '{profile.Name}' at {profile.Host}: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<QueryRecord> RecordsOf(QueryProfile profile, string commandName, QueryResponse response)
    {
        if (response.Error.IsEmptyResult)
        {
            return [];
        }

        if (!response.Error.IsSuccess)
        {
            throw new McpException(DescribeRefusal(profile.Name, commandName, response.Error, profile.IsGuest));
        }

        return response.Records;
    }

    /// <summary>Sends one command at the safety level the command catalog gives it.</summary>
    /// <param name="action">What is being attempted, used in refusal messages, usually the tool name.</param>
    /// <param name="profile">The profile name, or <see langword="null"/> when only one is configured.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records; empty when the server reports an empty result set.</returns>
    /// <remarks>
    /// Every changing tool goes through here, so a tool can never ask for less than the catalog
    /// demands for the command it actually sends.
    /// </remarks>
    public Task<IReadOnlyList<QueryRecord>> RunCommandAsync(
        string action,
        string? profile,
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(action, CommandCatalog.RequiredLevel(command), profile, command, cancellationToken);
    }

    /// <summary>Refuses a multi-step action up front, before its first command changes anything.</summary>
    /// <param name="action">What is being attempted.</param>
    /// <param name="required">The highest level any of its commands needs.</param>
    /// <param name="profile">The profile name.</param>
    /// <exception cref="McpException">Thrown when the profile does not allow the action.</exception>
    public void Demand(string action, SafetyLevel required, string? profile) =>
        Safety.Demand(ResolveProfile(profile), required, action);

    /// <summary>Explains a server-side refusal, with a hint where the cause is known.</summary>
    /// <param name="profileName">The profile the command ran on.</param>
    /// <param name="commandName">The refused command.</param>
    /// <param name="error">The server's status.</param>
    /// <param name="guest">
    /// Whether the profile connects as the ServerQuery guest, which changes what a refusal means:
    /// it is the server's guest group that is too small, not this profile's login or key.
    /// </param>
    /// <returns>The message.</returns>
    public static string DescribeRefusal(string profileName, string commandName, QueryError error, bool guest = false)
    {
        ArgumentNullException.ThrowIfNull(error);

        var detail = string.IsNullOrWhiteSpace(error.ExtraMessage)
            ? error.Message
            : $"{error.Message}, {error.ExtraMessage}";

        var hint = error.Id switch
        {
            QueryErrorCode.OutOfScope when commandName.StartsWith("ft", StringComparison.OrdinalIgnoreCase) =>
                " TeamSpeak refuses every file command over the WebQuery, whatever the API key's scope. " +
                "Give the profile an SSH password; with Transport Auto it then uses SSH.",
            QueryErrorCode.InvalidFileName =>
                " The name is not a file, for example it is a directory; ts_file_list shows what is there.",
            QueryErrorCode.FileAlreadyExists =>
                " Something by that name already exists. An upload replaces a file only with overwrite=true.",
            QueryErrorCode.FileNotFound =>
                " There is no file by that name; ts_file_list shows what is there.",
            QueryErrorCode.FileIoError when commandName.Equals("logview", StringComparison.OrdinalIgnoreCase) =>
                " The virtual server has no log file yet, which is normal when nothing has been logged " +
                "since the server process started; what it logs at all is set by its virtualserver_log_* " +
                "settings. The instance log is a separate file and usually has entries: read it with " +
                "instance set. ts_log_add writes an entry, which creates the file.",
            QueryErrorCode.InvalidChannelPassword =>
                " The channel has a password; pass it as channelPassword.",
            QueryErrorCode.OverwriteExcludesResume =>
                " An upload either replaces a file (overwrite) or continues one (resume), not both.",
            QueryErrorCode.InsufficientPermissions =>
                " The profile's query login lacks the permission the message names.",
            QueryErrorCode.AccessToDefaultGroupForbidden =>
                " This is a default group, the one clients have when they have no other. Clients belong to it " +
                "without being added, so it has no member list and nobody can be added to or removed from it. " +
                "ts_vserver_info shows the default groups as virtualserver_default_server_group and " +
                "virtualserver_default_channel_group.",
            QueryErrorCode.InvalidFilePath =>
                " The path does not exist, or a directory on the way to it is missing; ts_file_manage " +
                "createdir creates directories.",
            QueryErrorCode.OutOfScope =>
                " The profile's WebQuery API key does not cover this command. A key created with " +
                "scope=read or scope=write is limited; create one with 'apikeyadd scope=manage' over " +
                "SSH, or give the profile an SSH password. Events are never available over the WebQuery.",
            QueryErrorCode.UnknownCommand =>
                " The server does not recognise the command or one of its parameters.",
            QueryErrorCode.ParameterNotFound =>
                " A required parameter is missing.",
            QueryErrorCode.InvalidParameterSize =>
                " A value is too long for the server, for example a group name over 30 characters or a " +
                "long query login name. Shorten it and try again.",
            QueryErrorCode.MissingRequiredParameter =>
                " A required parameter is missing or empty; TeamSpeak treats an empty value as absent.",
            QueryErrorCode.Flooding =>
                " The server is still throttling this client after the requested wait. Wait before " +
                "trying again; sending on escalates to an IP-level block.",
            _ => string.Empty,
        };

        // A guest is refused for a reason of its own, so its hint replaces the one about logins and keys.
        if (guest && GuestHint(error.Id) is { } guestHint)
        {
            hint = guestHint;
        }

        return $"TeamSpeak refused '{commandName}' on profile '{profileName}' (error {error.Id}: {detail}).{hint}";
    }

    /// <summary>Explains a refusal that is about being the ServerQuery guest, not about a login.</summary>
    /// <param name="errorId">The server's status.</param>
    /// <returns>The hint, or <see langword="null"/> when the status has nothing to do with being a guest.</returns>
    private static string? GuestHint(int errorId) => errorId switch
    {
        QueryErrorCode.InsufficientPermissions =>
            " This profile connects as the ServerQuery guest, which holds only what the TeamSpeak server's " +
            "'Guest Server Query' group grants, by default next to nothing. Either give the profile a " +
            "password or an API key, or have the server's administrator grant that group the permission " +
            "the message names.",
        QueryErrorCode.OutOfScope =>
            " This profile connects as the ServerQuery guest, and TeamSpeak does not allow guests this " +
            "command at all, whatever the guest group may hold otherwise. Give the profile a password or " +
            "an API key.",
        _ => null,
    };

    /// <summary>Copies a record into a plain dictionary for structured tool output.</summary>
    /// <remarks>Kept next to <see cref="SessionSequence"/>'s users for discoverability.</remarks>
    /// <param name="record">The record.</param>
    /// <returns>The fields.</returns>
    public static IReadOnlyDictionary<string, string> ToFields(QueryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
    }
}