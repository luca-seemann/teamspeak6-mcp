using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TeamSpeak.Mcp.Prompts;

/// <summary>
/// Ready-made instructions for the administration tasks this server exists for.
/// </summary>
/// <remarks>
/// <para>
/// A prompt is a starting point a person picks in their MCP client, not a tool the model calls. Each one
/// names the tools that do the work, in the order that answers the question fastest, and states what
/// the model may change. The tools enforce the safety level regardless; the prompts keep a model from
/// trying changes nobody asked for.
/// </para>
/// <para>
/// Arguments arrive as text, because MCP prompt arguments are strings.
/// </para>
/// </remarks>
[McpServerPromptType]
public static class AdminPrompts
{
    private const string ProfileArgument =
        "The profile to work on, as listed by ts_profiles_list. Omit it when only one is configured.";

    private const string VirtualServerArgument =
        "The virtual server id. Omit it for the profile's default virtual server.";

    /// <summary>Audits a virtual server without changing it.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <returns>The instructions.</returns>
    [McpServerPrompt(Name = "server-audit", Title = "Audit a virtual server")]
    [Description("Reviews a TeamSpeak virtual server's health, who holds power over it, bans, complaints, " +
                 "access keys and recent log entries, and reports findings by severity. Changes nothing.")]
    public static string ServerAudit(
        [Description(ProfileArgument)] string? profile = null,
        [Description(VirtualServerArgument)] string? virtualServerId = null)
    {
        var target = Target(profile, virtualServerId);

        return $"""
            Audit {target.Name} and report what needs attention. {target.Calls}

            This is a read-only review. Do not call any tool that changes something, even where a fix is
            obvious; describe the fix and the tool that would make it instead.

            Work through these, and keep going when one is refused for its safety level; say so in the report:
            1. Health: ts_health_report for slots, packet loss and ping, and ts_vserver_info for the settings
               behind them.
            2. Power: ts_servergroup_list, then ts_servergroup_members for every group that is not a guest or
               default group. For the permissions that matter most, use ts_perm_list to find the exact names
               (kicking, banning, editing groups and permissions, editing the virtual server) and ts_perm_find
               to see who holds them. Flag individual clients holding them directly rather than through a group.
            3. Moderation: ts_ban_list for bans that never expire, bans with no reason, and IP bans. An IP ban
               may lock out everyone behind the same NAT or container network. ts_complaint_list for
               complaints nobody acted on.
            4. Access: ts_apikey_list for keys with unlimited lifetime or a wider scope than needed,
               ts_querylogin_list for query logins, and ts_token_list for privilege keys nobody redeemed.
               ts_token_list needs Write, because the keys are live credentials; if it is refused, note that
               privilege keys were not checked.
            5. Log: ts_log_view for recent warnings and errors, and ts_log_view with instance=true for the
               instance as a whole.

            Report findings grouped as critical, worth fixing, and informational. For each, give the evidence
            (names, ids, values), why it matters, and the suggested change with the tool that would make it.
            End with what could not be checked and why.
            """;
    }

