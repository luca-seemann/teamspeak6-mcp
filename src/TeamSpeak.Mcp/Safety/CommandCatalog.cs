using System.Collections.Frozen;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Safety;

/// <summary>
/// The safety level every captured ServerQuery command needs when issued through
/// <c>ts_query_raw</c>.
/// </summary>
/// <remarks>
/// <para>
/// Covers all 143 commands in the index of <c>reference/serverquery-6.0.0-beta13.txt</c>; a test
/// holds the two in step. A command this catalog does not know, for example one added by a later server version,
/// needs <see cref="SafetyLevel.Destructive"/> until someone classifies it.
/// </para>
/// <para>
/// Two judgements go beyond "does it change state". Commands that hand out credentials or reveal
/// them, such as privilege keys, temporary passwords, snapshots and HTTP file transfer tokens, are never
/// <see cref="SafetyLevel.ReadOnly"/>, because a leaked privilege key is as damaging as a changed
/// setting. The key <c>ftinitdownload</c> returns is not such a credential: it opens one download of
/// one file and nothing else. And commands that grant server-wide power, mint or remove credentials,
/// or cut people off are <see cref="SafetyLevel.Destructive"/> even where they are technically
/// reversible: adding someone to a server group, changing a server group's or a client's permissions,
/// changing the default groups, and copying a group over an existing one. Otherwise a
/// <see cref="SafetyLevel.Write"/> profile could make anyone a Server Admin. Channel and channel group
/// permissions, removing a member from a group, and deleting a privilege key stay
/// <see cref="SafetyLevel.Write"/>: they act within a channel, or take power away.
/// </para>
/// <para>
/// Some commands escalate only with certain parameters, so <see cref="RequiredLevel(QueryCommand)"/>
/// looks at those too.
/// </para>
/// </remarks>
public static class CommandCatalog
{
    private static readonly string[] ReadOnlyCommands =
    [
        "apikeylist", "banfind", "banlist", "bindinglist", "channelclientpermlist", "channelfind",
        "channelgroupclientlist", "channelgrouplist", "channelgrouppermlist", "channelinfo", "channellist",
        "channelpermlist", "clientdbfind", "clientdbinfo", "clientdblist", "clientfind",
        "clientgetdbidfromuid", "clientgetids", "clientgetnamefromdbid", "clientgetnamefromuid",
        "clientgetuidfromclid", "clientinfo", "clientlist", "clientpermlist", "complainlist", "custominfo",
        "customsearch", "ftgetfileinfo", "ftgetfilelist", "ftlist", "help", "hostinfo", "instanceinfo",
        "logview", "messageget", "messagelist", "permfind", "permget", "permidgetbyname", "permissionlist",
        "permoverview", "queryloginlist", "servergroupclientlist", "servergrouplist", "servergrouppermlist",
        "servergroupsbyclientid", "serveridgetbyport", "serverinfo", "serverlist",
        "serverrequestconnectioninfo", "version", "whoami",

        // Reads a stored file. The key it returns opens that one download and nothing else, and a
        // key never used lapses after a few minutes, so it changes no more than reading a message.
        "ftinitdownload",
    ];

    private static readonly string[] WriteCommands =
    [
        // Routine, reversible changes.
        "bandel", "channeladdperm", "channelclientaddperm", "channelclientdelperm", "channelcreate",
        "channeldelperm", "channeledit", "channelgroupadd", "channelgroupaddperm", "channelgroupcopy",
        "channelgroupdelperm", "channelgrouprename", "channelmove", "clientdbedit",
        "clientedit", "clientmove", "clientpoke", "clientupdate", "complainadd",
        "complaindel", "customdelete", "customset", "ftcreatedir", "ftinitupload",
        "ftrenamefile", "ftstop", "gm", "logadd", "messageadd", "messagedel", "messageupdateflag",
        "privilegekeydelete", "sendtextmessage", "serveredit", "servergroupadd",
        "servergroupcopy", "servergroupdelclient",
        "servergrouprename", "serverstart", "servertemppassworddel",
        "setclientchannelgroup", "tokendelete",

        // Read-only on the server, but they reveal credentials or everything at once.
        "ftgetchannelfilehttptoken", "privilegekeylist", "serversnapshotcreate", "servertemppasswordlist",
        "tokenlist",
    ];

