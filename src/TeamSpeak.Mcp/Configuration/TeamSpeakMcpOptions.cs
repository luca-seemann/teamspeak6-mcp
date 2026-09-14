using TeamSpeak.Mcp.Safety;
using TeamSpeak.Query.Client;

namespace TeamSpeak.Mcp.Configuration;

/// <summary>
/// Everything this server reads from configuration.
/// </summary>
/// <remarks>
/// Bound from a configuration file and from environment variables, with the environment winning.
/// That split matters because the file is a convenient place for hosts and ports but a poor place
/// for passwords and API keys, which belong in the environment or a secret store.
/// </remarks>
public sealed class TeamSpeakMcpOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "TeamSpeak";

    /// <summary>Gets the configured servers, keyed by profile name.</summary>
    /// <remarks>
    /// The dictionary key names the profile, so a profile need not repeat its own name. Where a
    /// profile does set <see cref="QueryProfileOptions.Name"/>, that value wins.
    /// </remarks>
    public Dictionary<string, QueryProfileOptions> Profiles { get; } = [];

    /// <summary>Gets or sets how much damage tools are allowed to do on profiles that do not say.</summary>
    public SafetyLevel Safety { get; set; } = SafetyLevel.ReadOnly;

    /// <summary>Gets or sets how many events each profile keeps for callers to read.</summary>
    public int EventBufferSize { get; set; } = QueryEventHub.DefaultCapacity;

    /// <summary>Turns the bound options into validated profiles.</summary>
    /// <returns>The profile registry.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a profile is unusable.</exception>
    public ProfileRegistry BuildRegistry() =>
        new(Profiles.Select(entry => entry.Value.ToProfile(entry.Key)));

    /// <summary>Builds the safety policy from the global level and each profile's own setting.</summary>
    /// <returns>The policy.</returns>
    public SafetyPolicy BuildSafetyPolicy() =>
        new(
            Safety,
            Profiles
                .Where(entry => entry.Value.Safety is not null)
                .ToDictionary(
                    entry => string.IsNullOrWhiteSpace(entry.Value.Name) ? entry.Key : entry.Value.Name,
                    entry => entry.Value.Safety!.Value,
                    StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// One configured TeamSpeak server, in the shape configuration binding understands.
/// </summary>
/// <remarks>
/// This mirrors <see cref="QueryProfile"/> but uses plain, bindable types — strings and integers
/// rather than <see cref="Uri"/> and <see cref="TimeSpan"/> — so that a malformed setting produces
/// a clear message here instead of a binding failure deep in the host.
/// </remarks>
public sealed class QueryProfileOptions
{
    /// <summary>Gets or sets the profile name. Defaults to its configuration key.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the server host name or address.</summary>
    public string? Host { get; set; }

    /// <summary>Gets or sets the SSH query port.</summary>
    public int SshPort { get; set; } = 10022;

    /// <summary>Gets or sets the query login name.</summary>
    public string Username { get; set; } = "serveradmin";

    /// <summary>Gets or sets the query admin password.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets the WebQuery base address.</summary>
    public string? WebQueryUrl { get; set; }

    /// <summary>Gets or sets the WebQuery API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets which interface to prefer.</summary>
    public PreferredTransport Transport { get; set; } = PreferredTransport.Auto;

    /// <summary>Gets or sets the virtual server addressed by default.</summary>
    public int DefaultVirtualServerId { get; set; } = 1;

    /// <summary>Gets or sets the minimum gap between commands, in milliseconds.</summary>
    public int CommandIntervalMs { get; set; } = (int)FloodGuard.DefaultInterval.TotalMilliseconds;

    /// <summary>Gets or sets how long to wait for one command to answer, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets after how many idle seconds an SSH session sends a command to stay connected.
    /// </summary>
    /// <remarks>The test server dropped sessions after 25 to 30 idle seconds, so keep this below that.</remarks>
    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets how much damage tools may do on this profile, overriding the global level.
    /// </summary>
    /// <remarks>
    /// The override works in both directions: a staging server can allow destructive tools while
    /// production stays read-only, or one production profile can be locked down below a permissive
    /// global default.
    /// </remarks>
    public SafetyLevel? Safety { get; set; }

    /// <summary>Converts these settings into a validated profile.</summary>
    /// <param name="key">The configuration key, used when no explicit name is set.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a setting is unusable.</exception>
    public QueryProfile ToProfile(string key)
    {
        var name = string.IsNullOrWhiteSpace(Name) ? key : Name;

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException($"Profile '{name}' has no Host.");
        }

        Uri? webQueryUrl = null;
        if (!string.IsNullOrWhiteSpace(WebQueryUrl)
            && !Uri.TryCreate(WebQueryUrl, UriKind.Absolute, out webQueryUrl))
        {
            throw new InvalidOperationException(
                $"Profile '{name}' has an unusable WebQueryUrl '{WebQueryUrl}'. " +
                "Expected something like http://ts.example.com:10080.");
        }

        if (CommandIntervalMs < 0)
        {
            throw new InvalidOperationException($"Profile '{name}' has a negative CommandIntervalMs.");
        }

        var profile = new QueryProfile
        {
            Name = name,
            Host = Host,
            SshPort = SshPort,
            Username = Username,
            Password = Password,
            WebQueryUrl = webQueryUrl,
            ApiKey = ApiKey,
            Transport = Transport,
            DefaultVirtualServerId = DefaultVirtualServerId,
            CommandInterval = TimeSpan.FromMilliseconds(CommandIntervalMs),
            CommandTimeout = TimeSpan.FromSeconds(CommandTimeoutSeconds),
            KeepAliveInterval = TimeSpan.FromSeconds(KeepAliveSeconds),
        };

        profile.Validate();
        return profile;
    }
}