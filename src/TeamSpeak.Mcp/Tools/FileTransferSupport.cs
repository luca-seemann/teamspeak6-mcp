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
    /// <remarks>
    /// Names are taken exactly as given: a trailing space or a backslash is a legal part of a file name
    /// on the server, so changing either could make a delete or an overwrite hit a different file.
    /// </remarks>
    public static string ServerPath(string? value, string name, bool allowRoot)
    {
        var segments = string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('/', StringSplitOptions.RemoveEmptyEntries);

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

    /// <summary>The directory channel 0 lists the virtual server's icons in.</summary>
    private const string IconDirectory = "/icons";

    /// <summary>
    /// Normalises the path of one file or directory, turning channel 0's listed icon form into the one the
    /// server takes.
    /// </summary>
    /// <param name="channelId">The channel the path is in.</param>
    /// <param name="value">The path as given.</param>
    /// <param name="name">The argument name, for messages.</param>
    /// <returns>The path the server takes.</returns>
    /// <remarks>
    /// Measured on 6.0.0-beta12.1: an upload to <c>/icon_123</c> in channel 0 is listed as
    /// <c>/icons/icon_123</c>, but <c>ftgetfileinfo</c>, <c>ftinitdownload</c> and <c>ftdeletefile</c> refuse
    /// that form with <c>1538 invalid parameter</c> and take only <c>/icon_123</c>.
    /// </remarks>
    public static string EntryPath(int channelId, string? value, string name)
    {
        var path = ServerPath(value, name, allowRoot: false);
        return channelId == 0 && path.StartsWith(IconDirectory + "/", StringComparison.Ordinal) && path.LastIndexOf('/') == IconDirectory.Length
            ? path[IconDirectory.Length..]
            : path;
    }

    /// <summary>The path of an entry from a listing, in the form the server and the other file tools take.</summary>
    public static string ListedPath(int channelId, string directory, string entry) =>
        channelId == 0 && directory == IconDirectory ? "/" + entry : Join(directory, entry);

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
    /// <remarks>
    /// The check is on the path as written and on every part of it that exists: a symbolic link or
    /// junction inside the directory is refused, because it could lead anywhere.
    /// </remarks>
    public static string LocalPath(FileTransferOptions options, string localPath)
    {
        var root = LocalRoot(options);

        if (string.IsNullOrWhiteSpace(localPath) || localPath.Contains('\0', StringComparison.Ordinal))
        {
            throw new McpException("localPath must name a file, such as 'exports/logo.png'.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, localPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"localPath '{localPath}' is not a usable path: {ex.Message}", ex);
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison))
        {
            throw new McpException(
                $"localPath '{localPath}' lies outside {root}, the directory file transfers may use. " +
                "Give a path inside it, or relative to it.");
        }

        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists)
            {
                break;
            }

            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new McpException(
                    $"localPath '{localPath}' passes through the link {current}, which could lead outside {root}. " +
                    "Links inside the directory are not followed.");
            }
        }

        return full;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Gets the configured local directory, checked to be usable.</summary>
    /// <param name="options">The file transfer settings.</param>
    /// <returns>The full path, without a trailing separator.</returns>
    public static string LocalRoot(FileTransferOptions options)
    {
        var configured = options.LocalDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new McpException(
                "localPath is switched off on this server. Pass the content inline, or configure " +
                "TeamSpeak:FileTransfer:LocalDirectory as the directory file transfers may use.");
        }

        // A relative setting would move with the working directory, which differs between a terminal,
        // a service and a container.
        if (!Path.IsPathFullyQualified(configured))
        {
            throw new McpException($"TeamSpeak:FileTransfer:LocalDirectory must be an absolute path, not '{configured}'.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        if (string.Equals(Path.GetPathRoot(root), root, PathComparison))
        {
            throw new McpException($"TeamSpeak:FileTransfer:LocalDirectory cannot be the root of a drive or file system ('{root}').");
        }

        return Directory.Exists(root)
            ? root
            : throw new McpException($"TeamSpeak:FileTransfer:LocalDirectory '{root}' does not exist.");
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