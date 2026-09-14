namespace TeamSpeak.Query.Protocol;

/// <summary>
/// A single ServerQuery command together with its parameters and options.
/// </summary>
/// <remarks>
/// <para>
/// The same instance can be issued over any <see cref="Transport.IQueryTransport"/>: the SSH transport
/// serialises it to the line-based query protocol, the HTTP transport maps it onto a WebQuery request.
/// </para>
/// <para>
/// The virtual server travels with the command rather than living on the connection. SSH selects a
/// virtual server with a stateful <c>use</c>, and a connection shared by concurrent tool calls would
/// otherwise let one call's selection leak into another's command.
/// </para>
/// </remarks>
/// <param name="Name">The command name, for example <c>clientlist</c>.</param>
/// <param name="Parameters">Key/value parameters, for example <c>cid=1</c>.</param>
/// <param name="Options">Flag-style options passed with a leading dash, for example <c>-uid</c>.</param>
/// <param name="VirtualServerId">
/// The virtual server the command addresses, or <see langword="null"/> for the profile's default.
/// Ignored for instance-wide commands; see <see cref="QueryCommandScope"/>.
/// </param>
/// <param name="Timeout">
/// How long to wait for the response, or <see langword="null"/> for the profile's command timeout.
/// Only for commands known to run long: a snapshot deploy abandoned after the default timeout left
/// a live server stuck in <c>deploy running</c>.
/// </param>
public sealed record QueryCommand(
    string Name,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<string>? Options = null,
    int? VirtualServerId = null,
    TimeSpan? Timeout = null);