    private static readonly string[] DestructiveCommands =
    [
        // Destroy state.
        "bandelall", "channeldelete", "channelgroupdel", "clientdbdelete", "complaindelall", "ftdeletefile",
        "servergroupdel", "serverdelete", "servergroupautodelperm", "servergroupautoaddperm",

        // Deploys without any permission check and replaces the server's configuration.
        "serversnapshotdeploy",

        // Deletes the virtual server if it fails part-way.
        "permreset",

        // Cut people off, or stop the service.
        "banadd", "banclient", "clientkick", "serverprocessstop", "serverstop", "instanceedit",

        // Grant or take server-wide power: group membership and the permissions of server groups and
        // clients decide what everyone may do. Removing a "needed power" escalates as surely as a grant.
        "servergroupaddclient", "servergroupaddperm", "servergroupdelperm", "clientaddperm", "clientdelperm",

        // Mint, change or remove access. servercreate belongs here because its answer is a privilege
        // key with full control of the new server.
        "servercreate", "apikeyadd", "apikeydel", "authenticationtoken", "chatlogintoken", "clientsetserverquerylogin",
        "licensesignmessage", "privilegekeyadd", "privilegekeyuse", "queryloginadd", "querylogindel",
        "servertemppasswordadd", "tokenadd", "tokenuse",
    ];

    /// <summary>
    /// Commands that change the state of the shared session itself rather than the server.
    /// </summary>
    /// <remarks>
    /// Every tool call shares one connection per profile. <c>logout</c> or <c>quit</c> would end it
    /// for everyone, <c>login</c> would switch its identity, <c>use</c> is already handled by each
    /// command's virtual server, and notification registration belongs to the events feature.
    /// </remarks>
    private static readonly FrozenSet<string> SessionControlCommands = new[]
    {
        "login", "logout", "quit", "use", "servernotifyregister", "servernotifyunregister",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, SafetyLevel> Levels =
        ReadOnlyCommands.Select(name => KeyValuePair.Create(name, SafetyLevel.ReadOnly))
            .Concat(WriteCommands.Select(name => KeyValuePair.Create(name, SafetyLevel.Write)))
            .Concat(DestructiveCommands.Select(name => KeyValuePair.Create(name, SafetyLevel.Destructive)))
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every command this catalog classifies, including session control commands.</summary>
    public static IEnumerable<string> KnownCommands => Levels.Keys.Concat(SessionControlCommands);

    /// <summary>Gets the level a command needs.</summary>
    /// <param name="commandName">The command name.</param>
    /// <returns>Its level, or <see cref="SafetyLevel.Destructive"/> for a command not in the catalog.</returns>
    public static SafetyLevel RequiredLevel(string commandName)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        return Levels.TryGetValue(commandName, out var level) ? level : SafetyLevel.Destructive;
    }

    /// <summary>Gets the level a command needs, taking the parameters that make it escalate into account.</summary>
    /// <param name="command">The command with its parameters.</param>
    /// <returns>
    /// The level of its name, raised to <see cref="SafetyLevel.Destructive"/> for <c>serveredit</c>
    /// changing a default group, a group copy over an existing group, and an upload that overwrites
    /// or continues a stored file.
    /// </returns>
    public static SafetyLevel RequiredLevel(QueryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var level = RequiredLevel(command.Name);
        var parameters = command.Parameters ?? new Dictionary<string, string>();

        var escalates = command.Name.ToLowerInvariant() switch
        {
            // Newcomers, or everyone given channel admin, would get the chosen group's power.
            "serveredit" => parameters.Keys.Any(key =>
                key.StartsWith("virtualserver_default_", StringComparison.OrdinalIgnoreCase)
                && key.EndsWith("group", StringComparison.OrdinalIgnoreCase)),

            // A target of 0 creates a new group; any other id replaces an existing group's permissions.
            "servergroupcopy" => IsNonZero(parameters, "tsgid"),
            "channelgroupcopy" => IsNonZero(parameters, "tcgid"),

            // Replacing or extending a stored file, as ts_file_upload with overwrite or resume.
            "ftinitupload" => IsOne(parameters, "overwrite") || IsOne(parameters, "resume"),

            _ => false,
        };

        return escalates ? SafetyLevel.Destructive : level;
    }

    private static bool IsNonZero(IReadOnlyDictionary<string, string> parameters, string key) =>
        Value(parameters, key) is { } value && value.Trim() != "0";

    private static bool IsOne(IReadOnlyDictionary<string, string> parameters, string key) =>
        Value(parameters, key)?.Trim() == "1";

    private static string? Value(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Gets a value indicating whether a command controls the shared session.</summary>
    /// <param name="commandName">The command name.</param>
    /// <returns><see langword="true"/> when the command must never be issued by a tool call.</returns>
    public static bool IsSessionControl(string commandName)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        return SessionControlCommands.Contains(commandName);
    }
}