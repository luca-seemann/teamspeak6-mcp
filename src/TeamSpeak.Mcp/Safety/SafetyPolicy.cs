using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Client;

namespace TeamSpeak.Mcp.Safety;

/// <summary>
/// Decides whether a tool may do what it is about to do on a given profile.
/// </summary>
/// <remarks>
/// <para>
/// This is the enforcement behind the MCP tool annotations. Annotations are hints a client may
/// ignore; the policy is checked on the server before anything is sent to TeamSpeak.
/// </para>
/// <para>
/// A refusal is reported, not hidden. The model receives a message naming the level it needs and
/// the setting that grants it, so it can tell the user what to change rather than silently doing
/// nothing.
/// </para>
/// </remarks>
public sealed class SafetyPolicy
{
    private readonly Dictionary<string, SafetyLevel> _profileLevels;

    /// <summary>Initialises a policy.</summary>
    /// <param name="defaultLevel">The level for profiles that do not set their own.</param>
    /// <param name="profileLevels">Per-profile levels, keyed by profile name.</param>
    public SafetyPolicy(
        SafetyLevel defaultLevel = SafetyLevel.ReadOnly,
        IReadOnlyDictionary<string, SafetyLevel>? profileLevels = null)
    {
        Default = defaultLevel;
        _profileLevels = new Dictionary<string, SafetyLevel>(
            profileLevels ?? new Dictionary<string, SafetyLevel>(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the level applied to profiles that do not set their own.</summary>
    public SafetyLevel Default { get; }

    /// <summary>Gets the level a profile is allowed.</summary>
    /// <param name="profileName">The profile name.</param>
    /// <returns>The profile's own level, or <see cref="Default"/>.</returns>
    public SafetyLevel LevelFor(string profileName)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return _profileLevels.TryGetValue(profileName, out var level) ? level : Default;
    }

    /// <summary>
    /// Refuses an action that needs more than the profile allows.
    /// </summary>
    /// <param name="profile">The profile the action targets.</param>
    /// <param name="required">The level the action needs.</param>
    /// <param name="action">What is being attempted, for the message, for example <c>ts_query_raw with 'channeldelete'</c>.</param>
    /// <exception cref="McpException">Thrown when the profile does not allow the action.</exception>
    public void Demand(QueryProfile profile, SafetyLevel required, string action)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(action);

        var allowed = LevelFor(profile.Name);
        if (required <= allowed)
        {
            return;
        }

        throw new McpException(
            $"{action} needs the {required} safety level, but profile '{profile.Name}' allows only " +
            $"{allowed}. Nothing was sent to the server. To allow it, set " +
            $"TeamSpeak:Profiles:{profile.Name}:Safety to {required} (environment variable " +
            $"TSMCP_TeamSpeak__Profiles__{profile.Name}__Safety={required}), or TeamSpeak:Safety for " +
            "every profile.");
    }
}