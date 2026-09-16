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

    /// <summary>
    /// A scoped command named a virtual server the session is not validly on. Sent as
    /// <c>invalid serverID</c>.
    /// </summary>
    /// <remarks>
    /// Observed after a virtual server was stopped and started: a session that had selected it keeps a
    /// stale selection, and its next scoped command is refused with this until a fresh <c>use</c> is
    /// sent. The transport reacts by forgetting the selection, re-selecting and retrying the command
    /// once.
    /// </remarks>
    public const int InvalidServerId = 1024;

    /// <summary>
    /// A client was told to move into the channel it is already in. Sent as
    /// <c>already member of channel</c>; harmless, so callers treat it as success.
    /// </summary>
    public const int AlreadyMemberOfChannel = 770;

    /// <summary>A client id names no client. Sent as <c>invalid clientID</c>.</summary>
    /// <remarks>
    /// Also what <c>clientfind</c> answers when no nickname matches, over both interfaces, where
    /// <c>clientdbfind</c> answers <see cref="EmptyResultSet"/>. Measured on 6.0.0-beta12.1.
    /// </remarks>
    public const int InvalidClientId = 512;

    /// <summary>A channel id names no channel. Sent as <c>invalid channelID</c>.</summary>
    /// <remarks>
    /// Also what <c>channelfind</c> answers when no channel name matches, over both interfaces.
    /// Measured on 6.0.0-beta12.1.
    /// </remarks>
    public const int InvalidChannelId = 768;

    /// <summary>
    /// The channel password was missing or wrong. Sent as <c>invalid channel password</c>.
    /// </summary>
    /// <remarks>
    /// Observed for file commands by a query login without the permission to skip channel passwords;
    /// <c>serveradmin</c> is let in with any password. <c>ftinitupload</c> and <c>ftinitdownload</c>
    /// report it inside the record.
    /// </remarks>
    public const int InvalidChannelPassword = 781;

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
    /// A file command named something that is not a file. Sent as <c>invalid file name</c>.
    /// </summary>
    /// <remarks>Observed for <c>ftgetfileinfo</c> on a directory.</remarks>
    public const int InvalidFileName = 2048;

    /// <summary>
    /// A file or directory by that name already exists. Sent as <c>file already exists</c>.
    /// </summary>
    /// <remarks>
    /// Observed for <c>ftcreatedir</c> on an existing directory, and for <c>ftinitupload
    /// overwrite=0</c>, which reports it inside the record with <c>error id=0</c>.
    /// </remarks>
    public const int FileAlreadyExists = 2050;

    /// <summary>No file by that name. Sent as <c>file not found</c>.</summary>
    /// <remarks><c>ftinitdownload</c> reports it inside the record with <c>error id=0</c>.</remarks>
    public const int FileNotFound = 2051;

    /// <summary>
    /// The path does not exist, or a directory on the way to it is missing. Sent as
    /// <c>invalid file path</c>.
    /// </summary>
    /// <remarks>
    /// Observed for <c>ftdeletefile</c> on a missing name, and for <c>ftinitupload</c> into a missing
    /// directory, which reports it inside the record.
    /// </remarks>
    public const int InvalidFilePath = 2054;

    /// <summary>
    /// An upload asked to both replace and continue a file. Sent as <c>overwrite excludes resume</c>.
    /// </summary>
    public const int OverwriteExcludesResume = 2056;

    /// <summary>
    /// A command asked for something a default group does not have. Sent as
    /// <c>access to default group is forbidden</c>.
    /// </summary>
    /// <remarks>
    /// Observed for <c>servergroupclientlist</c> on the virtual server's default server group: clients
    /// belong to it by having no other group, so the server keeps no member list for it.
    /// </remarks>
    public const int AccessToDefaultGroupForbidden = 2564;

    /// <summary>
    /// The query login lacks a permission. Sent as <c>insufficient client permissions</c>, naming it.
    /// </summary>
    /// <remarks>
    /// Observed for <c>ftinitupload</c> by a login in the Guest group, inside the record with
    /// <c>failed_permid</c>: <c>failed on i_ft_needed_file_upload_power</c>.
    /// </remarks>
    public const int InsufficientPermissions = 2568;

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