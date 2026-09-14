namespace TeamSpeak.Query.Client;

/// <summary>
/// Which transport a profile should prefer when both are configured.
/// </summary>
public enum PreferredTransport
{
    /// <summary>
    /// Use SSH when it is configured, otherwise the WebQuery. The sensible default.
    /// </summary>
    /// <remarks>
    /// SSH carries many commands over one session and is the only interface that delivers events,
    /// whereas the WebQuery answers every request with <c>Connection: close</c> and so pays for a
    /// fresh TCP connection each time.
    /// </remarks>
    Auto,

    /// <summary>Always use the SSH interface.</summary>
    Ssh,

    /// <summary>Always use the HTTP or HTTPS WebQuery.</summary>
    WebQuery,
}

/// <summary>
/// A named TeamSpeak server this MCP server can administer.
/// </summary>
/// <remarks>
/// A profile can carry credentials for both interfaces. SSH needs the <c>serveradmin</c> password;
/// the WebQuery needs an API key, which can only be minted over SSH with
/// <c>apikeyadd scope=manage lifetime=0</c>, so an SSH-capable profile can bootstrap its own key.
/// </remarks>
public sealed class QueryProfile
{
    /// <summary>Gets or sets the name used to select this profile from a tool call.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the server host name or address.</summary>
    public required string Host { get; set; }

    /// <summary>Gets or sets the SSH query port. The server default is 10022.</summary>
    public int SshPort { get; set; } = 10022;

    /// <summary>Gets or sets the query login name. Nearly always <c>serveradmin</c>.</summary>
    public string Username { get; set; } = "serveradmin";

    /// <summary>Gets or sets the query password used for SSH authentication.</summary>
    /// <remarks>The server offers password authentication only; keys are not accepted.</remarks>
    public string? Password { get; set; }

    /// <summary>Gets or sets the WebQuery base address, for example <c>http://ts.example.com:10080</c>.</summary>
    public Uri? WebQueryUrl { get; set; }

    /// <summary>Gets or sets the API key sent as the <c>x-api-key</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets which interface to use when both are configured.</summary>
    public PreferredTransport Transport { get; set; } = PreferredTransport.Auto;

    /// <summary>Gets or sets the virtual server selected by default.</summary>
    public int DefaultVirtualServerId { get; set; } = 1;

    /// <summary>Gets or sets the minimum gap between commands.</summary>
    /// <remarks>
    /// Lower this only against a server whose flood protection you have exempted. The default was
    /// measured against a live server; going faster earns an IP-level block.
    /// </remarks>
    public TimeSpan CommandInterval { get; set; } = FloodGuard.DefaultInterval;

    /// <summary>Gets or sets how long to wait for a single command to answer.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how long an SSH session may sit without a command before one is sent to keep it.</summary>
    /// <remarks>
    /// Measured on 6.0.0-beta12.1: the server cut off a query session after 25 to 30 seconds without a
    /// command. SSH-level keepalive packets did not prevent that; a <c>version</c> every 20 seconds did.
    /// The documented 300 seconds did not hold there, so the default stays well below 25 seconds.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets a value indicating whether this profile can use the SSH interface.</summary>
    public bool CanUseSsh => !string.IsNullOrEmpty(Password);

    /// <summary>Gets a value indicating whether this profile can use the WebQuery interface.</summary>
    public bool CanUseWebQuery => WebQueryUrl is not null && !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// Checks that the profile can actually reach a server.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the profile is missing the credentials its chosen transport needs.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("A profile needs a name.");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException($"Profile '{Name}' has no host.");
        }

        if (KeepAliveInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Profile '{Name}' needs a keepalive interval above zero; the server drops a session after about 30 seconds without a command.");
        }

        switch (Transport)
        {
            case PreferredTransport.Ssh when !CanUseSsh:
                throw new InvalidOperationException(
                    $"Profile '{Name}' selects the SSH transport but has no password.");

            case PreferredTransport.WebQuery when !CanUseWebQuery:
                throw new InvalidOperationException(
                    $"Profile '{Name}' selects the WebQuery transport but has no URL and API key.");

            case PreferredTransport.Auto when !CanUseSsh && !CanUseWebQuery:
                throw new InvalidOperationException(
                    $"Profile '{Name}' has neither a password for SSH nor a URL and API key for the WebQuery.");

            default:
                break;
        }
    }
}