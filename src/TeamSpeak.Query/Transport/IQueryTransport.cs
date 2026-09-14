using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Transport;

/// <summary>Sends one command inside an exclusive sequence.</summary>
/// <param name="command">The command.</param>
/// <param name="cancellationToken">Cancels the command.</param>
/// <returns>The response.</returns>
public delegate Task<QueryResponse> QuerySender(QueryCommand command, CancellationToken cancellationToken);

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

    /// <summary>
    /// Gets a value indicating whether the transport speaks for one server-side query client whose
    /// state — selected virtual server, current channel — lasts between commands.
    /// </summary>
    /// <remarks>
    /// True for SSH, and for the WebQuery, whose internal query client keeps its channel between
    /// requests. A transport without such a client has nothing to move into a channel and nothing to
    /// protect from being kicked.
    /// </remarks>
    bool HoldsSession { get; }

    /// <summary>
    /// Runs several commands as one uninterrupted sequence on the session.
    /// </summary>
    /// <typeparam name="T">What the sequence produces.</typeparam>
    /// <param name="work">The sequence, given a sender for its commands.</param>
    /// <param name="cancellationToken">Cancels waiting for the session.</param>
    /// <returns>What <paramref name="work"/> returns.</returns>
    /// <remarks>
    /// No other caller's command can run between the commands of <paramref name="work"/>, so a
    /// sequence that depends on session state — which client this session is, which channel it is in —
    /// cannot have that state changed underneath it. Keep sequences short: every other caller on the
    /// connection waits for them. A transport without a session runs the commands as they come.
    /// </remarks>
    Task<T> RunExclusiveAsync<T>(Func<QuerySender, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Sends a command and waits for its complete response.</summary>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>The decoded response, including a non-zero status for server-side failures.</returns>
    /// <remarks>
    /// The command is addressed to <see cref="QueryCommand.VirtualServerId"/>, or to the profile's
    /// default virtual server when that is unset. The two interfaces express this completely
    /// differently — SSH has a stateful <c>use</c> command, the WebQuery puts the id in the URL — and
    /// an implementation must make selection and command one indivisible step, because a transport is
    /// shared by concurrent callers.
    /// </remarks>
    Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default);

    /// <summary>Streams server-pushed events for the subscriptions registered on this connection.</summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>An asynchronous sequence of events.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="SupportsEvents"/> is <see langword="false"/>.
    /// </exception>
    IAsyncEnumerable<QueryEvent> GetEventsAsync(CancellationToken cancellationToken = default);
}