using Microsoft.Extensions.Configuration;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Sorts the tools into groups that configuration can switch off, to spend less of a client's context.
/// </summary>
/// <remarks>
/// <para>
/// Every tool definition is sent to the model with each request, about 34,000 tokens for all of them.
/// A deployment that never touches files or events can leave those groups out.
/// </para>
/// <para>
/// This is not a safety measure. Safety levels decide what may change, and a tool above the profile's
/// level stays listed so the model can say why it was refused. <c>core</c> cannot be switched off:
/// without <c>ts_profiles_list</c> a model cannot tell which servers exist.
/// </para>
/// </remarks>
public static class ToolGroups
{
    /// <summary>The configuration key, below <c>TeamSpeak</c>, listing the groups to switch off.</summary>
    public const string ConfigurationKey = "TeamSpeak:DisabledToolGroups";

    /// <summary>The group that is always on.</summary>
    public const string Core = "core";

    // Exact names are matched before prefixes, so ts_client_groups lands in groups, not clients.
    private static readonly (string Group, string[] Names, string[] Prefixes)[] Rules =
    [
        (Core, ["ts_profiles_list", "ts_whoami", "ts_command_help"], []),
        ("raw", ["ts_query_raw"], []),
        ("servers", ["ts_health_report", "ts_temp_password"], ["ts_vserver_", "ts_instance_"]),
        ("channels", [], ["ts_channel_"]),
        ("groups", ["ts_client_groups", "ts_client_channelgroup_set"], ["ts_servergroup_", "ts_channelgroup_"]),
        ("clients", ["ts_offline_message"], ["ts_client_", "ts_clientdb_", "ts_message_", "ts_custom_"]),
        ("permissions", [], ["ts_perm_"]),
        ("moderation", [], ["ts_ban_", "ts_complaint_", "ts_token_", "ts_log_"]),
        ("access", [], ["ts_apikey_", "ts_querylogin_"]),
        ("events", [], ["ts_events_"]),
        ("files", [], ["ts_file_"]),
    ];

    /// <summary>Gets every group name, in the order the README lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = Rules.Select(rule => rule.Group).ToList();

    /// <summary>Finds the group a tool belongs to.</summary>
    /// <param name="toolName">The tool's name.</param>
    /// <returns>The group, or <see langword="null"/> for a tool no rule covers.</returns>
    public static string? Of(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        return Rules.FirstOrDefault(rule => rule.Names.Contains(toolName, StringComparer.Ordinal)).Group
            ?? Rules.FirstOrDefault(rule => rule.Prefixes.Any(prefix => toolName.StartsWith(prefix, StringComparison.Ordinal))).Group;
    }

    /// <summary>Reads the groups to switch off from configuration.</summary>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The groups to leave out; empty when none are configured.</returns>
    /// <exception cref="InvalidOperationException">Thrown for an unknown group, or for <c>core</c>.</exception>
    /// <remarks>
    /// Entries may also be comma-separated, so a single environment variable such as
    /// <c>TSMCP_TeamSpeak__DisabledToolGroups=files,events</c> works in an MCP client's config.
    /// </remarks>
    public static IReadOnlySet<string> Disabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationKey);
        var entries = section.Value is { } single ? [single] : section.Get<string[]>() ?? [];

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in entries.SelectMany(entry => entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var group = name.ToLowerInvariant();
            if (group == Core)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} lists '{Core}', which cannot be switched off: it holds ts_profiles_list, ts_whoami and ts_command_help.");
            }

            if (!Names.Contains(group))
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} lists '{name}', which is not a tool group. The groups are: {string.Join(", ", Names.Where(n => n != Core))}.");
            }

            disabled.Add(group);
        }

        return disabled;
    }
}