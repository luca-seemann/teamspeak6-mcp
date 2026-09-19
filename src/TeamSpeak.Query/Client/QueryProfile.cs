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
    /// <summary>
    /// The login name that reaches a server without credentials, as the ServerQuery guest.
    /// </summary>
    /// <remarks>
    /// Added in TeamSpeak 6.0.0-beta13 and measured there: the SSH interface accepts the user
    /// <c>guest</c> with any password, the empty one included, and the WebQuery treats a request
    /// without an <c>x-api-key</c> header the same way. Such a session is nobody — <c>whoami</c>
    /// reports <c>client_database_id=0</c> and no login name — and holds only what the virtual
    /// server's <c>Guest Server Query</c> group grants, which by default is nothing but
    /// <c>version</c>, <c>whoami</c> and <c>use</c>.
    /// </remarks>
    public const string GuestUsername = "guest";

    /// <summary>Gets or sets the name used to select this profile from a tool call.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the server host name or address.</summary>
    public required string Host { get; set; }

    /// <summary>Gets or sets the SSH query port. The server default is 10022.</summary>
    public int SshPort { get; set; } = 10022;

    /// <summary>Gets or sets the query login name. Nearly always <c>serveradmin</c>.</summary>
    /// <remarks>
    /// <see cref="GuestUsername"/>, with no password and no API key, connects as the ServerQuery
    /// guest instead of as an account.
    /// </remarks>
    public string Username { get; set; } = "serveradmin";

    /// <summary>Gets or sets the query password used for SSH authentication.</summary>
    /// <remarks>
    /// The server offers password authentication only; keys are not accepted. A guest profile has
    /// none, and the empty password it sends instead is accepted for <see cref="GuestUsername"/>.
    /// </remarks>
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
    /// Measured on 6.0.0-beta12.1, and the same on 6.0.0-beta13: the server cut off a query session
    /// after 25 to 30 seconds without a command. SSH-level keepalive packets did not prevent that; a
    /// <c>version</c> every 20 seconds did.
    /// The documented 300 seconds did not hold there, so the default stays well below 25 seconds.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the SSH host key fingerprint the server must present, as <c>SHA256:</c> plus base64.</summary>
    /// <remarks>When set, it alone decides, and <see cref="HostKeyVerifier"/> is not consulted.</remarks>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>Gets or sets what checks the SSH host key when no fingerprint is pinned.</summary>
    /// <remarks>
    /// <see langword="null"/> accepts any key, which leaves the password open to whoever answers in the
    /// server's place. Applications should set a <see cref="Transport.KnownHostsFile"/> here.
    /// </remarks>
    public Transport.IHostKeyVerifier? HostKeyVerifier { get; set; }

    /// <summary>
    /// Gets a value indicating whether this profile reaches the server as the ServerQuery guest.
    /// </summary>
    /// <remarks>
    /// Deliberately an explicit choice — <see cref="Username"/> set to <see cref="GuestUsername"/>
    /// and no credentials — rather than something a profile falls back to. A forgotten password or
    /// API key stays an error instead of quietly becoming a session that can read almost nothing.
    /// </remarks>
    public bool IsGuest =>
        string.Equals(Username, GuestUsername, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(Password)
        && string.IsNullOrEmpty(ApiKey);

    /// <summary>Gets a value indicating whether this profile can use the SSH interface.</summary>
    public bool CanUseSsh => !string.IsNullOrEmpty(Password) || IsGuest;

    /// <summary>Gets a value indicating whether this profile can use the WebQuery interface.</summary>
    public bool CanUseWebQuery => WebQueryUrl is not null && (!string.IsNullOrEmpty(ApiKey) || IsGuest);

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
                    $"Profile '{Name}' selects the SSH transport but has no password. Set one, or set " +
                    $"Username to '{GuestUsername}' and leave the credentials empty to connect as the " +
                    "ServerQuery guest.");

            case PreferredTransport.WebQuery when !CanUseWebQuery:
                throw new InvalidOperationException(
                    $"Profile '{Name}' selects the WebQuery transport but has no URL and API key. A guest " +
                    $"profile (Username '{GuestUsername}', no credentials) still needs the URL.");

            case PreferredTransport.Auto when !CanUseSsh && !CanUseWebQuery:
                throw new InvalidOperationException(
                    $"Profile '{Name}' has neither a password for SSH nor a URL and API key for the " +
                    $"WebQuery. To reach the server without credentials, set Username to " +
                    $"'{GuestUsername}'.");

            default:
                break;
        }
    }
}