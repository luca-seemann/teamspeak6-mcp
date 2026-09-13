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
    /// adjusting group membership.
    /// </summary>
    Write,

    /// <summary>
    /// Everything, including actions that destroy state or cut people off: deleting a virtual
    /// server, deploying a snapshot over one, resetting permissions, banning.
    /// </summary>
    Destructive,
}