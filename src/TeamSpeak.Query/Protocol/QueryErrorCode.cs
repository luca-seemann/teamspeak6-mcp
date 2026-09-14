namespace TeamSpeak.Query.Protocol;

/// <summary>
/// ServerQuery status codes this client reacts to, as opposed to merely reports.
/// </summary>
/// <remarks>
/// The server defines many more; only the ones that change control flow are named here. All values
/// were observed on a live 6.0.0-beta12.1 server.
/// </remarks>
public static class QueryErrorCode
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>
    /// The client is sending commands too quickly. The server closes the request and puts the wait
    /// time in the extra message, for example <c>please wait 1 seconds</c>.
    /// </summary>
    /// <remarks>
    /// Continuing to send while this is being returned escalates to an IP-level block that takes
    /// both the SSH and the HTTP interface down for minutes, so this code must be honoured by
    /// waiting rather than retried immediately.
    /// </remarks>
    public const int Flooding = 524;

    /// <summary>The command name is not known to the server. Sent as <c>invalid parameter</c>.</summary>
    public const int UnknownCommand = 1538;

    /// <summary>A required parameter was missing.</summary>
    public const int ParameterNotFound = 1539;

    /// <summary>
    /// A value is longer than the server accepts. Sent as <c>invalid parameter size</c>.
    /// </summary>
    /// <remarks>
    /// Observed for a 31-character channel group name, where 29 characters were accepted, and for
    /// a 23-character query login name. The exact limits are not documented.
    /// </remarks>
    public const int InvalidParameterSize = 1541;

    /// <summary>
    /// A required parameter was missing or empty. Sent as <c>missing required parameter</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ParameterNotFound"/>: <c>channelfind pattern=</c> with an empty value
    /// returns this, so an empty string is not a valid "match anything" pattern.
    /// </remarks>
    public const int MissingRequiredParameter = 1542;

    /// <summary>
    /// The query succeeded but matched nothing. Routine for list commands on a fresh server, and
    /// should surface as an empty result rather than as a failure.
    /// </summary>
    public const int EmptyResultSet = 1281;

    /// <summary>
    /// The command is outside what the presented API key is allowed to do.
    /// </summary>
    /// <remarks>
    /// Sent as <c>out of scope</c> with <c>command not in api key scope</c>. Notably this is what
    /// <c>servernotifyregister</c> returns over the WebQuery even with a <c>manage</c> key.
    /// </remarks>
    public const int OutOfScope = 5120;

    /// <summary>The WebQuery request carried no <c>x-api-key</c> header.</summary>
    public const int ApiKeyMissing = 5124;

    /// <summary>The WebQuery request carried an <c>x-api-key</c> header the server rejected.</summary>
    public const int ApiKeyInvalid = 5122;
}