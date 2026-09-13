namespace TeamSpeak.Query.Protocol;

/// <summary>
/// A single ServerQuery command together with its parameters and options.
/// </summary>
/// <remarks>
/// The same instance can be issued over any <see cref="Transport.IQueryTransport"/>: the SSH transport
/// serialises it to the line-based query protocol, the HTTP transport maps it onto a WebQuery request.
/// </remarks>
/// <param name="Name">The command name, for example <c>clientlist</c>.</param>
/// <param name="Parameters">Key/value parameters, for example <c>cid=1</c>.</param>
/// <param name="Options">Flag-style options passed with a leading dash, for example <c>-uid</c>.</param>
public sealed record QueryCommand(
    string Name,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<string>? Options = null);