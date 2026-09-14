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
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(command);

        KnownCrashes.Refuse(command);

        var resolved = ResolveProfile(profile);
        Safety.Demand(resolved, required, action);

        var transport = await TransportForAsync(resolved, cancellationToken).ConfigureAwait(false);

        QueryResponse response;
        try
        {
            response = await transport.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException(
                $"'{command.Name}' did not complete on profile '{resolved.Name}': {ex.Message}", ex);
        }

        return RecordsOf(resolved.Name, command.Name, response);
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
                    KnownCrashes.Refuse(command);
                    Safety.Demand(resolved, CommandCatalog.RequiredLevel(command.Name), action);
                    var response = await send(command, cancellationToken).ConfigureAwait(false);
                    return RecordsOf(resolved.Name, command.Name, response);
                })),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
        {
            throw new McpException($"{action} did not complete on profile '{resolved.Name}': {ex.Message}", ex);
        }
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

    private static IReadOnlyList<QueryRecord> RecordsOf(string profileName, string commandName, QueryResponse response)
    {
        if (response.Error.IsEmptyResult)
        {
            return [];
        }

        if (!response.Error.IsSuccess)
        {
            throw new McpException(DescribeRefusal(profileName, commandName, response.Error));
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
        return RunAsync(action, CommandCatalog.RequiredLevel(command.Name), profile, command, cancellationToken);
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
    /// <returns>The message.</returns>
    public static string DescribeRefusal(string profileName, string commandName, QueryError error)
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

        return $"TeamSpeak refused '{commandName}' on profile '{profileName}' (error {error.Id}: {detail}).{hint}";
    }

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