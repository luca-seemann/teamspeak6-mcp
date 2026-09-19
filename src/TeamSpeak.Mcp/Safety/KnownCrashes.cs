using ModelContextProtocol;

using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Safety;

/// <summary>
/// Commands this server refuses to send whatever the safety level, because they are known to leave a
/// TeamSpeak server, or one of its virtual servers, in a state the query interface cannot recover.
/// </summary>
/// <remarks>
/// Checked on every path to the server, <c>ts_query_raw</c> included. A safety level decides how much
/// a caller may change; it cannot make a wrecked server acceptable. Both entries here are bugs of
/// particular server versions, so each names the version that fixes it and is refused below that;
/// <see cref="NeedsServerVersion"/> says when the version has to be read from the server first.
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
    /// The first server version in which <c>serverstop</c> can be trusted to finish, or
    /// <see langword="null"/> while no such version is known.
    /// </summary>
    /// <remarks>
    /// On 6.0.0-beta13 a stop hung five times out of seven and left the virtual server in
    /// <c>shutting down</c> until the whole server process was restarted. TeamSpeak confirmed the bug
    /// on 19 September 2026 and announced a hotfix, saying that no file transfer is needed to trigger
    /// it: "just trying to stop the server was causing trouble". Until that hotfix names a version,
    /// there is none this can clear, so every stop is refused. Setting this to the fixed version
    /// narrows the refusal to the releases that still have the bug, the way
    /// <see cref="KeepFilesFixedIn"/> does.
    /// </remarks>
    public static ServerVersion? StopFixedIn { get; }

    /// <summary>
    /// Says whether deciding on this command needs the version the server reports.
    /// </summary>
    /// <param name="command">The command about to be sent.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="Refuse"/> decides by version, so the caller should ask
    /// the server for it first.
    /// </returns>
    public static bool NeedsServerVersion(QueryCommand command) => KeepsFiles(command) || StopsVirtualServer(command);

    /// <summary>Throws when a command is known to wreck the server it is aimed at.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <param name="serverVersion">
    /// What the server answers to <c>version</c>, or <see langword="null"/> when it is not known. A
    /// command whose safety depends on the version is refused while it is unknown.
    /// </param>
    /// <exception cref="McpException">Thrown for a refused command. Nothing has been sent.</exception>
    public static void Refuse(QueryCommand command, ServerVersion? serverVersion = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (KeepsFiles(command) && !(serverVersion >= KeepFilesFixedIn))
        {
            throw new McpException(
                "serversnapshotdeploy with -keepfiles is refused, and nothing was sent. On TeamSpeak " +
                "6.0.0-beta12.1 it was tried three times: once the deploy hung in 'deploy running', twice " +
                "it crashed the whole server within seconds, the last time sent exactly as documented and " +
                "with a file stored in a channel. Every time the virtual server could no longer be started " +
                "or selected ('VIRTUALSERVER_DEFAULT_SERVER_GROUP points to 0', error 2560), deleting it " +
                "failed with 1281 where that was tried, and only wiping the server's database brought it " +
                $"back. It is fixed in {KeepFilesFixedIn}, and this server reports " +
                $"{Reported(serverVersion)}. Deploy without -keepfiles on this server; channel files are " +
                "not kept.");
        }

        if (StopsVirtualServer(command) && !(StopFixedIn is { } fixedIn && serverVersion >= fixedIn))
        {
            throw new McpException(
                "serverstop is refused, and nothing was sent. On TeamSpeak 6.0.0-beta13 a stop hung five " +
                "times out of seven: the command either answered ok or never answered at all, and either " +
                "way the virtual server stayed in 'shutting down' for good. It could then not be selected " +
                "(1035) or started again (2816), no file transfer had to be involved, and only restarting " +
                "the whole TeamSpeak server process brought it back. TeamSpeak confirmed the bug on " +
                "19 September 2026 and announced a hotfix, so far without naming the version that carries " +
                "it: https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376 " +
                $"This server reports {Reported(serverVersion)}, and no version is known in which the bug " +
                "is gone, so every stop is refused. What does still work: a snapshot deploy restarts a " +
                "virtual server from the inside, and stopping the whole instance takes its virtual servers " +
                "with it. Tell the user that stopping a single virtual server needs access to the " +
                "TeamSpeak host until the hotfix is out.");
        }
    }

    private static string Reported(ServerVersion? serverVersion) =>
        serverVersion is { } version ? version.Text : "no version";

    private static bool KeepsFiles(QueryCommand command) =>
        Is(command, "serversnapshotdeploy")
        && command.Options is { } options
        && options.Any(option => string.Equals(option.Trim().TrimStart('-'), "keepfiles", StringComparison.OrdinalIgnoreCase));

    private static bool StopsVirtualServer(QueryCommand command) => Is(command, "serverstop");

    private static bool Is(QueryCommand command, string name) =>
        string.Equals(command.Name.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