    /// <summary>Explains why a client can or cannot do something.</summary>
    /// <param name="client">The person, by nickname, unique identity or id.</param>
    /// <param name="action">What they try to do.</param>
    /// <param name="channel">Where they try it.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <returns>The instructions.</returns>
    [McpServerPrompt(Name = "explain-user-permissions", Title = "Explain a user's permissions")]
    [Description("Finds out why a person can or cannot do something on a TeamSpeak server, such as talk, " +
                 "join a channel or upload a file, by tracing the permission through every group and " +
                 "assignment. Changes nothing.")]
    public static string ExplainUserPermissions(
        [Description("The person: a nickname, a unique identity, or a database or session id.")] string client,
        [Description("What they try to do, in plain words, for example 'talk in the Lobby' or 'upload files'.")] string action,
        [Description("The channel it happens in, by name or id. Omit it to use the channel they are in.")] string? channel = null,
        [Description(ProfileArgument)] string? profile = null,
        [Description(VirtualServerArgument)] string? virtualServerId = null)
    {
        var who = Required(client, nameof(client));
        var what = Required(action, nameof(action));
        var target = Target(profile, virtualServerId);
        var where = string.IsNullOrWhiteSpace(channel)
            ? "in the channel they are in (or the default channel if they are offline)"
            : $"in the channel \"{channel.Trim()}\"";

        return $"""
            Explain why "{who}" can or cannot {what} {where}, on {target.Name}. {target.Calls}

            This is a read-only investigation. Do not change permissions or groups; suggest the change instead.

            1. Identify the person. Permissions attach to the permanent database id, not the session.
               - A nickname: ts_client_find if they are online, otherwise ts_clientdb_find.
               - A unique identity or an id: ts_client_resolve.
               If several identities match, list them and ask which one is meant before going on.
            2. Identify the channel, if one was named: ts_channel_find by name, or ts_channel_info by id.
            3. Find the permissions involved. Use ts_perm_list with a search term from the action (for example
               'talk', 'join', 'upload', 'kick'). Most abilities in TeamSpeak compare a power the person has
               with a needed power the target requires, such as talk power against the channel's needed talk
               power. Look up both sides.
            4. For each permission, run ts_perm_effective with the database id, the permission name and the
               channel. It shows every assignment, which one decided, and whether a skip flag or
               b_client_skip_channelgroup_permissions kept the channel values out.
            5. For the needed side, read what the channel requires with ts_channel_info or
               ts_perm_assigned on the channel, and the person's groups with ts_client_groups.

            Answer in this order: a one-sentence verdict; the comparison that decides it, with the numbers; the
            chain of assignments behind each number, naming groups and channels; and the smallest change that
            would give the intended result, naming the tool (for example ts_perm_set or
            ts_servergroup_membership) without calling it. Mention side effects, such as other members of a
            group gaining the same ability.
            """;
    }

    /// <summary>Proposes a tidier channel tree.</summary>
    /// <param name="goal">What the tidy-up should achieve.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <returns>The instructions.</returns>
    [McpServerPrompt(Name = "cleanup-channel-tree", Title = "Clean up the channel tree")]
    [Description("Reviews a TeamSpeak channel tree for empty, duplicate, misplaced and badly named channels, " +
                 "proposes a cleanup plan, and carries it out only after the plan is confirmed.")]
    public static string CleanupChannelTree(
        [Description("What the cleanup should achieve, for example 'remove old game channels'. Omit it for a general tidy-up.")] string? goal = null,
        [Description(ProfileArgument)] string? profile = null,
        [Description(VirtualServerArgument)] string? virtualServerId = null)
    {
        var target = Target(profile, virtualServerId);
        var aim = string.IsNullOrWhiteSpace(goal)
            ? "a clearer, smaller channel tree"
            : goal.Trim();

        return $"""
            Help clean up the channel tree of {target.Name}. The aim: {aim}. {target.Calls}

            Phase 1, read only:
            1. ts_channel_list with tree=true for the whole structure, and ts_client_list for who is where.
            2. Look for channels that are empty with no sub-channels in use, near-duplicate names, sub-channels
               that belong under another parent, inconsistent naming, and temporary or semi-permanent channels
               that should be permanent or gone. Use ts_channel_info for details such as passwords, slot limits
               and needed talk power.
            3. Before proposing to delete a channel, check ts_file_list for files stored in it: deleting a
               channel deletes its files and every sub-channel with them.

            Phase 2, the plan: present a table of proposed changes, one row per change, with the channel, the
            change (rename, move, edit, delete), the reason, and the tool (ts_channel_edit, ts_channel_move or
            ts_channel_delete). Put deletions last and mark them clearly. Never touch the default channel.

            Stop after the plan and ask which changes to make. Do not change anything before the person
            confirms. Then carry out only the confirmed rows, one at a time, and report each result. Renames
            and moves need Write; deletions need Destructive. If a change is refused for its safety level, stop
            and say which level the profile would need. A channel with people in it is only deleted if the
            person explicitly agrees to move them out.
            """;
    }

