using System.Globalization;

using ModelContextProtocol;

using TeamSpeak.Mcp.Safety;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>What a confirmation must name.</summary>
/// <param name="What">The kind of target, such as <c>channel</c>, used in the refusal.</param>
/// <param name="Name">Its current name, read from the server.</param>
internal sealed record ConfirmationTarget(string What, string Name);

/// <summary>
/// Reads the current name of what an irreversible command would remove, so the caller has to repeat it.
/// </summary>
/// <remarks>
/// <para>
/// One place for the dedicated tools and <c>ts_query_raw</c> alike, so the raw tool cannot become the
/// way around a confirmation. The name is read at the level of the command itself: a profile that may
/// not delete learns nothing and sends nothing.
/// </para>
/// <para>
/// A mistyped id is the error this prevents. The model has to have read the name that belongs to the
/// id it is about to delete, rather than acting on an id it guessed or mixed up.
/// </para>
/// </remarks>
/// <param name="executor">The shared path to the server.</param>
internal sealed class DeletionTargets(QueryExecutor executor)
{
    /// <summary>Refuses the command unless <paramref name="confirmName"/> repeats its target's name.</summary>
    /// <param name="action">The tool, named in refusals.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="command">The command about to be sent.</param>
    /// <param name="confirmName">The name the caller gave.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The target, or <see langword="null"/> for a command that needs no confirmation.</returns>
    /// <exception cref="McpException">Thrown when the name is missing or wrong, or cannot be read.</exception>
    public async Task<ConfirmationTarget?> ConfirmAsync(
        string action,
        string? profile,
        QueryCommand command,
        string? confirmName,
        CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(action, profile, command, cancellationToken).ConfigureAwait(false);
        if (target is not null)
        {
            RequireConfirmation(confirmName, target.Name, target.What);
        }

        return target;
    }

    /// <summary>Gets a value indicating whether a command removes something only a confirmation may remove.</summary>
    /// <param name="command">The command.</param>
    /// <returns><see langword="true"/> when <see cref="ConfirmAsync"/> would ask for a name.</returns>
    public static bool NeedsConfirmation(QueryCommand command) =>
        command.Name.ToLowerInvariant() switch
        {
            "channeldelete" or "servergroupdel" or "channelgroupdel" or "clientdbdelete" or "querylogindel"
                or "apikeydel" or "ftdeletefile" or "serverdelete" or "serversnapshotdeploy" or "permreset" => true,
            "ftinitupload" => Parameter(command, "overwrite") == "1",
            _ => false,
        };

    private async Task<ConfirmationTarget?> ResolveAsync(string action, string? profile, QueryCommand command, CancellationToken cancellationToken)
    {
        if (!NeedsConfirmation(command))
        {
            return null;
        }

        var level = CommandCatalog.RequiredLevel(command);
        var server = command.VirtualServerId;

        async Task<IReadOnlyList<QueryRecord>> Read(string name, Dictionary<string, string>? parameters = null) =>
            await executor.RunAsync(action, level, profile, new QueryCommand(name, parameters, VirtualServerId: server), cancellationToken)
                .ConfigureAwait(false);

        switch (command.Name.ToLowerInvariant())
        {
            case "channeldelete":
                return new("channel", await ChannelNameAsync(Required(command, "cid")).ConfigureAwait(false));

            case "servergroupdel":
                return new("server group", Named(await Read("servergrouplist").ConfigureAwait(false), "sgid", Required(command, "sgid"), "name", "server group"));

            case "channelgroupdel":
                return new("channel group", Named(await Read("channelgrouplist").ConfigureAwait(false), "cgid", Required(command, "cgid"), "name", "channel group"));

            case "clientdbdelete":
                return new("identity", Single(await Read("clientdbinfo", new() { ["cldbid"] = Required(command, "cldbid") }).ConfigureAwait(false), "client_nickname", "identity"));

            case "querylogindel":
                return new("query login", Named(await Read("queryloginlist").ConfigureAwait(false), "cldbid", Required(command, "cldbid"), "client_login_name", "query login"));

            case "apikeydel":
                return new("API key's owner", await ApiKeyOwnerAsync(Required(command, "id")).ConfigureAwait(false));

            case "ftdeletefile":
                return new("channel", await ChannelNameAsync(Required(command, "cid")).ConfigureAwait(false));

            case "ftinitupload":
                var name = Required(command, "name");
                var info = new Dictionary<string, string> { ["cid"] = Required(command, "cid"), ["name"] = name };
                if (Parameter(command, "cpw") is { } password)
                {
                    info["cpw"] = password;
                }

                // Overwriting a file that does not exist replaces nothing and needs no confirmation.
                var stored = await executor.RunSearchAsync(
                    action, level, profile, new QueryCommand("ftgetfileinfo", info, VirtualServerId: server), QueryErrorCode.FileNotFound, cancellationToken)
                    .ConfigureAwait(false);
                return stored.Count == 0 ? null : new("file", name.TrimEnd('/').Split('/')[^1]);

            case "serverdelete":
                return new("virtual server", Named(await Read("serverlist").ConfigureAwait(false), "virtualserver_id", Required(command, "sid"), "virtualserver_name", "virtual server"));

            default:
                // serversnapshotdeploy and permreset act on the selected virtual server.
                return new("virtual server", Single(await Read("serverinfo").ConfigureAwait(false), "virtualserver_name", "virtual server"));
        }

        async Task<string> ChannelNameAsync(string channelId)
        {
            // Channel 0 holds the virtual server's icons and avatars, and has no name of its own.
            return channelId == "0"
                ? Single(await Read("serverinfo").ConfigureAwait(false), "virtualserver_name", "virtual server")
                : Single(await Read("channelinfo", new() { ["cid"] = channelId }).ConfigureAwait(false), "channel_name", "channel");
        }

        async Task<string> ApiKeyOwnerAsync(string keyId)
        {
            var keys = await Read("apikeylist", new() { ["cldbid"] = "*" }).ConfigureAwait(false);
            var key = keys.FirstOrDefault(record => record.GetString("id") == keyId)
                ?? throw new McpException($"There is no API key {keyId}. ts_apikey_list shows the ids.");
            var owner = key.GetString("cldbid");

            // Keys of this query login belong to an identity outside the virtual server's database.
            var self = await Read("whoami").ConfigureAwait(false);
            if (self.Count > 0 && self[0].GetString("client_database_id") == owner)
            {
                return self[0].GetString("client_login_name");
            }

            return Single(await Read("clientdbinfo", new() { ["cldbid"] = owner }).ConfigureAwait(false), "client_nickname", "identity");
        }
    }

    private static string Required(QueryCommand command, string key) =>
        Parameter(command, key)
        ?? throw new McpException($"'{command.Name}' needs the parameter '{key}' to name what it removes, and it is missing. Nothing was sent.");

    private static string? Parameter(QueryCommand command, string key) =>
        command.Parameters?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();

    private static string Named(IReadOnlyList<QueryRecord> records, string idField, string id, string nameField, string what) =>
        records.FirstOrDefault(record => string.Equals(record.GetString(idField), id, StringComparison.Ordinal))?.GetString(nameField)
            ?? throw new McpException($"There is no {what} {id}, so there is nothing to confirm. Nothing was sent.");

    private static string Single(IReadOnlyList<QueryRecord> records, string nameField, string what) =>
        records.Count > 0
            ? records[0].GetString(nameField)
            : throw new McpException(string.Create(CultureInfo.InvariantCulture, $"The {what} could not be read, so there is nothing to confirm. Nothing was sent."));
}