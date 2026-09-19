using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools about the configuration, the connection and the instance as a whole.</summary>
/// <param name="executor">The shared path to the server.</param>
[McpServerToolType]
public sealed class MetaTools(QueryExecutor executor)
{
    private static string? HostKeyOf(QueryProfile profile, PreferredTransport transport) =>
        transport != PreferredTransport.Ssh
            ? null
            : profile.HostKeyFingerprint is { } pinned
                ? Query.Transport.HostKeyFingerprint.Normalize(pinned)
                : (profile.HostKeyVerifier as Query.Transport.KnownHostsFile)?.Find(profile.Host, profile.SshPort);

    /// <summary>Lists the configured profiles.</summary>
    /// <returns>The profiles, without credentials.</returns>
    [McpServerTool(Name = "ts_profiles_list", Title = "List TeamSpeak profiles",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the TeamSpeak servers this MCP server is configured to administer, with the " +
                 "interface and safety level each uses, the SSH host key fingerprint each server is " +
                 "trusted with, and whether a profile connects as the ServerQuery guest, which can do " +
                 "only what that server grants guests. Does not contact any server. Call this first when " +
                 "several profiles exist and a tool needs a profile name.")]
    public ProfileList ListProfiles()
    {
        var registry = executor.Connections.Profiles;

        return new ProfileList(registry.Names
            .Select(registry.Resolve)
            .Select(profile =>
            {
                var transport = QueryConnectionManager.ResolveTransport(profile);
                return new ProfileSummary(
                    profile.Name,
                    profile.Host,
                    transport == PreferredTransport.Ssh ? "ssh" : "webquery",
                    EventsAvailable: transport == PreferredTransport.Ssh,
                    executor.Safety.LevelFor(profile.Name).ToString(),
                    profile.DefaultVirtualServerId,
                    HostKeyOf(profile, transport),
                    profile.IsGuest);
            })
            .ToList());
    }

    /// <summary>Shows the identity of this server's own query session.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The session's fields.</returns>
    [McpServerTool(Name = "ts_whoami", Title = "Show the query session identity",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows who this MCP server is logged in as on a TeamSpeak server: the query login, " +
                 "its client and database ids, and the virtual server its session currently has " +
                 "selected. Useful for checking that a profile connects and with which account.")]
    public async Task<RecordResult> WhoAmIAsync(
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var records = await executor.RunAsync(
            "ts_whoami", SafetyLevel.ReadOnly, profile, new QueryCommand("whoami"), cancellationToken)
            .ConfigureAwait(false);

        return new RecordResult(records.Count > 0 ? QueryExecutor.ToFields(records[0]) : new Dictionary<string, string>());
    }

    /// <summary>Summarises the server instance.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Version, host statistics, instance settings and bound addresses.</returns>
    [McpServerTool(Name = "ts_instance_info", Title = "Show the server instance",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Summarises a TeamSpeak server instance in one call: the server version and platform, " +
                 "uptime and totals across all virtual servers, instance-wide settings such as the " +
                 "file transfer port and query flood limits, and the IP addresses it listens on." + ToolDescriptions.UserWrittenText)]
    public async Task<InstanceInfo> InstanceInfoAsync(
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        async Task<IReadOnlyList<QueryRecord>> Run(string command) =>
            await executor.RunAsync(
                "ts_instance_info", SafetyLevel.ReadOnly, profile, new QueryCommand(command), cancellationToken)
                .ConfigureAwait(false);

        static IReadOnlyDictionary<string, string> First(IReadOnlyList<QueryRecord> records) =>
            records.Count > 0 ? QueryExecutor.ToFields(records[0]) : new Dictionary<string, string>();

        // Sequential on purpose: they share one paced connection, so parallel calls gain nothing.
        var version = await Run("version").ConfigureAwait(false);
        var host = await Run("hostinfo").ConfigureAwait(false);
        var instance = await Run("instanceinfo").ConfigureAwait(false);
        var bindings = await Run("bindinglist").ConfigureAwait(false);

        return new InstanceInfo(
            First(version),
            First(host),
            First(instance),
            bindings.Select(binding => binding.GetString("ip")).Where(ip => ip.Length > 0).ToList());
    }

