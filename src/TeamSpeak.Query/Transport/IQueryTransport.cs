using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Transport;

/// <summary>
/// A connection to the ServerQuery interface of a TeamSpeak 6 server.
/// </summary>
/// <remarks>
/// TeamSpeak 6 exposes the same command set over SSH (default port 10022) and over the HTTP/HTTPS
/// WebQuery (default ports 10080/10443); only the transport differs. Everything above this interface
/// is therefore transport-agnostic. The one asymmetry is events: <c>servernotifyregister</c> requires a
/// persistent session and is available over SSH only, which <see cref="SupportsEvents"/> reports.
/// </remarks>
public interface IQueryTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether this transport can deliver events via <see cref="GetEventsAsync"/>.
    /// </summary>
    bool SupportsEvents { get; }

    /// <summary>Sends a command and waits for its complete response.</summary>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>The decoded response, including a non-zero status for server-side failures.</returns>
    Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default);

    /// <summary>Streams server-pushed events for the subscriptions registered on this connection.</summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>An asynchronous sequence of events.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="SupportsEvents"/> is <see langword="false"/>.
    /// </exception>
    IAsyncEnumerable<QueryEvent> GetEventsAsync(CancellationToken cancellationToken = default);
}