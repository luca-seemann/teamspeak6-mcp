using System.ComponentModel;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for the server logs.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class LogTools(QueryExecutor executor)
{
    /// <summary>Reads recent log entries.</summary>
    /// <param name="lines">How many entries.</param>
    /// <param name="instance">Whether to read the instance log.</param>
    /// <param name="beforePosition">Where an earlier page started.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, oldest first.</returns>
    [McpServerTool(Name = "ts_log_view", Title = "Read the server log",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Reads the most recent entries of a virtual server's log, or of the instance log, " +
                 "oldest first: timestamp, level, source and message. Connections, kicks, bans, " +
                 "permission and group changes and query activity all appear here. For older entries, " +
                 "pass the previous page's position back as beforePosition.")]
    public async Task<LogPage> ViewLogAsync(
        [Description("How many entries to return, from 1 to 100.")] int lines = 50,
        [Description("Read the instance log instead of the virtual server's.")] bool instance = false,
        [Description("The position a previous page reported, to read the entries before it.")] long? beforePosition = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["lines"] = Text(Math.Clamp(lines, 1, 100)),
            ["instance"] = instance ? "1" : "0",
        };

        if (beforePosition is { } position)
        {
            parameters["begin_pos"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var records = await executor.RunAsync(
            "ts_log_view",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("logview", parameters, VirtualServerId: virtualServerId),
            cancellationToken).ConfigureAwait(false);

        // The position and file size ride on the first record only; every record carries one line.
        return new LogPage(
            records.Count > 0 ? records[0].GetInt64("last_pos") : 0,
            records.Count > 0 ? records[0].GetInt64("file_size") : 0,
            records.Select(record => ParseLine(record.GetString("l"))).ToList());
    }

    /// <summary>Splits a log line into its columns.</summary>
    /// <param name="line">A line such as <c>2026-09-14 08:05:53.383146|INFO    |VirtualServer |1  |message</c>.</param>
    /// <returns>The entry; a line that does not have five columns is kept whole as the message.</returns>
    public static LogEntry ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var parts = line.Split('|', 5);
        return parts.Length == 5
            ? new LogEntry(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim(), parts[4].Trim())
            : new LogEntry(string.Empty, string.Empty, string.Empty, string.Empty, line);
    }
}

/// <summary>A page of log entries.</summary>
/// <param name="Position">Where this page starts; pass it as beforePosition for the page before.</param>
/// <param name="FileSize">The size of the log file.</param>
/// <param name="Entries">The entries, oldest first.</param>
public sealed record LogPage(long Position, long FileSize, IReadOnlyList<LogEntry> Entries);

/// <summary>A log entry.</summary>
/// <param name="Timestamp">When it was written, in the server's local time.</param>
/// <param name="Level">The level, for example <c>INFO</c>.</param>
/// <param name="Source">The component that wrote it.</param>
/// <param name="VirtualServer">The virtual server id, empty for instance entries.</param>
/// <param name="Message">The message.</param>
public sealed record LogEntry(string Timestamp, string Level, string Source, string VirtualServer, string Message);