    /// <summary>Shows the server's own documentation of a ServerQuery command.</summary>
    /// <param name="command">The command to explain, or <see langword="null"/> for the overview.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The documentation, as the server wrote it.</returns>
    [McpServerTool(Name = "ts_command_help", Title = "Show a ServerQuery command's documentation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Asks the TeamSpeak server for its own documentation of a ServerQuery command: its usage " +
                 "with every parameter, the permissions it checks, a description and an example. Without " +
                 "command it returns the overview of all commands. Use it before ts_query_raw to get command " +
                 "and parameter names right; the answer always matches the version the server runs. Needs a " +
                 "profile that uses SSH, because the WebQuery does not serve help.")]
    public async Task<CommandHelp> CommandHelpAsync(
        [Description("The command to explain, for example 'channeledit'. Omit it for the list of all commands.")]
        string? command = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(command) ? null : command.Trim();
        if (name is not null && !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new McpException(
                $"'{name}' is not a ServerQuery command name. Pass the name alone, for example 'channeledit'.");
        }

        var resolved = executor.ResolveProfile(profile);
        if (QueryConnectionManager.ResolveTransport(resolved) != PreferredTransport.Ssh)
        {
            throw new McpException(
                $"Profile '{resolved.Name}' uses the WebQuery, which does not serve help: it answers 404. " +
                "Give the profile an SSH password; with Transport Auto it then uses SSH. Nothing was sent.");
        }

        var response = await executor.RunForResponseAsync(
            "ts_command_help",
            SafetyLevel.ReadOnly,
            profile,
            new QueryCommand("help", Arguments: name is null ? null : [name]),
            cancellationToken).ConfigureAwait(false);

        return new CommandHelp(name, response.Text);
    }

    /// <summary>Sends any ServerQuery command.</summary>
    /// <param name="command">The command name.</param>
    /// <param name="parameters">Key/value parameters.</param>
    /// <param name="options">Flag options.</param>
    /// <param name="limit">How many records to return.</param>
    /// <param name="confirmName">The name of what a deleting command removes.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records the server returned.</returns>
    [McpServerTool(Name = "ts_query_raw", Title = "Send a raw ServerQuery command",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sends one TeamSpeak ServerQuery command that no dedicated tool covers, and returns " +
                 "the records it produces. Prefer a dedicated tool whenever one exists. The command " +
                 "needs the safety level of what it does: read commands need ReadOnly, changes need " +
                 "Write, and deleting, banning, kicking or handing out access needs Destructive, as " +
                 "does any command this server does not classify. Session commands (use, login, " +
                 "logout, quit, servernotifyregister, servernotifyunregister) are refused because the " +
                 "connection is shared. Commands that delete what a dedicated tool asks a name for " +
                 "(channeldelete, servergroupdel, channelgroupdel, clientdbdelete, querylogindel, apikeydel, " +
                 "ftdeletefile, ftinitupload with overwrite, serverdelete, serversnapshotdeploy, permreset) need " +
                 "the same confirmName here. At most limit records come back; totalRecords says how many the " +
                 "server returned." + ToolDescriptions.UserWrittenText)]
    public async Task<RawQueryResult> QueryRawAsync(
        [Description("The command name alone, for example 'clientdbfind'. Parameters and options go in their own arguments.")]
        string command,
        [Description("Key/value parameters, for example {\"pattern\": \"Alice\"}. Values are escaped automatically.")]
        IReadOnlyDictionary<string, string>? parameters = null,
        [Description("Flag options, for example [\"-uid\"]. The leading dash is optional.")]
        IReadOnlyList<string>? options = null,
        [Description("How many records to return, from 1 to 1000. The command runs in full either way.")] int limit = 100,
        [Description("For a command that deletes something a dedicated tool confirms: the current name of what it deletes.")] string? confirmName = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        var name = command?.Trim() ?? string.Empty;

        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            throw new McpException(
                "Pass only the command name in 'command', for example 'clientdbfind'. " +
                "Parameters belong in 'parameters' and flags in 'options'.");
        }

