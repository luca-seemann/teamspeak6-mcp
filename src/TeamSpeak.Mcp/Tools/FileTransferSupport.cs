using System.Globalization;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.FileTransfer;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Paths, local files and transfer tickets, shared by the file tools.</summary>
internal static class FileTransferSupport
{
    private static int s_lastTransferId;

    /// <summary>Chooses a <c>clientftfid</c>. The server only echoes it, so it only has to differ between transfers.</summary>
    public static string NextTransferId() =>
        ((Interlocked.Increment(ref s_lastTransferId) & 0x7FFF) + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Normalises a path inside a channel's file repository to the <c>/dir/name</c> form the server takes.</summary>
    /// <param name="value">The path as given.</param>
    /// <param name="name">The argument name, for messages.</param>
    /// <param name="allowRoot">Whether the top level itself is acceptable, as for listing.</param>
    /// <returns>The path, starting with <c>/</c> and without a trailing one.</returns>
    public static string ServerPath(string? value, string name, bool allowRoot)
    {
        var segments = (value ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new McpException($"'{name}' cannot contain '.' or '..'; give the path from the top of the channel's files, such as /docs/readme.txt.");
        }

        if (segments.Length == 0 && !allowRoot)
        {
            throw new McpException($"'{name}' must name a file or directory, such as /docs/readme.txt.");
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>Joins a directory and an entry name from its listing.</summary>
    public static string Join(string directory, string entry) =>
        directory.EndsWith('/') ? directory + entry : $"{directory}/{entry}";

    /// <summary>The parameters every channel file command starts with.</summary>
    public static Dictionary<string, string> ChannelParameters(int channelId, string? password) =>
        new(StringComparer.Ordinal)
        {
            ["cid"] = channelId.ToString(CultureInfo.InvariantCulture),

            // Always sent, as the reference does: an empty cpw is accepted where other empty values are not.
            ["cpw"] = password ?? string.Empty,
        };

    /// <summary>Resolves a local path inside the configured directory, refusing anything outside it.</summary>
    /// <param name="options">The file transfer settings.</param>
    /// <param name="localPath">The path as given, relative to the directory or absolute inside it.</param>
    /// <returns>The full path.</returns>
    public static string LocalPath(FileTransferOptions options, string localPath)
    {
        if (string.IsNullOrWhiteSpace(options.LocalDirectory))
        {
            throw new McpException(
                "localPath is switched off on this server. Pass the content inline, or configure " +
                "TeamSpeak:FileTransfer:LocalDirectory as the directory file transfers may use.");
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            throw new McpException("localPath must not be empty.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.LocalDirectory));
        var full = Path.GetFullPath(Path.Combine(root, localPath.Trim()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            ? full
            : throw new McpException(
                $"localPath '{localPath}' lies outside {root}, the directory file transfers may use. " +
                "Give a path inside it, or relative to it.");
    }

    /// <summary>Reads the ticket from an init command's answer, explaining a refusal inside the record.</summary>
    public static FileTransferTicket Ticket(string profileName, string commandName, IReadOnlyList<QueryRecord> records)
    {
        if (records.Count == 0)
        {
            throw new McpException($"'{commandName}' returned no transfer on profile '{profileName}'.");
        }

        try
        {
            return FileTransferTicket.FromRecord(records[0]);
        }
        catch (FileTransferRefusedException ex)
        {
            throw new McpException(QueryExecutor.DescribeRefusal(profileName, commandName, ex.ToQueryError()), ex);
        }
        catch (QueryProtocolException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