    /// <summary>Sets up a new member.</summary>
    /// <param name="member">The new member.</param>
    /// <param name="role">The group they should join.</param>
    /// <param name="channel">A channel they should get rights in.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <returns>The instructions.</returns>
    [McpServerPrompt(Name = "onboard-new-member", Title = "Onboard a new member")]
    [Description("Gives a new member of a TeamSpeak community their server group and, optionally, a channel " +
                 "group in their team's channel, or a privilege key if they have never connected. Asks " +
                 "before every change.")]
    public static string OnboardNewMember(
        [Description("The new member: a nickname, a unique identity or an id.")] string member,
        [Description("The server group they should join, for example 'Member' or 'Moderator'.")] string? role = null,
        [Description("A channel they should get a channel group in, by name or id.")] string? channel = null,
        [Description(ProfileArgument)] string? profile = null,
        [Description(VirtualServerArgument)] string? virtualServerId = null)
    {
        var who = Required(member, nameof(member));
        var target = Target(profile, virtualServerId);
        var group = string.IsNullOrWhiteSpace(role)
            ? "Ask which server group they should join, after listing the groups."
            : $"They should join the server group \"{role.Trim()}\".";
        var channelStep = string.IsNullOrWhiteSpace(channel)
            ? "No channel was named, so skip channel groups unless the person asks for one."
            : $"They should get a channel group in the channel \"{channel.Trim()}\"; ask which one if that is not clear.";

        return $"""
            Onboard "{who}" as a new member of {target.Name}. {target.Calls}

            {group} {channelStep}

            1. Find the identity: ts_client_find if they are online, ts_clientdb_find otherwise, or
               ts_client_resolve for a unique identity or id. If nobody matches, they have never connected;
               offer a privilege key instead (step 4). If several match, ask which one.
            2. Look up the groups: ts_servergroup_list, and ts_channelgroup_list plus ts_channel_find if a
               channel is involved. Check with ts_client_groups what they already have, so nothing is added
               twice.
            3. Present the plan: the identity (nickname and database id), each group to add, and the tool for
               each (ts_servergroup_membership, ts_client_channelgroup_set). Wait for confirmation.
            4. Only if they have never connected: a privilege key made with ts_token_manage (action add) puts
               whoever redeems it into the group. It needs Destructive, because anyone holding the key gets the
               access. Create one only when the person confirms, and remind them to hand it over privately.
            5. After confirmation, make the changes one at a time and report each result. Group changes need
               Write. If they are online, offer a private welcome message with ts_message_send; if not, an
               offline message with ts_offline_message.

            Finish by confirming the result with ts_client_groups. Never add anyone to an admin group without
            the person explicitly naming that group.
            """;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new McpException($"The prompt needs '{name}'.")
            : value.Trim();

    private static (string Name, string Calls) Target(string? profile, string? virtualServerId)
    {
        var hasProfile = !string.IsNullOrWhiteSpace(profile);
        int? server = null;

        if (!string.IsNullOrWhiteSpace(virtualServerId))
        {
            server = int.TryParse(virtualServerId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : throw new McpException($"'virtualServerId' must be a positive whole number, not '{virtualServerId}'.");
        }

        var name = (hasProfile, server) switch
        {
            (true, { } id) => $"virtual server {id} of the TeamSpeak profile '{profile!.Trim()}'",
            (true, null) => $"the default virtual server of the TeamSpeak profile '{profile!.Trim()}'",
            (false, { } id) => $"virtual server {id}",
            _ => "the default virtual server",
        };

        var arguments = new List<string>();
        if (hasProfile)
        {
            arguments.Add($"profile '{profile!.Trim()}'");
        }

        if (server is { } serverId)
        {
            arguments.Add($"virtualServerId {serverId}");
        }

        var calls = arguments.Count == 0
            ? "If several profiles are configured, call ts_profiles_list first and ask which one is meant."
            : $"Pass {string.Join(" and ", arguments)} to every tool call that takes them.";

        return (name, calls);
    }
}