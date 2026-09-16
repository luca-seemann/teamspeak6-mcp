using System.Diagnostics;

using ModelContextProtocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// Turns the byte counts of a file transfer into MCP progress notifications, a few per second at most.
/// </summary>
/// <remarks>
/// A transfer reports after every 80 KiB chunk, which on a fast link would be thousands of
/// notifications. The last one is always sent, so a client sees the transfer reach its total.
/// </remarks>
public sealed class TransferProgress : IProgress<long>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly IProgress<ProgressNotificationValue>? target;
    private readonly long offset;
    private readonly long total;
    private readonly string verb;
    private readonly Stopwatch sinceLast = Stopwatch.StartNew();

    /// <summary>Creates a reporter for one transfer.</summary>
    /// <param name="target">Where notifications go; <see langword="null"/> when nobody listens.</param>
    /// <param name="offset">Bytes already in place before this transfer, as when resuming.</param>
    /// <param name="total">The whole file's size.</param>
    /// <param name="verb">What is happening, such as "Uploaded".</param>
    public TransferProgress(IProgress<ProgressNotificationValue>? target, long offset, long total, string verb)
    {
        this.target = target;
        this.offset = offset;
        this.total = total;
        this.verb = verb;
    }

    /// <inheritdoc />
    public void Report(long value)
    {
        if (target is null)
        {
            return;
        }

        var done = offset + value;
        if (done < total && sinceLast.Elapsed < Interval)
        {
            return;
        }

        sinceLast.Restart();
        target.Report(new ProgressNotificationValue
        {
            Progress = done,
            Total = total,
            Message = $"{verb} {done} of {total} bytes",
        });
    }
}