using ModelContextProtocol;

using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Safety;

/// <summary>
/// Commands this server refuses to send whatever the safety level, because they are known to crash
/// TeamSpeak and leave a virtual server that cannot be recovered through the query interface.
/// </summary>
/// <remarks>
/// Checked on every path to the server, <c>ts_query_raw</c> included. A safety level decides how much
/// a caller may change; it cannot make a crash acceptable.
/// </remarks>
public static class KnownCrashes
{
    /// <summary>Throws when a command is known to crash the server.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <exception cref="McpException">Thrown for a known crashing command. Nothing has been sent.</exception>
    public static void Refuse(QueryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.Equals(command.Name.Trim(), "serversnapshotdeploy", StringComparison.OrdinalIgnoreCase)
            && command.Options is { } options
            && options.Any(option => string.Equals(option.Trim().TrimStart('-'), "keepfiles", StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpException(
                "serversnapshotdeploy with -keepfiles is refused, and nothing was sent. On TeamSpeak " +
                "6.0.0-beta12.1 it was tried three times: once the deploy hung in 'deploy running', twice " +
                "it crashed the whole server within seconds, the last time sent exactly as documented and " +
                "with a file stored in a channel. Every time the virtual server could no longer be started " +
                "or selected ('VIRTUALSERVER_DEFAULT_SERVER_GROUP points to 0', error 2560), deleting it " +
                "failed with 1281 where that was tried, and only " +
                "wiping the server's database brought it back. Deploy without -keepfiles; channel " +
                "files are not kept.");
        }
    }
}