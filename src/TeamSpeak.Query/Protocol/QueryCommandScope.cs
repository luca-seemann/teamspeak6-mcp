namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Tells commands that address the whole instance apart from commands that address one virtual
/// server.
/// </summary>
/// <remarks>
/// <para>
/// Both transports need the distinction, for different reasons. The WebQuery requests an
/// instance-wide command as <c>/version</c> and everything else as <c>/{sid}/{command}</c>. The SSH
/// transport sends <c>use</c> before a scoped command when a different virtual server is selected,
/// and must not do so before an instance-wide one — <c>serverstart</c> on a stopped server would
/// otherwise fail on the <c>use</c> it never needed.
/// </para>
/// <para>
/// Everything not listed is treated as scoped, which is the right default: of the 141 captured
/// commands, only these operate above a single virtual server.
/// </para>
/// </remarks>
public static class QueryCommandScope
{
    private static readonly HashSet<string> InstanceWideCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Instance information and configuration.
        "version",
        "hostinfo",
        "instanceinfo",
        "instanceedit",
        "bindinglist",

        // Reaches every client on every virtual server, so it must not depend on one being selectable.
        "gm",

        // Virtual server lifecycle, which takes the server id as a parameter.
        "serverlist",
        "servercreate",
        "serverdelete",
        "serverstart",
        "serverstop",
        "serveridgetbyport",
        "serverprocessstop",

        // Session control.
        "use",
        "login",
        "logout",
        "quit",
        "whoami",
        "help",

        // Not the API key commands: a key carries the virtual server it was created on, so they need
        // that server selected rather than whatever an earlier command happened to leave selected.
    };

    /// <summary>Gets a value indicating whether a command addresses the whole instance.</summary>
    /// <param name="commandName">The command name, for example <c>serverlist</c>.</param>
    /// <returns><see langword="true"/> for an instance-wide command.</returns>
    public static bool IsInstanceWide(string commandName)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        return InstanceWideCommands.Contains(commandName);
    }
}