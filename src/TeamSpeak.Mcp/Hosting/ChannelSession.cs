using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Holds the session a channel pushes into, captured as the initialize answer goes out.
/// </summary>
/// <remarks>
/// A background service has no request of its own, so it cannot be handed an
/// <see cref="McpServer"/> the way a tool is. An outgoing message filter sees one on every message,
/// and the initialize result is the first, which is also the moment a channel may start pushing.
/// Over stdio there is exactly one session; a second one simply replaces the first here, and
/// Streamable HTTP is refused at startup instead.
/// </remarks>
public sealed class ChannelSession
{
    private readonly TaskCompletionSource<McpServer> _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Captures the session from every outgoing message.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A handler that remembers the session and changes nothing.</returns>
    public McpMessageHandler Capture(McpMessageHandler next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcResponse { Result: not null })
            {
                _opened.TrySetResult(context.Server);
            }

            return next(context, cancellationToken);
        };
    }

    /// <summary>Waits for the session to open.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The session, once the client has been answered once.</returns>
    public Task<McpServer> OpenedAsync(CancellationToken cancellationToken) =>
        _opened.Task.WaitAsync(cancellationToken);
}