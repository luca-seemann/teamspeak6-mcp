using System.Runtime.CompilerServices;

using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.FakeServer;

/// <summary>
/// An <see cref="IQueryTransport"/> that replays canned responses, so transports and tools can be
/// tested without a live TeamSpeak server.
/// </summary>
public sealed class FakeQueryTransport : IQueryTransport
{
    private readonly Dictionary<string, Func<QueryCommand, QueryResponse>> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryCommand> _sent = [];
    private readonly System.Threading.Channels.Channel<QueryEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<QueryEvent>();

    /// <summary>Initialises a fake that reports the given event support.</summary>
    /// <param name="supportsEvents">What <see cref="SupportsEvents"/> reports.</param>
    public FakeQueryTransport(bool supportsEvents = true) => SupportsEvents = supportsEvents;

    /// <inheritdoc />
    public bool SupportsEvents { get; }

    /// <inheritdoc />
    /// <remarks>Follows <see cref="SupportsEvents"/>: a fake SSH session has both, a fake WebQuery neither.</remarks>
    public bool HoldsSession => SupportsEvents;

    /// <summary>Gets how many exclusive sequences have run.</summary>
    public int ExclusiveSequences { get; private set; }

    /// <inheritdoc />
    public Task<T> RunExclusiveAsync<T>(Func<QuerySender, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExclusiveSequences++;
        return work(SendAsync);
    }

    /// <summary>Gets a value indicating whether <see cref="DisposeAsync"/> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets the commands this transport has been asked to send, in order.</summary>
    public IReadOnlyList<QueryCommand> SentCommands => _sent;

    /// <summary>Registers the response to return for a command name.</summary>
    /// <param name="commandName">The command to answer, for example <c>whoami</c>.</param>
    /// <param name="response">The response to return.</param>
    /// <returns>This instance, so registrations can be chained.</returns>
    public FakeQueryTransport Returns(string commandName, QueryResponse response) =>
        Returns(commandName, _ => response);

    /// <summary>Registers a response that depends on the command's parameters.</summary>
    /// <param name="commandName">The command to answer.</param>
    /// <param name="respond">Builds the response for each command sent.</param>
    /// <returns>This instance, so registrations can be chained.</returns>
    public FakeQueryTransport Returns(string commandName, Func<QueryCommand, QueryResponse> respond)
    {
        _responses[commandName] = respond;
        return this;
    }

    /// <inheritdoc />
    public Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _sent.Add(command);

        return Task.FromResult(_responses.TryGetValue(command.Name, out var respond)
            ? respond(command)
            : new QueryResponse([], new QueryError(256, "command not found")));
    }

    /// <summary>Delivers an event to whoever reads <see cref="GetEventsAsync"/>, as the server would.</summary>
    /// <param name="notification">The event.</param>
    public void Push(QueryEvent notification) => _events.Writer.TryWrite(notification);

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryEvent> GetEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var notification in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return notification;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}