using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.Resources;

/// <summary>
/// Read-only snapshots of server state, for clients that attach context rather than call tools.
/// </summary>
/// <remarks>
/// Each resource is the same data a tool returns, serialised as JSON. They go through the same
/// executor, so they obey the same safety policy and share the same connection.
/// </remarks>
/// <param name="executor">The shared path to the server.</param>
/// <param name="permissionNames">The per-profile permission name cache.</param>
[McpServerResourceType]
public sealed class ServerResources(QueryExecutor executor, PermissionNameCache permissionNames)
{
    /// <summary>The configured profiles.</summary>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://profiles", Name = "profiles", Title = "Configured TeamSpeak profiles",
        MimeType = "application/json")]
    [Description("The TeamSpeak servers this MCP server administers, with the interface and safety level of each.")]
    public string Profiles() => Json(new MetaTools(executor).ListProfiles());

    /// <summary>A virtual server's properties.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://{profile}/{virtualServerId}/info", Name = "virtual-server-info",
        Title = "Virtual server properties", MimeType = "application/json")]
    [Description("Every property of one virtual server, by its ServerQuery field name." + ToolDescriptions.UserWrittenText)]
    public async Task<string> InfoAsync(string profile, int virtualServerId, CancellationToken cancellationToken) =>
        Json(await new VirtualServerTools(executor).VirtualServerInfoAsync(virtualServerId, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>A virtual server's channel tree.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://{profile}/{virtualServerId}/channels", Name = "channels",
        Title = "Channel tree", MimeType = "application/json")]
    [Description("The channels of one virtual server as a tree in display order." + ToolDescriptions.UserWrittenText)]
    public async Task<string> ChannelsAsync(string profile, int virtualServerId, CancellationToken cancellationToken) =>
        Json(await new ChannelTools(executor).ListChannelsAsync(true, virtualServerId, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>The people connected to a virtual server.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://{profile}/{virtualServerId}/clients", Name = "clients",
        Title = "Connected clients", MimeType = "application/json")]
    [Description("The people connected to one virtual server, with channel, groups and away state. Query clients are left out." + ToolDescriptions.UserWrittenText)]
    public async Task<string> ClientsAsync(string profile, int virtualServerId, CancellationToken cancellationToken) =>
        Json(await new ClientTools(executor).ListClientsAsync(false, virtualServerId, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>A virtual server's groups.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://{profile}/{virtualServerId}/groups", Name = "groups",
        Title = "Server and channel groups", MimeType = "application/json")]
    [Description("The server groups and channel groups of one virtual server.")]
    public async Task<string> GroupsAsync(string profile, int virtualServerId, CancellationToken cancellationToken)
    {
        var groups = new GroupTools(executor);
        var serverGroups = await groups.ListServerGroupsAsync(virtualServerId, profile, cancellationToken).ConfigureAwait(false);
        var channelGroups = await groups.ListChannelGroupsAsync(virtualServerId, profile, cancellationToken).ConfigureAwait(false);

        return Json(new GroupsSnapshot(serverGroups.Groups, channelGroups.Groups));
    }

    /// <summary>The permissions a server knows.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>JSON.</returns>
    [McpServerResource(UriTemplate = "ts://{profile}/permissions", Name = "permissions",
        Title = "Permission catalog", MimeType = "application/json")]
    [Description("Every permission the TeamSpeak server knows, with id, name and description.")]
    public async Task<string> PermissionsAsync(string profile, CancellationToken cancellationToken) =>
        Json((await permissionNames.GetAsync(executor, profile, cancellationToken).ConfigureAwait(false)).All);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ResultJson.Options);
}

/// <summary>A virtual server's groups.</summary>
/// <param name="ServerGroups">The server groups.</param>
/// <param name="ChannelGroups">The channel groups.</param>
public sealed record GroupsSnapshot(IReadOnlyList<GroupSummary> ServerGroups, IReadOnlyList<GroupSummary> ChannelGroups);