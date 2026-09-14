using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// The commands of one uninterrupted sequence, as <see cref="QueryExecutor.RunExclusiveAsync{T}"/>
/// hands them to a tool.
/// </summary>
/// <param name="Profile">The profile the sequence runs on.</param>
/// <param name="HoldsSession">Whether the interface holds a session, so this server is itself a client on the server.</param>
/// <param name="Send">Sends one command and returns its records, refusing it like any other tool call would.</param>
public sealed record SessionSequence(
    QueryProfile Profile,
    bool HoldsSession,
    Func<QueryCommand, Task<IReadOnlyList<QueryRecord>>> Send);