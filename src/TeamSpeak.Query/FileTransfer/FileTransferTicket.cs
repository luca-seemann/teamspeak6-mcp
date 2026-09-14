using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.FileTransfer;

/// <summary>
/// What <c>ftinitupload</c> or <c>ftinitdownload</c> hands back: the key and port for one transfer
/// on the file transfer interface.
/// </summary>
/// <remarks>
/// <para>
/// A ticket is used once. The bytes then travel over a plain TCP connection to <see cref="Port"/>
/// (30033 by default): the client writes the 32 characters of <see cref="Key"/>, followed by the file
/// for an upload, or reads the file for a download. The server closes the connection when the
/// transfer is done. A ticket that is never used shows up in <c>ftlist</c> as waiting and was gone a
/// few minutes later on 6.0.0-beta12.1.
/// </para>
/// <para>
/// The two commands report a refusal inside the record rather than in the status line: a missing file
/// comes back as <c>status=2051 msg=file\snot\sfound</c> together with <c>error id=0</c>.
/// <see cref="FromRecord"/> turns that into an exception, so a refused transfer cannot be mistaken
/// for a ticket.
/// </para>
/// </remarks>
/// <param name="ClientTransferId">The id the client chose for the transfer (<c>clientftfid</c>).</param>
/// <param name="ServerTransferId">The server's id for it (<c>serverftfid</c>), which <c>ftstop</c> takes.</param>
/// <param name="Key">The key that opens the transfer (<c>ftkey</c>).</param>
/// <param name="Port">The file transfer port.</param>
/// <param name="Size">For a download, the whole file's size in bytes; 0 for an upload.</param>
/// <param name="SeekPosition">For an upload, where the server expects the bytes to start; 0 unless resuming.</param>
/// <param name="Addresses">
/// Addresses the server named because it thinks the query address cannot reach its file transfer
/// interface; usually empty.
/// </param>
public sealed record FileTransferTicket(
    int ClientTransferId,
    int ServerTransferId,
    string Key,
    int Port,
    long Size,
    long SeekPosition,
    IReadOnlyList<string> Addresses)
{
    /// <summary>Reads a ticket from the record of <c>ftinitupload</c> or <c>ftinitdownload</c>.</summary>
    /// <param name="record">The first record of the response.</param>
    /// <returns>The ticket.</returns>
    /// <exception cref="FileTransferRefusedException">Thrown when the record carries a refusal status.</exception>
    /// <exception cref="QueryProtocolException">Thrown when the record has neither a status nor a key.</exception>
    public static FileTransferTicket FromRecord(QueryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.GetInt32("status") is var status and not QueryErrorCode.Ok)
        {
            throw new FileTransferRefusedException(status, record.GetString("msg"));
        }

        var key = record.GetString("ftkey");
        if (key.Length == 0)
        {
            throw new QueryProtocolException(
                $"The file transfer answer has no ftkey. Present fields: {string.Join(", ", record.Keys)}.");
        }

        var addresses = record.GetString("ip")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new FileTransferTicket(
            record.GetInt32("clientftfid"),
            record.GetInt32("serverftfid"),
            key,
            record.GetInt32("port", 30033),
            record.GetInt64("size"),
            record.GetInt64("seekpos"),
            addresses);
    }
}

/// <summary>
/// The server refused to start a transfer, reporting why inside the answer's record.
/// </summary>
public sealed class FileTransferRefusedException : Exception
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="status">The status code from the record, such as 2051 for a missing file.</param>
    /// <param name="serverMessage">The server's message, such as <c>file not found</c>.</param>
    public FileTransferRefusedException(int status, string serverMessage)
        : base($"The server refused the transfer (status {status}: {serverMessage}).")
    {
        Status = status;
        ServerMessage = serverMessage;
    }

    /// <summary>Initialises the exception.</summary>
    public FileTransferRefusedException()
        : this(0, string.Empty)
    {
    }

    /// <summary>Initialises the exception.</summary>
    /// <param name="message">The message.</param>
    public FileTransferRefusedException(string message)
        : base(message)
    {
        ServerMessage = string.Empty;
    }

    /// <summary>Initialises the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public FileTransferRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ServerMessage = string.Empty;
    }

    /// <summary>Gets the status code from the record.</summary>
    public int Status { get; }

    /// <summary>Gets the server's message.</summary>
    public string ServerMessage { get; }

    /// <summary>Gets the refusal as a <see cref="QueryError"/>, so it can be explained like any other.</summary>
    public QueryError ToQueryError() => new(Status, ServerMessage);
}
