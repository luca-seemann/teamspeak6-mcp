using System.ComponentModel;
using System.Text;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.FileTransfer;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.FileTransferSupport;
using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that read the files stored in channels, and the transfers under way.</summary>
/// <param name="executor">The shared path to the server.</param>
/// <param name="options">Where downloads may be saved, and how much comes back inline.</param>
/// <remarks>
/// TeamSpeak 6.0.0-beta12.1 refuses every file command over the WebQuery, so these tools work on
/// profiles that use SSH.
/// </remarks>
[McpServerToolType]
public sealed class FileTools(QueryExecutor executor, FileTransferOptions options)
{
    private const string SshOnly =
        " File commands need a profile that uses SSH; TeamSpeak refuses them over the WebQuery.";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Lists one directory of a channel's files.</summary>
    [McpServerTool(Name = "ts_file_list", Title = "List a channel's files",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the files and directories in one directory of a channel's file repository: name, " +
                 "size, and when each last changed. An upload that is still running or broke off shows " +
                 "incompleteSize, the size it is meant to reach. Channel 0 holds the virtual server's " +
                 "icons and avatars." + SshOnly)]
    public async Task<FileList> ListFilesAsync(
        [Description("The channel whose files to list; 0 for the virtual server's icons and avatars.")] int channelId,
        [Description("The directory, such as /screenshots. Omit it for the top level.")] string? path = null,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var directory = ServerPath(path, nameof(path), allowRoot: true);
        var parameters = ChannelParameters(channelId, channelPassword);
        parameters["path"] = directory;

        var records = await executor.RunAsync(
            "ts_file_list", SafetyLevel.ReadOnly, profile, new QueryCommand("ftgetfilelist", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return new FileList(
            channelId,
            directory,
            records
                .Select(record =>
                {
                    var name = record.GetString("name");
                    return new FileEntry(
                        name,
                        ListedPath(channelId, directory, name),
                        record.GetInt32("type", 1) == 0 ? "directory" : "file",
                        record.GetInt64("size"),
                        record.GetUnixTimeOfAnyPrecision("datetime"),
                        record.ContainsKey("incompletesize") ? record.GetInt64("incompletesize") : null);
                })
                .ToList());
    }

    /// <summary>Shows one stored file.</summary>
    [McpServerTool(Name = "ts_file_info", Title = "Show a stored file",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows the size of one file in a channel's file repository and when it last changed. It " +
                 "takes files only; ts_file_list shows directories." + SshOnly)]
    public async Task<FileDetails> FileInfoAsync(
        [Description("The channel the file is stored in; 0 for icons and avatars.")] int channelId,
        [Description("The file's path, such as /docs/readme.txt.")] string path,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var name = EntryPath(channelId, path, nameof(path));
        var parameters = ChannelParameters(channelId, channelPassword);
        parameters["name"] = name;

        var records = await executor.RunAsync(
            "ts_file_info", SafetyLevel.ReadOnly, profile, new QueryCommand("ftgetfileinfo", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return records.Count == 0
            ? throw new McpException($"There is no file {name} in channel {channelId}.")
            : new FileDetails(channelId, name, records[0].GetInt64("size"), records[0].GetUnixTimeOfAnyPrecision("datetime"));
    }

    /// <summary>Lists the transfers running or waiting.</summary>
    [McpServerTool(Name = "ts_file_transfers", Title = "List file transfers",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the file transfers running or waiting on a virtual server: which client, which file, " +
                 "how many bytes have moved, and how fast. A transfer that was started but never connected " +
                 "waits here until the server drops it a few minutes later. ts_file_manage with action stop " +
                 "ends one." + SshOnly)]
    public async Task<FileTransferList> ListTransfersAsync(
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_file_transfers", SafetyLevel.ReadOnly, profile, new QueryCommand("ftlist", VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return new FileTransferList(records
            .Select(record => new RunningTransfer(
                record.GetInt32("serverftfid"),
                record.GetInt32("clientftfid"),
                record.GetInt32("clid"),
                record.GetInt32("sender") == 1 ? "download" : "upload",
                record.GetInt32("status") switch
                {
                    0 => "waiting",
                    1 => "running",
                    var other => $"status {other}",
                },
                record.GetString("name"),
                record.GetString("path"),
                record.GetInt64("size"),
                record.GetInt64("sizedone"),
                record.GetDouble("current_speed"),
                record.GetDouble("average_speed"),
                record.GetInt64("runtime")))
            .ToList());
    }

    /// <summary>Downloads a stored file.</summary>
    [McpServerTool(Name = "ts_file_download", Title = "Download a stored file",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Downloads a file from a channel's file repository. Without localPath the content comes back " +
                 "in the answer, as text when it is valid UTF-8 and as base64 otherwise, for files up to " +
                 "TeamSpeak:FileTransfer:MaxInlineBytes (100 KiB unless configured); this needs ReadOnly. With " +
                 "localPath it is saved inside the directory configured as TeamSpeak:FileTransfer:LocalDirectory, " +
                 "which needs Write, and an existing local file is never replaced. The bytes travel over the " +
                 "server's file transfer port, 30033 by default, which must be reachable from this machine." + SshOnly)]
    public async Task<FileDownload> DownloadAsync(
        [Description("The channel the file is stored in; 0 for icons and avatars.")] int channelId,
        [Description("The file's path, such as /docs/readme.txt.")] string path,
        [Description("Save the file here instead of returning it: a path relative to, or inside, the configured local directory.")] string? localPath = null,
        [Description("With localPath: continue a download that broke off from the part it left behind (localPath + \".partial\"), fetching only the missing bytes.")] bool resume = false,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var name = EntryPath(channelId, path, nameof(path));

        if (resume && localPath is null)
        {
            throw new McpException("resume needs localPath: it continues a partial local file, and an inline download is simply repeated.");
        }

        // Nothing changes on the server either way, but saving writes a file on this machine.
        if (localPath is not null)
        {
            executor.Demand("ts_file_download with localPath", SafetyLevel.Write, profile);
        }

        var target = localPath is null ? null : LocalPath(options, localPath);

        if (target is not null && (File.Exists(target) || Directory.Exists(target)))
        {
            throw new McpException($"Nothing was downloaded: {target} already exists, and a download never replaces a local file.");
        }

        // A partial file this call did not make may be someone's own file or another download under way.
        if (target is not null && !resume && File.Exists(target + ".partial"))
        {
            throw new McpException(
                $"Nothing was downloaded: {target}.partial already exists. If it is what a broken download of this " +
                "file left, pass resume=true to continue it; otherwise choose another localPath.");
        }

        var resolved = executor.ResolveProfile(profile);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["clientftfid"] = NextTransferId(), ["name"] = name };
        foreach (var (key, value) in ChannelParameters(channelId, channelPassword))
        {
            parameters[key] = value;
        }

        // Measured: seekpos makes the server send the file from that byte on, while size stays the whole file's.
        var offset = resume && target is not null && File.Exists(target + ".partial") ? new FileInfo(target + ".partial").Length : 0;
        parameters["seekpos"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var records = await executor.RunCommandAsync(
            "ts_file_download", profile, new QueryCommand("ftinitdownload", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);
        var ticket = Ticket(resolved.Name, "ftinitdownload", records);

        if (offset > ticket.Size)
        {
            throw new McpException(
                $"Nothing was downloaded: {target}.partial holds {offset} bytes, more than the {ticket.Size} of {name}, so it " +
                "belongs to another file. Download without resume to start over.");
        }

        if (target is null && ticket.Size > options.MaxInlineBytes)
        {
            throw new McpException(
                $"{name} is {ticket.Size} bytes, more than the {options.MaxInlineBytes} returned inline. " +
                "Pass localPath to save it to a file instead.");
        }

        try
        {
            if (target is null)
            {
                using var buffer = new MemoryStream((int)ticket.Size);
                await FileTransferClient.DownloadAsync(ticket, resolved.Host, buffer, ticket.Size, cancellationToken).ConfigureAwait(false);

                var bytes = buffer.ToArray();
                return AsText(bytes) is { } text
                    ? new FileDownload(channelId, name, bytes.Length, "text", text, null)
                    : new FileDownload(channelId, name, bytes.Length, "base64", Convert.ToBase64String(bytes), null);
            }

            await SaveAsync(ticket, resolved.Host, target, offset, cancellationToken).ConfigureAwait(false);
            return new FileDownload(channelId, name, ticket.Size, "file", null, target, offset > 0 ? offset : null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            var kept = target is not null && File.Exists(target + ".partial")
                ? $" What arrived is kept in {target}.partial; call again with resume=true to continue."
                : string.Empty;
            throw new McpException($"Downloading {name} from channel {channelId} failed: {ex.Message}{kept}", ex);
        }
    }

    private static async Task SaveAsync(FileTransferTicket ticket, string host, string target, long offset, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Written under another name first, so a transfer that breaks off leaves no file that looks complete,
        // and what arrived stays there for resume to continue. A fresh download never opens an existing one.
        var partial = target + ".partial";
        var file = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            await FileTransferClient.DownloadAsync(ticket, host, file, ticket.Size - offset, cancellationToken).ConfigureAwait(false);
        }

        File.Move(partial, target, overwrite: false);
    }

    private static string? AsText(byte[] bytes)
    {
        if (bytes.AsSpan().Contains((byte)0))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>One directory of a channel's files.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Directory">The directory listed.</param>
/// <param name="Entries">Its files and directories; empty when it holds nothing.</param>
public sealed record FileList(int ChannelId, string Directory, IReadOnlyList<FileEntry> Entries);

/// <summary>A file or directory in a listing.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its full path, as the other file tools take it.</param>
/// <param name="Type"><c>file</c> or <c>directory</c>.</param>
/// <param name="Size">Its size in bytes; for an unfinished upload, what has arrived.</param>
/// <param name="Modified">When it last changed.</param>
/// <param name="IncompleteSize">For an upload still running or broken off, the size it is meant to reach.</param>
public sealed record FileEntry(string Name, string Path, string Type, long Size, DateTimeOffset? Modified, long? IncompleteSize);

/// <summary>One stored file.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Path">The file's path.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Modified">When it last changed.</param>
public sealed record FileDetails(int ChannelId, string Path, long Size, DateTimeOffset? Modified);

/// <summary>The file transfers on a virtual server.</summary>
/// <param name="Transfers">One entry per transfer.</param>
public sealed record FileTransferList(IReadOnlyList<RunningTransfer> Transfers);

/// <summary>A file transfer running or waiting.</summary>
/// <param name="ServerTransferId">The id ts_file_manage stop takes.</param>
/// <param name="ClientTransferId">The id the client chose.</param>
/// <param name="ClientId">The session id of the client transferring.</param>
/// <param name="Direction"><c>upload</c> or <c>download</c>.</param>
/// <param name="State"><c>waiting</c> until the client connects, then <c>running</c>.</param>
/// <param name="Name">The file's name.</param>
/// <param name="ServerDirectory">Where the server stores it, relative to its own data directory.</param>
/// <param name="Size">The file's full size in bytes.</param>
/// <param name="BytesDone">How many bytes have moved.</param>
/// <param name="CurrentBytesPerSecond">The current speed.</param>
/// <param name="AverageBytesPerSecond">The average speed.</param>
/// <param name="RuntimeSeconds">How long it has been running.</param>
public sealed record RunningTransfer(
    int ServerTransferId,
    int ClientTransferId,
    int ClientId,
    string Direction,
    string State,
    string Name,
    string ServerDirectory,
    long Size,
    long BytesDone,
    double CurrentBytesPerSecond,
    double AverageBytesPerSecond,
    long RuntimeSeconds);

/// <summary>A downloaded file.</summary>
/// <param name="ChannelId">The channel it came from.</param>
/// <param name="Path">Its path there.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Format"><c>text</c> or <c>base64</c> when the content is in the answer, <c>file</c> when it was saved.</param>
/// <param name="Content">The content, for <c>text</c> and <c>base64</c>.</param>
/// <param name="SavedTo">Where it was saved, for <c>file</c>.</param>
/// <param name="ResumedFrom">For a resumed download, the byte it continued from.</param>
public sealed record FileDownload(int ChannelId, string Path, long Size, string Format, string? Content, string? SavedTo, long? ResumedFrom = null);