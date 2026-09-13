namespace TeamSpeak.Query.Client;

/// <summary>
/// The set of TeamSpeak servers this process can administer, addressed by name.
/// </summary>
/// <remarks>
/// Tools take an optional profile name so one MCP server can look after several TeamSpeak servers,
/// for example a production and a staging instance. With a single profile configured, the name can
/// be omitted everywhere.
/// </remarks>
public sealed class ProfileRegistry
{
    private readonly Dictionary<string, QueryProfile> _profiles;

    /// <summary>Creates a registry over a set of profiles.</summary>
    /// <param name="profiles">The configured profiles.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a profile is invalid or two profiles share a name.
    /// </exception>
    public ProfileRegistry(IEnumerable<QueryProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        _profiles = new Dictionary<string, QueryProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            profile.Validate();

            if (!_profiles.TryAdd(profile.Name, profile))
            {
                throw new InvalidOperationException(
                    $"Two profiles are named '{profile.Name}'; names must be unique.");
            }
        }
    }

    /// <summary>Gets the configured profile names, in configuration order.</summary>
    public IReadOnlyCollection<string> Names => _profiles.Keys;

    /// <summary>Gets a value indicating whether any profile is configured.</summary>
    public bool IsEmpty => _profiles.Count == 0;

    /// <summary>
    /// Resolves a profile by name, or the only profile when no name is given.
    /// </summary>
    /// <param name="name">
    /// The profile to resolve. Omit it to use the single configured profile.
    /// </param>
    /// <returns>The requested profile.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when nothing is configured, when the name is unknown, or when the name was omitted
    /// while several profiles exist.
    /// </exception>
    public QueryProfile Resolve(string? name = null)
    {
        if (_profiles.Count == 0)
        {
            throw new InvalidOperationException(
                "No TeamSpeak server is configured. Add a profile with a host and credentials.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return _profiles.Count == 1
                ? _profiles.Values.First()
                : throw new InvalidOperationException(
                    $"Several profiles are configured ({string.Join(", ", _profiles.Keys)}), " +
                    "so a tool call has to name the one it means.");
        }

        return _profiles.TryGetValue(name, out var profile)
            ? profile
            : throw new InvalidOperationException(
                $"No profile named '{name}'. Configured profiles: {string.Join(", ", _profiles.Keys)}.");
    }
}