using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Turns the errors tools raise on purpose into error results before the SDK treats them as crashes.
/// </summary>
/// <remarks>
/// <para>
/// Tools report a safety level that is too low, a bad argument or a refusal from the server by throwing
/// <see cref="McpException"/> with a message written for the model. The SDK sends that message back
/// either way, but it also logged each one as an unhandled exception, at error level with a full stack
/// trace, so every refused call looked like a crash in the client's MCP log.
/// </para>
/// <para>
/// Only <see cref="McpException"/> itself is handled here. <see cref="McpProtocolException"/> signals a
/// protocol-level failure, such as an unknown tool, and any other exception is a real bug; both still
/// reach the SDK unchanged and are logged in full.
/// </para>
/// </remarks>
public static class ExpectedToolErrors
{
    /// <summary>Wraps the call-tool pipeline.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A handler that answers an expected tool error with an error result.</returns>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (McpException ex) when (ex is not McpProtocolException)
            {
                return ToResult(ex);
            }
        };
    }

    /// <summary>Builds the error result a model sees for an expected tool error.</summary>
    /// <param name="exception">The error the tool raised.</param>
    /// <returns>An error result carrying the message.</returns>
    public static CallToolResult ToResult(McpException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = exception.Message }],
        };
    }
}