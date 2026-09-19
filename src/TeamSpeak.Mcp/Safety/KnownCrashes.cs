using ModelContextProtocol;

using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Safety;

/// <summary>
/// Commands this server refuses to send whatever the safety level, because they are known to leave a
/// TeamSpeak server, or one of its virtual servers, in a state the query interface cannot recover.
/// </summary>
/// <remarks>
/// Checked on every path to the server, <c>ts_query_raw</c> included. A safety level decides how much
/// a caller may change; it cannot make a wrecked server acceptable. Some of these depend on
/// something only the server can answer — which version it runs, what it is busy with — so
/// <see cref="NeedsServerFacts"/> says when to ask before deciding.
/// </remarks>
public static class KnownCrashes
{
    /// <summary>
    /// The first server version in which <c>serversnapshotdeploy -keepfiles</c> stopped crashing.
    /// </summary>
    /// <remarks>
    /// Verified on 18 September 2026 against 6.0.0-beta13: the deploy that had killed 6.0.0-beta12.1
    /// three times answered <c>error id=0</c> in 0.3 seconds, the instance stayed up, and the virtual
    /// server came back online with a valid default server group.
    /// </remarks>
    public static ServerVersion KeepFilesFixedIn { get; } = ServerVersion.Parse("6.0.0-beta13");

    /// <summary>
    /// Says whether refusing this command needs answers from the server itself.
    /// </summary>
    /// <param name="command">The command about to be sent.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="Refuse"/> decides on <see cref="ServerFacts"/> the
    /// caller has to read from the server first.
    /// </returns>
    public static bool NeedsServerFacts(QueryCommand command) => KeepsFiles(command) || StopsVirtualServer(command);

    /// <summary>Says whether deciding on this command needs the server's version.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <returns><see langword="true"/> when <see cref="ServerFacts.Version"/> is consulted.</returns>
    public static bool NeedsServerVersion(QueryCommand command) => KeepsFiles(command);

    /// <summary>Says whether deciding on this command needs the virtual server's pending transfers.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <returns><see langword="true"/> when <see cref="ServerFacts.PendingTransfers"/> is consulted.</returns>
    public static bool NeedsPendingTransfers(QueryCommand command) => StopsVirtualServer(command);

    /// <summary>Throws when a command is known to wreck the server it is aimed at.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <param name="facts">
    /// What the server answered about itself, or <see langword="null"/> when nothing was asked. A
    /// command whose safety depends on a fact that could not be read is refused.
    /// </param>
    /// <exception cref="McpException">Thrown for a refused command. Nothing has been sent.</exception>
    public static void Refuse(QueryCommand command, ServerFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (KeepsFiles(command) && !(facts?.Version >= KeepFilesFixedIn))
        {
            throw new McpException(
                "serversnapshotdeploy with -keepfiles is refused, and nothing was sent. On TeamSpeak " +
                "6.0.0-beta12.1 it was tried three times: once the deploy hung in 'deploy running', twice " +
                "it crashed the whole server within seconds, the last time sent exactly as documented and " +
                "with a file stored in a channel. Every time the virtual server could no longer be started " +
                "or selected ('VIRTUALSERVER_DEFAULT_SERVER_GROUP points to 0', error 2560), deleting it " +
                "failed with 1281 where that was tried, and only wiping the server's database brought it " +
                $"back. It is fixed in {KeepFilesFixedIn}, and this server reports " +
                $"{(facts?.Version is { } version ? version.Text : "no version")}. Deploy without -keepfiles " +
                "on this server; channel files are not kept.");
        }

        if (StopsVirtualServer(command) && facts?.PendingTransfers is not 0)
        {
            var pending = facts?.PendingTransfers is { } count
                ? $"{count} file transfer(s) are pending on it"
                : "this server could not read its file transfers, so it cannot tell whether any are pending";

            throw new McpException(
                $"serverstop is refused, and nothing was sent: {pending}. Measured on TeamSpeak " +
                "6.0.0-beta13: with a single transfer waiting — an upload ticket nobody connected to was " +
                "enough — the stop never answered, the virtual server stayed 'shutting down' for good, " +
                "'use' on it returned 1035 and starting it again 2816, and only restarting the whole " +
                "server process brought it back. On a quiet virtual server the same stop finishes in under " +
                "a second. Use ts_file_transfers to see what is running, ts_file_manage stop to end a " +
                "transfer, or wait: an unused ticket lapses after about two minutes, an upload that broke " +
                "off after about thirty seconds.");
        }
    }

    private static bool KeepsFiles(QueryCommand command) =>
        Is(command, "serversnapshotdeploy")
        && command.Options is { } options
        && options.Any(option => string.Equals(option.Trim().TrimStart('-'), "keepfiles", StringComparison.OrdinalIgnoreCase));

    private static bool StopsVirtualServer(QueryCommand command) => Is(command, "serverstop");

    private static bool Is(QueryCommand command, string name) =>
        string.Equals(command.Name.Trim(), name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the server said about itself, read just before a command that could wreck it.
/// </summary>
/// <param name="Version">
/// What it answers to <c>version</c>, or <see langword="null"/> when that could not be read.
/// </param>
/// <param name="PendingTransfers">
/// How many file transfers are running or waiting on the virtual server the command addresses, or
/// <see langword="null"/> when that could not be read.
/// </param>
public sealed record ServerFacts(ServerVersion? Version = null, int? PendingTransfers = null);
