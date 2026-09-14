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

        var resolved = ResolveProfile(profile);
        Safety.Demand(resolved, required, action);

        IQueryTransport transport;
        try
        {
            transport = await Connections.GetTransportAsync(resolved.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException(
                $"Could not connect to profile '{resolved.Name}' at {resolved.Host}: {ex.Message}", ex);
        }

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

        if (response.Error.IsEmptyResult)
        {
            return [];
        }

        if (!response.Error.IsSuccess)
        {
            throw new McpException(DescribeRefusal(resolved.Name, command.Name, response.Error));
        }

        return response.Records;
    }

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
            QueryErrorCode.OutOfScope =>
                " The profile's WebQuery API key does not cover this command. A key created with " +
                "scope=read or scope=write is limited; create one with 'apikeyadd scope=manage' over " +
                "SSH, or give the profile an SSH password. Events are never available over the WebQuery.",
            QueryErrorCode.UnknownCommand =>
                " The server does not recognise the command or one of its parameters.",
            QueryErrorCode.ParameterNotFound =>
                " A required parameter is missing.",
            QueryErrorCode.Flooding =>
                " The server is still throttling this client after the requested wait. Wait before " +
                "trying again; sending on escalates to an IP-level block.",
            _ => string.Empty,
        };

        return $"TeamSpeak refused '{commandName}' on profile '{profileName}' (error {error.Id}: {detail}).{hint}";
    }

    /// <summary>Copies a record into a plain dictionary for structured tool output.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The fields.</returns>
    public static IReadOnlyDictionary<string, string> ToFields(QueryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
    }
}