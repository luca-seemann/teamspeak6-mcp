namespace TeamSpeak.Query.Protocol;

/// <summary>
/// A decoded ServerQuery response: zero or more records plus the terminating status.
/// </summary>
/// <remarks>
/// Both transports normalise onto this shape. A single-record command such as <c>serverinfo</c> yields
/// one entry in <see cref="Records"/>; a list command such as <c>clientlist</c> yields one entry per item.
/// </remarks>
/// <param name="Records">The returned records, each a set of key/value pairs.</param>
/// <param name="Error">The terminating status of the command.</param>
public sealed record QueryResponse(
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records,
    QueryError Error);