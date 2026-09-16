using System.Net.Sockets;
using System.Text;

namespace TeamSpeak.Query.FileTransfer;

/// <summary>
/// Moves the bytes of one file transfer over TeamSpeak's file transfer interface.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 6.0.0-beta12.1, this is the only way files move. <c>ftgetchannelfilehttptoken</c>, the
/// HTTP alternative the reference describes, answers <c>2 not implemented</c>, and the WebQuery
/// refuses every <c>ft*</c> command with <c>5120 out of scope</c> even with a <c>manage</c> key, so the
/// ticket has to come from the SSH interface.
/// </para>
/// <para>
/// The protocol is minimal: connect, write the ticket's key, then write or read the raw bytes. There
/// is no framing and no acknowledgement; the server closes the connection when it has what it
/// expects, or when it has sent the last byte. A connection with an unknown key is closed at once.
/// Whether an upload arrived whole can therefore only be told from the file's size afterwards.
/// </para>
/// <para>
/// Measured on a stall: the server closed an upload that sent nothing for between 16 and 30 seconds,
/// keeping what had arrived. A steady 20 KB/s completed. The stall timeout here is set just above that.
/// </para>
/// </remarks>
public static class FileTransferClient
{
    /// <summary>How long a transfer may go without moving a byte before it is abandoned.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private const int ChunkSize = 81920;

    /// <summary>Uploads bytes for a ticket from <c>ftinitupload</c>.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="host">The server's host, tried before any addresses the ticket names.</param>
    /// <param name="source">Where the bytes come from, positioned at the first byte to send.</param>
    /// <param name="length">How many bytes to send: the size announced to <c>ftinitupload</c>, less the ticket's seek position.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <param name="stallTimeout">How long the transfer may stall; <see cref="DefaultStallTimeout"/> when omitted.</param>
    /// <param name="progress">Told how many bytes this transfer has sent so far, after every chunk.</param>
    /// <returns>A task that completes when the bytes were sent and the server closed the connection or stayed silent.</returns>
    /// <exception cref="IOException">Thrown when no address could be reached, the source ran out, or the connection broke.</exception>
    public static async Task UploadAsync(
        FileTransferTicket ticket,
        string host,
        Stream source,
        long length,
        CancellationToken cancellationToken,
        TimeSpan? stallTimeout = null,
        IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var stall = stallTimeout ?? DefaultStallTimeout;
        using var client = await ConnectAsync(ticket, host, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();

        await WriteKeyAsync(stream, ticket, stall, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await WithStallAsync(
                token => source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token),
                stall,
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new IOException($"The source ended after {length - remaining} of {length} bytes.");
            }

            await WithStallAsync(
                async token =>
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    return read;
                },
                stall,
                cancellationToken).ConfigureAwait(false);

            remaining -= read;
            progress?.Report(length - remaining);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException ex)
        {
            // The server may already have closed; whether everything arrived is for the size check to say.
            throw new IOException($"The connection closed while the upload was being finished: {ex.Message}", ex);
        }

        // The server closes once it has stored the file. Waiting for that keeps a caller from checking
        // the file's size before the last bytes are written. Silence is not an error here: the size
        // check afterwards is what tells a complete upload from a partial one.
        try
        {
            await WithStallAsync(token => stream.ReadAsync(buffer, token), stall, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Downloads the bytes for a ticket from <c>ftinitdownload</c>.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="host">The server's host, tried before any addresses the ticket names.</param>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="length">How many bytes to expect: the ticket's size, less the seek position asked for.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <param name="stallTimeout">How long the transfer may stall; <see cref="DefaultStallTimeout"/> when omitted.</param>
    /// <param name="progress">Told how many bytes this transfer has received so far, after every chunk.</param>
    /// <returns>A task that completes when every expected byte arrived.</returns>
    /// <exception cref="IOException">Thrown when no address could be reached or the server closed before the last byte.</exception>
    public static async Task DownloadAsync(
        FileTransferTicket ticket,
        string host,
        Stream destination,
        long length,
        CancellationToken cancellationToken,
        TimeSpan? stallTimeout = null,
        IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var stall = stallTimeout ?? DefaultStallTimeout;
        using var client = await ConnectAsync(ticket, host, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();

        await WriteKeyAsync(stream, ticket, stall, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        var received = 0L;
        while (received < length)
        {
            var read = await WithStallAsync(
                token => stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - received)), token),
                stall,
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new IOException(
                    $"The server closed the transfer after {received} of {length} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(received);
        }
    }

    private static async Task WriteKeyAsync(NetworkStream stream, FileTransferTicket ticket, TimeSpan stall, CancellationToken cancellationToken)
    {
        var key = Encoding.ASCII.GetBytes(ticket.Key);
        await WithStallAsync(
            async token =>
            {
                await stream.WriteAsync(key, token).ConfigureAwait(false);
                return key.Length;
            },
            stall,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TcpClient> ConnectAsync(FileTransferTicket ticket, string host, CancellationToken cancellationToken)
    {
        // The configured host first: it is known to reach the server, while an address the server names
        // may be one only it can see, such as a container's own, and could hang until the timeout.
        var candidates = ticket.Addresses
            .Prepend(host)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(candidate, ticket.Port, deadline.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                failures.Add($"{candidate}:{ticket.Port} ({(ex is OperationCanceledException ? "timed out" : ex.Message)})");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw new IOException(
            $"Could not reach the file transfer interface at {string.Join(", ", failures)}. " +
            "The port has to be reachable from this machine, as the query port is.");
    }

    private static async Task<int> WithStallAsync(
        Func<CancellationToken, ValueTask<int>> operation,
        TimeSpan stall,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(stall);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The file transfer moved no data for {stall.TotalSeconds:0} seconds.");
        }
    }
}