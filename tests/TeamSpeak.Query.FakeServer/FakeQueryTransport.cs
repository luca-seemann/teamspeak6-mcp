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
    private readonly Dictionary<string, QueryResponse> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryCommand> _sent = [];

    /// <summary>Initialises a fake that reports the given event support.</summary>
    /// <param name="supportsEvents">What <see cref="SupportsEvents"/> reports.</param>
    public FakeQueryTransport(bool supportsEvents = true) => SupportsEvents = supportsEvents;

    /// <inheritdoc />
    public bool SupportsEvents { get; }

    /// <summary>Gets a value indicating whether <see cref="DisposeAsync"/> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets the commands this transport has been asked to send, in order.</summary>
    public IReadOnlyList<QueryCommand> SentCommands => _sent;

    /// <summary>Registers the response to return for a command name.</summary>
    /// <param name="commandName">The command to answer, for example <c>whoami</c>.</param>
    /// <param name="response">The response to return.</param>
    /// <returns>This instance, so registrations can be chained.</returns>
    public FakeQueryTransport Returns(string commandName, QueryResponse response)
    {
        _responses[commandName] = response;
        return this;
    }

    /// <inheritdoc />
    public Task<QueryResponse> SendAsync(QueryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _sent.Add(command);

        return Task.FromResult(_responses.TryGetValue(command.Name, out var response)
            ? response
            : new QueryResponse([], new QueryError(256, "command not found")));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryEvent> GetEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}