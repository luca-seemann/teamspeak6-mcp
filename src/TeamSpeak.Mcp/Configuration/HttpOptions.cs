namespace TeamSpeak.Mcp.Configuration;

/// <summary>
/// Who may use the Streamable HTTP endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>TeamSpeak:Http</c>. None of it applies over stdio, where only the client that
/// started the process can talk to it.
/// </para>
/// <para>
/// Without a <see cref="BearerToken"/> the endpoint trusts whoever reaches it, so it may then only
/// be bound to a loopback address, and only loopback host names are accepted. With a token every
/// request must present it, which also covers a browser page that reached the endpoint through DNS
/// rebinding: the page cannot know the token.
/// </para>
/// </remarks>
public sealed class HttpOptions
{
    /// <summary>The shortest bearer token accepted, so that it cannot be guessed.</summary>
    public const int MinimumTokenLength = 32;

    /// <summary>
    /// Gets or sets the token every request must present as <c>Authorization: Bearer</c>.
    /// </summary>
    /// <remarks>Required when the endpoint is bound to anything but a loopback address.</remarks>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Gets origins, such as <c>https://admin.example.com</c>, allowed in addition to loopback ones.
    /// </summary>
    /// <remarks>
    /// MCP clients other than browsers send no <c>Origin</c> header and are not affected. A request
    /// that does send one, from any origin not listed here or on a loopback host, is refused with 403.
    /// </remarks>
    public List<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// Gets host names, such as <c>teamspeak6-mcp</c>, accepted in addition to loopback names when no
    /// bearer token is configured.
    /// </summary>
    public List<string> AllowedHosts { get; } = [];
}