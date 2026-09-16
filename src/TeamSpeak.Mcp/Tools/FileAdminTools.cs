using System.ComponentModel;
using System.Globalization;
using System.Text;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.FileTransfer;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.FileTransferSupport;
using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools that upload, organise and delete the files stored in channels.</summary>
/// <param name="executor">The shared path to the server.</param>
/// <param name="options">Where uploads may be read from, and how much is accepted inline.</param>
[McpServerToolType]
public sealed class FileAdminTools(QueryExecutor executor, FileTransferOptions options)
{
    private const string SshOnly =
        " File commands need a profile that uses SSH; TeamSpeak refuses them over the WebQuery.";

    /// <summary>Uploads a file.</summary>
    [McpServerTool(Name = "ts_file_upload", Title = "Upload a file",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Uploads a file into a channel's file repository. Give exactly one of content (text, stored " +
                 "as UTF-8), contentBase64, or localPath, a file inside the directory configured as " +
                 "TeamSpeak:FileTransfer:LocalDirectory. Inline content is limited to " +
                 "TeamSpeak:FileTransfer:MaxInlineBytes (100 KiB unless configured). The directory it goes into " +
                 "must exist; ts_file_manage createdir makes one. The stored size is checked afterwards, so a " +
                 "transfer that broke off is reported, not taken for success, and its partial file stays so " +
                 "that resume=true can continue it with the same content. Needs Write; replacing an existing " +
                 "file with overwrite=true, or continuing one with resume=true, needs Destructive. The bytes " +
                 "travel over the server's file transfer port, 30033 by default, which must be reachable from " +
                 "this machine." + SshOnly)]
    public async Task<FileUpload> UploadAsync(
        [Description("The channel to store the file in; 0 for icons and avatars.")] int channelId,
        [Description("Where to store it, such as /docs/readme.txt.")] string path,
        [Description("The file's content as text.")] string? content = null,
        [Description("The file's content, base64-encoded, for binary files.")] string? contentBase64 = null,
        [Description("A local file to upload: a path relative to, or inside, the configured local directory.")] string? localPath = null,
        [Description("Replace a file that already exists at path. Needs Destructive.")] bool overwrite = false,
        [Description("Continue an upload that broke off: only the bytes the partial file at path lacks are sent. Give the same, complete content as before; the last bytes stored are compared with it first. Needs Destructive, since TeamSpeak would extend a finished file just the same. Cannot be combined with overwrite.")] bool resume = false,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = EntryPath(channelId, path, nameof(path));

        if (new[] { content, contentBase64, localPath }.Count(source => source is not null) != 1)
        {
            throw new McpException("Pass exactly one of content, contentBase64 and localPath.");
        }

        if (overwrite && resume)
        {
            // The server refuses the pair with 2056 "overwrite excludes resume".
            throw new McpException("Pass overwrite to replace a file or resume to continue one, not both.");
        }

        // Resuming changes an existing file just as replacing one does. Measured: the server cannot tell a
        // partial file from a finished one, and lengthened a finished file when asked to resume it.
        executor.Demand("ts_file_upload", overwrite || resume ? SafetyLevel.Destructive : SafetyLevel.Write, profile);

        var (inline, local, length) = Source(content, contentBase64, localPath);
        var resolved = executor.ResolveProfile(profile);

        var offset = 0L;
        if (resume)
        {
            // Everything is checked before the upload is started, so a refusal leaves no transfer waiting.
            offset = Math.Max(0, await StoredSizeAsync(channelId, channelPassword, name, virtualServerId, profile, cancellationToken).ConfigureAwait(false));
            if (offset > length)
            {
                throw new McpException(
                    $"Nothing was sent: {name} in channel {channelId} already holds {offset} bytes, more than the {length} " +
                    "given, so it is not an unfinished upload of this content. Replace it with overwrite=true instead.");
            }

            if (offset > 0)
            {
                await RefuseForeignTailAsync(channelId, channelPassword, name, offset, inline, local, virtualServerId, profile, cancellationToken).ConfigureAwait(false);
            }

            if (offset == length)
            {
                return new FileUpload(
                    $"{name} in channel {channelId} already holds all {length} bytes, ending with the same bytes as the content; nothing was sent.",
                    channelId,
                    name,
                    length);
            }
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["clientftfid"] = NextTransferId(), ["name"] = name };
        foreach (var (key, value) in ChannelParameters(channelId, channelPassword))
        {
            parameters[key] = value;
        }

        parameters["size"] = length.ToString(CultureInfo.InvariantCulture);
        parameters["overwrite"] = overwrite ? "1" : "0";
        parameters["resume"] = resume ? "1" : "0";

        var records = await executor.RunCommandAsync(
            "ts_file_upload", profile, new QueryCommand("ftinitupload", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);
        var ticket = Ticket(resolved.Name, "ftinitupload", records);

        // Measured: resume answers with the stored size as seekpos, and the bytes from there on complete
        // the file byte for byte. A fresh upload answers 0. Anything else means the file changed meanwhile.
        if (ticket.SeekPosition != offset)
        {
            await StopQuietlyAsync(ticket, virtualServerId, profile).ConfigureAwait(false);
            throw new McpException(
                $"Nothing was sent: {name} changed while the upload was being prepared; the server expects byte " +
                $"{ticket.SeekPosition}, not {offset}. Try again.");
        }

        try
        {
            var source = inline is not null
                ? (Stream)new MemoryStream(inline, writable: false)
                : new FileStream(local!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            await using (source.ConfigureAwait(false))
            {
                source.Position = offset;
                await FileTransferClient.UploadAsync(
                    ticket,
                    resolved.Host,
                    source,
                    length - offset,
                    cancellationToken,
                    progress: new TransferProgress(progress, offset, length, "Uploaded")).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            var cleanup = await StopQuietlyAsync(ticket, virtualServerId, profile).ConfigureAwait(false);
            throw new McpException($"Uploading {name} to channel {channelId} failed: {ex.Message}{cleanup}", ex);
        }
        catch (OperationCanceledException)
        {
            // Otherwise the transfer would stay listed as waiting until the server drops it minutes later.
            await StopQuietlyAsync(ticket, virtualServerId, profile).ConfigureAwait(false);
            throw;
        }

        var stored = await StoredSizeAsync(channelId, channelPassword, name, virtualServerId, profile, cancellationToken).ConfigureAwait(false);
        if (stored < length)
        {
            // The server closes the connection once it has written the file, but check once more before
            // calling an upload incomplete.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            stored = await StoredSizeAsync(channelId, channelPassword, name, virtualServerId, profile, cancellationToken).ConfigureAwait(false);
        }

        var done = offset > 0
            ? $"Continued {name} in channel {channelId} from byte {offset}; it now holds all {length} bytes."
            : $"Uploaded {length} bytes to {name} in channel {channelId}.";

        return stored == length
            ? new FileUpload(done, channelId, name, length)
            : throw new McpException(
                $"The upload of {name} did not arrive whole: channel {channelId} holds {Math.Max(stored, 0)} of {length} bytes. " +
                "The partial file is still there. Upload the same content again with resume=true to continue it, " +
                "or delete it with ts_file_delete.");
    }

    /// <summary>Creates a directory, renames or moves a file, or stops a transfer.</summary>
    [McpServerTool(Name = "ts_file_manage", Title = "Create a directory, rename a file, or stop a transfer",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("createdir creates the directory path in a channel's file repository; its parent must exist. " +
                 "rename renames or moves a file or directory from path to newPath, into another channel's " +
                 "repository when targetChannelId is given. stop ends a transfer ts_file_transfers lists, by " +
                 "its serverTransferId. These need Write. With deletePartial=true, stop also removes what an " +
                 "unfinished upload left behind, which can be someone else's upload, so that needs Destructive." + SshOnly)]
    public async Task<ActionResult> ManageAsync(
        [Description("createdir, rename, or stop.")] string action,
        [Description("For createdir and rename: the channel.")] int? channelId = null,
        [Description("For createdir: the directory to create. For rename: what to rename, such as /old.txt.")] string? path = null,
        [Description("For rename: the new path, such as /archive/new.txt.")] string? newPath = null,
        [Description("For rename: move into this channel's repository instead.")] int? targetChannelId = null,
        [Description("For rename into another channel: that channel's password, if it has one.")] string? targetChannelPassword = null,
        [Description("For stop: the transfer's serverTransferId from ts_file_transfers.")] int? transferId = null,
        [Description("For stop: also delete the partial file of an unfinished upload.")] bool deletePartial = false,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        QueryCommand command;
        string done;

        switch (Choice(action, nameof(action), "createdir", "rename", "stop"))
        {
            case "stop":
                {
                    var id = transferId ?? throw new McpException("stop needs transferId, the serverTransferId ts_file_transfers shows.");
                    if (deletePartial)
                    {
                        executor.Demand("ts_file_manage stop with deletePartial", SafetyLevel.Destructive, profile);
                    }

                    command = new QueryCommand(
                        "ftstop",
                        new Dictionary<string, string> { ["serverftfid"] = Text(id), ["delete"] = deletePartial ? "1" : "0" },
                        VirtualServerId: virtualServerId);
                    done = deletePartial ? $"Stopped transfer {id} and removed its partial file." : $"Stopped transfer {id}.";
                    break;
                }

            case "createdir":
                {
                    var channel = channelId ?? throw new McpException("createdir needs channelId.");
                    var directory = ServerPath(path, nameof(path), allowRoot: false);
                    var parameters = ChannelParameters(channel, channelPassword);
                    parameters["dirname"] = directory;
                    command = new QueryCommand("ftcreatedir", parameters, VirtualServerId: virtualServerId);
                    done = $"Created the directory {directory} in channel {channel}.";
                    break;
                }

            default:
                {
                    var channel = channelId ?? throw new McpException("rename needs channelId.");
                    var from = EntryPath(channel, path, nameof(path));
                    var to = EntryPath(targetChannelId ?? channel, newPath, nameof(newPath));
                    var parameters = ChannelParameters(channel, channelPassword);

                    var target = targetChannelId is { } other && other != channel ? other : (int?)null;
                    if (target is { } targetChannel)
                    {
                        parameters["tcid"] = Text(targetChannel);
                        parameters["tcpw"] = targetChannelPassword ?? string.Empty;
                    }

                    parameters["oldname"] = from;
                    parameters["newname"] = to;
                    command = new QueryCommand("ftrenamefile", parameters, VirtualServerId: virtualServerId);
                    done = target is { } moved
                        ? $"Moved {from} in channel {channel} to {to} in channel {moved}."
                        : $"Renamed {from} to {to} in channel {channel}.";
                    break;
                }
        }

        var records = await executor.RunCommandAsync("ts_file_manage", profile, command, cancellationToken).ConfigureAwait(false);
        return ActionResult.From(done, records);
    }

    /// <summary>Deletes stored files or directories.</summary>
    [McpServerTool(Name = "ts_file_delete", Title = "Delete stored files",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Deletes files or directories from a channel's file repository. A directory goes with " +
                 "everything in it. The paths are deleted one at a time; the first that fails stops the rest, " +
                 "and the error names what was already deleted. Needs Destructive." + SshOnly)]
    public async Task<ActionResult> DeleteAsync(
        [Description("The channel the files are stored in; 0 for icons and avatars.")] int channelId,
        [Description("The paths to delete, such as [\"/old.txt\", \"/archive\"].")] IReadOnlyList<string> paths,
        [Description(ToolDescriptions.ChannelPassword)] string? channelPassword = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        if (paths is null || paths.Count == 0)
        {
            throw new McpException("Name at least one path to delete.");
        }

        var names = paths.Select(path => EntryPath(channelId, path, nameof(paths))).Distinct(StringComparer.Ordinal).ToList();
        executor.Demand("ts_file_delete", SafetyLevel.Destructive, profile);

        var deleted = new List<string>();
        foreach (var name in names)
        {
            var parameters = ChannelParameters(channelId, channelPassword);
            parameters["name"] = name;

            try
            {
                await executor.RunCommandAsync(
                    "ts_file_delete", profile, new QueryCommand("ftdeletefile", parameters, VirtualServerId: virtualServerId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (McpException ex) when (deleted.Count > 0)
            {
                throw new McpException($"{ex.Message} Deleted before that: {string.Join(", ", deleted)}.", ex);
            }

            deleted.Add(name);
        }

        return new ActionResult($"Deleted {string.Join(", ", deleted)} from channel {channelId}.", new Dictionary<string, string>(), []);
    }

    private (byte[]? Inline, string? Local, long Length) Source(string? content, string? contentBase64, string? localPath)
    {
        if (localPath is not null)
        {
            var local = LocalPath(options, localPath);
            return File.Exists(local)
                ? (null, local, new FileInfo(local).Length)
                : throw new McpException($"There is no file at {local}.");
        }

        byte[] bytes;
        try
        {
            bytes = content is not null ? Encoding.UTF8.GetBytes(content) : Convert.FromBase64String(contentBase64!);
        }
        catch (FormatException)
        {
            throw new McpException("contentBase64 is not valid base64.");
        }

        return bytes.Length <= options.MaxInlineBytes
            ? (bytes, null, bytes.Length)
            : throw new McpException(
                $"The content is {bytes.Length} bytes, more than the {options.MaxInlineBytes} accepted inline. " +
                "Put the file in the configured local directory and pass localPath instead.");
    }

    /// <summary>How much of a partial file is compared with the content before it is continued.</summary>
    private const int TailCheckBytes = 64 * 1024;

    /// <summary>
    /// Refuses to continue a stored file whose last bytes differ from the same bytes of the content,
    /// since appending to it would produce a file that is neither.
    /// </summary>
    private async Task RefuseForeignTailAsync(
        int channelId,
        string? channelPassword,
        string name,
        long offset,
        byte[]? inline,
        string? local,
        int? virtualServerId,
        string? profile,
        CancellationToken cancellationToken)
    {
        var resolved = executor.ResolveProfile(profile);
        var count = (int)Math.Min(offset, TailCheckBytes);
        var start = offset - count;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["clientftfid"] = NextTransferId(), ["name"] = name };
        foreach (var (key, value) in ChannelParameters(channelId, channelPassword))
        {
            parameters[key] = value;
        }

        parameters["seekpos"] = start.ToString(CultureInfo.InvariantCulture);

        var records = await executor.RunCommandAsync(
            "ts_file_upload", profile, new QueryCommand("ftinitdownload", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);
        var ticket = Ticket(resolved.Name, "ftinitdownload", records);

        var stored = new byte[count];
        var expected = new byte[count];
        try
        {
            using (var buffer = new MemoryStream(stored))
            {
                await FileTransferClient.DownloadAsync(ticket, resolved.Host, buffer, count, cancellationToken).ConfigureAwait(false);
            }

            if (inline is not null)
            {
                Array.Copy(inline, start, expected, 0, count);
            }
            else
            {
                var file = new FileStream(local!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    file.Position = start;
                    await file.ReadExactlyAsync(expected, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw new McpException($"Nothing was sent: the stored part of {name} could not be compared with the content: {ex.Message}", ex);
        }

        if (!stored.AsSpan().SequenceEqual(expected))
        {
            throw new McpException(
                $"Nothing was sent: the last {count} bytes stored in {name} differ from the same bytes of the content, " +
                "so the file there is not an unfinished upload of it. Replace it with overwrite=true, or upload under another name.");
        }
    }

    private async Task<long> StoredSizeAsync(int channelId, string? channelPassword, string name, int? virtualServerId, string? profile, CancellationToken cancellationToken)
    {
        var parameters = ChannelParameters(channelId, channelPassword);
        parameters["name"] = name;

        var records = await executor.RunAsync(
            "ts_file_upload", SafetyLevel.Write, profile, new QueryCommand("ftgetfileinfo", parameters, VirtualServerId: virtualServerId), cancellationToken)
            .ConfigureAwait(false);

        return records.Count > 0 ? records[0].GetInt64("size", -1) : -1;
    }

    private async Task<string> StopQuietlyAsync(FileTransferTicket ticket, int? virtualServerId, string? profile)
    {
        try
        {
            // The partial file is kept, so that resume=true can continue it.
            await executor.RunCommandAsync(
                "ts_file_upload",
                profile,
                new QueryCommand(
                    "ftstop",
                    new Dictionary<string, string> { ["serverftfid"] = Text(ticket.ServerTransferId), ["delete"] = "0" },
                    VirtualServerId: virtualServerId),
                CancellationToken.None).ConfigureAwait(false);

            return " The transfer was stopped. What arrived stays as a partial file; upload the same content with " +
                   "resume=true to continue it, or delete it with ts_file_delete.";
        }
        catch (McpException)
        {
            return " The transfer could not be stopped; ts_file_transfers shows whether it is still listed.";
        }
    }
}

/// <summary>A finished upload.</summary>
/// <param name="Done">What was done, in words.</param>
/// <param name="ChannelId">The channel the file is stored in.</param>
/// <param name="Path">Its path there.</param>
/// <param name="Bytes">Its size, as checked on the server.</param>
public sealed record FileUpload(string Done, int ChannelId, string Path, long Bytes);