namespace TeamSpeak.Mcp.Configuration;

/// <summary>
/// How much damage the configured tools are allowed to do.
/// </summary>
/// <remarks>
/// Administering a voice server is a high-trust job, and a model driving it can misread a request.
/// The server therefore starts read-only and each further level has to be asked for explicitly.
/// Tools above the configured level are refused with an explanation rather than hidden, so the
/// model can tell the user what to change instead of silently doing nothing.
/// </remarks>
public enum SafetyLevel
{
    /// <summary>Only tools that read. The default.</summary>
    ReadOnly,

    /// <summary>
    /// Reading plus changes that are routine and reversible: editing channels, moving clients,
    /// removing someone from a group, channel and channel group permissions. Nothing that hands out
    /// server-wide power.
    /// </summary>
    Write,

    /// <summary>
    /// Everything, including actions that destroy state, cut people off or grant server-wide power:
    /// deleting a virtual server, deploying a snapshot over one, resetting permissions, banning, adding
    /// someone to a server group, changing a server group's or a client's permissions.
    /// </summary>
    Destructive,
}