        if (CommandCatalog.IsSessionControl(name))
        {
            throw new McpException(
                $"'{name}' controls the query session itself, which every tool call shares, so it is " +
                "refused. To address a virtual server, pass virtualServerId instead of sending 'use'.");
        }

        var raw = new QueryCommand(name, parameters, options, virtualServerId);
        var required = CommandCatalog.RequiredLevel(raw);

        // A command known to crash the server is refused before anything is read for its confirmation,
        // and the raw tool must not be the way around the confirmation the dedicated tools ask for.
        // One whose danger depends on what the server says about itself is left to the executor,
        // which has a connection to ask on.
        if (!KnownCrashes.NeedsServerFacts(raw))
        {
            KnownCrashes.Refuse(raw);
        }

        executor.Demand($"ts_query_raw with '{name}'", required, profile);
        await new DeletionTargets(executor).ConfirmAsync($"ts_query_raw with '{name}'", profile, raw, confirmName, cancellationToken).ConfigureAwait(false);
        var records = await executor.RunAsync(
            $"ts_query_raw with '{name}'",
            required,
            profile,
            raw,
            cancellationToken).ConfigureAwait(false);

        return new RawQueryResult(name, required.ToString(), records.Count, records.Take(Math.Clamp(limit, 1, 1000)).Select(QueryExecutor.ToFields).ToList());
    }
}

/// <summary>The configured profiles.</summary>
/// <param name="Profiles">One entry per profile.</param>
public sealed record ProfileList(IReadOnlyList<ProfileSummary> Profiles);

/// <summary>A configured profile, without its credentials.</summary>
/// <param name="Name">The profile name to pass to other tools.</param>
/// <param name="Host">The server host.</param>
/// <param name="Interface">The query interface in use: <c>ssh</c> or <c>webquery</c>.</param>
/// <param name="EventsAvailable">Whether the interface can deliver server events.</param>
/// <param name="Safety">The highest safety level tools may use on this profile.</param>
/// <param name="DefaultVirtualServerId">The virtual server addressed when a tool call names none.</param>
/// <param name="HostKeyFingerprint">
/// The SSH host key the server must present: pinned in configuration, or remembered from the first
/// connection. <see langword="null"/> before the first SSH connection, and for the WebQuery.
/// </param>
/// <param name="Guest">
/// Whether this profile connects without credentials, as the ServerQuery guest. Such a session may
/// do only what the server's <c>Guest Server Query</c> group allows, which is usually next to
/// nothing, so a refusal from it is about the server's permissions, not about this server's safety
/// level.
/// </param>
public sealed record ProfileSummary(
    string Name,
    string Host,
    string Interface,
    bool EventsAvailable,
    string Safety,
    int DefaultVirtualServerId,
    string? HostKeyFingerprint,
    bool Guest);

/// <summary>A summary of a server instance.</summary>
/// <param name="Version">The <c>version</c> fields.</param>
/// <param name="Host">The <c>hostinfo</c> fields: uptime and totals across virtual servers.</param>
/// <param name="Instance">The <c>instanceinfo</c> fields: instance-wide settings.</param>
/// <param name="BoundAddresses">The IP addresses the instance listens on.</param>
public sealed record InstanceInfo(
    IReadOnlyDictionary<string, string> Version,
    IReadOnlyDictionary<string, string> Host,
    IReadOnlyDictionary<string, string> Instance,
    IReadOnlyList<string> BoundAddresses);

/// <summary>A command's documentation, as the server wrote it.</summary>
/// <param name="Command">The command explained, or <see langword="null"/> for the overview of all commands.</param>
/// <param name="Text">The documentation, with its line breaks and indentation.</param>
public sealed record CommandHelp(string? Command, string Text);

/// <summary>The outcome of a raw command.</summary>
/// <param name="Command">The command that was sent.</param>
/// <param name="SafetyLevel">The safety level the command required.</param>
/// <param name="TotalRecords">How many records the server returned; more than listed when the limit cut them off.</param>
/// <param name="Records">The first records, by their ServerQuery field names.</param>
public sealed record RawQueryResult(
    string Command,
    string SafetyLevel,
    int TotalRecords,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records);