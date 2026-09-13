namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// The MCP transport the server listens on.
/// </summary>
public enum TransportMode
{
    /// <summary>Serve a single local client over stdin/stdout. The default.</summary>
    Stdio,

    /// <summary>Serve remote clients over Streamable HTTP.</summary>
    Http,
}